namespace ZapMQ.Core;

/// <summary>
/// The settings of one queue with the broker defaults already applied.
/// </summary>
internal sealed record QueueSettings(
    TimeSpan Retention,
    TimeSpan EmptyLifetime,
    int DeadLetterLimit,
    TimeSpan DeadLetterMaxAge,
    bool RedeliverUnconfirmed,
    bool Paused);

/// <summary>
/// Something to hand to a consumer once the queue lock is released.
/// </summary>
internal readonly record struct Delivery(Consumer Consumer, BrokerMessage Message, bool IsResponse);

internal sealed class MessageQueue(string name, TimeProvider time, QueueSettings settings, DeadLetterStore deadLetters)
{
    private sealed class Entry
    {
        public required string Id;
        public required string Body;
        public required bool Rpc;
        public required TimeSpan Ttl;
        public required long Born;
        public required DateTimeOffset PublishedAt;
        public string? RequeuedFrom;
        public string? Response;
        public long AnsweredAt;
        // The pushed-to consumer that has not confirmed this message yet.
        public Consumer? Owner;
        // The consumer waiting for the answer of this RPC message.
        public Consumer? ReplyTo;
    }

    private readonly object _gate = new();
    // Publication order, so the head is always the oldest pending message.
    private readonly LinkedList<Entry> _pending = new();
    // Delivered messages that are not finished: being processed by a pushed-to consumer, or RPC
    // messages waiting for the response to be written or collected.
    private readonly Dictionary<string, Entry> _inFlight = new(StringComparer.Ordinal);
    private readonly List<Consumer> _consumers = [];
    private int _nextConsumer;
    private long? _emptySince;
    private long _published, _delivered, _confirmed, _responded, _redelivered, _expired, _notConsumed, _unconfirmed, _dropped, _purged;
    // Who has been publishing here and who has been asking for messages (the 1.x way), with the
    // last time and how often. Consumers that get messages pushed are in _consumers already.
    /// <summary>
    /// For how long the queue remembers who published in it and who asked it for messages.
    /// </summary>
    public static readonly TimeSpan PartyMemory = TimeSpan.FromHours(24);

    private const int PartiesBeforeForgetting = 32;

    private readonly Dictionary<string, (DateTimeOffset Last, long Count)> _publishers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Last, long Count)> _askers = new(StringComparer.Ordinal);

    public string Name { get; } = name;

    /// <summary>
    /// Replaced as a whole when the queue is reconfigured.
    /// </summary>
    public QueueSettings Settings { get; set; } = settings;

    /// <summary>
    /// Set once the broker has let go of this queue. Whoever still holds the instance must fetch
    /// a new one instead of using a queue nobody will read.
    /// </summary>
    public bool Removed { get; private set; }

    public bool TryPublish(string id, string body, bool rpc, TimeSpan ttl, Consumer? replyTo, string? requeuedFrom, string? publisher, List<Delivery> deliveries)
    {
        lock (_gate)
        {
            if (Removed)
                return false;

            Note(_publishers, publisher);

            _pending.AddLast(new Entry
            {
                Id = id,
                Body = body,
                Rpc = rpc,
                Ttl = ttl,
                Born = time.GetTimestamp(),
                PublishedAt = time.GetUtcNow(),
                RequeuedFrom = requeuedFrom,
                ReplyTo = rpc ? replyTo : null
            });
            _emptySince = null;
            _published++;
            Dispatch(deliveries);
            return true;
        }
    }

    /// <summary>
    /// Hands the oldest deliverable message to a caller that asks for it (the 1.x way). Selecting
    /// the message and taking it out of the pending list happen under the same lock, so two
    /// concurrent callers can never receive the same message.
    /// </summary>
    public BrokerMessage? Take(string? asker = null)
    {
        lock (_gate)
        {
            Note(_askers, asker);
            // A paused queue keeps receiving and hands nothing to anybody.
            var node = Settings.Paused ? null : FirstDeliverable();
            if (node is null)
            {
                TrackEmptiness();
                return null;
            }

            var entry = node.Value;
            _pending.Remove(node);
            if (entry.Rpc)
                _inFlight[entry.Id] = entry;

            _delivered++;
            TrackEmptiness();
            return Snapshot(entry);
        }
    }

    public bool Bind(Consumer consumer, List<Delivery> deliveries)
    {
        lock (_gate)
        {
            if (Removed)
                return false;

            if (!_consumers.Contains(consumer))
                _consumers.Add(consumer);
            Dispatch(deliveries);
            return true;
        }
    }

    public void Unbind(Consumer consumer)
    {
        lock (_gate)
        {
            _consumers.Remove(consumer);
            _nextConsumer = 0;
        }
    }

    /// <summary>
    /// Pushes pending messages to free consumers, one consumer after the other.
    /// </summary>
    public void Dispatch(List<Delivery> deliveries)
    {
        lock (_gate)
        {
            while (!Settings.Paused && _consumers.Count > 0 && FirstDeliverable() is { } node)
            {
                var consumer = NextFreeConsumer();
                if (consumer is null)
                    break;

                var entry = node.Value;
                _pending.Remove(node);
                entry.Owner = consumer;
                _inFlight[entry.Id] = entry;
                consumer.Current = (Name, entry.Id);
                consumer.LastQueue = Name;
                _delivered++;
                deliveries.Add(new Delivery(consumer, Snapshot(entry), IsResponse: false));
            }
            TrackEmptiness();
        }
    }

    /// <summary>
    /// The consumer finished the message it was pushed.
    /// </summary>
    public bool Confirm(Consumer consumer, string id)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(id, out var entry) || entry.Owner != consumer)
                return false;

            _inFlight.Remove(id);
            consumer.Current = null;
            _confirmed++;
            TrackEmptiness();
            return true;
        }
    }

    /// <summary>
    /// The consumer finished the RPC message it was pushed and this is its answer.
    /// </summary>
    public bool Respond(Consumer consumer, string id, string response, List<Delivery> deliveries)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(id, out var entry) || entry.Owner != consumer)
                return false;

            consumer.Current = null;
            entry.Owner = null;
            _confirmed++;

            if (!entry.Rpc)
            {
                // Nobody is waiting for an answer: the response only confirms the message.
                _inFlight.Remove(id);
            }
            else
            {
                StoreResponse(entry, response);
                PushResponse(entry, deliveries);
            }
            TrackEmptiness();
            return true;
        }
    }

    /// <summary>
    /// Stores the response of a message taken the 1.x way. A message a pushed-to consumer is
    /// still processing can only be answered by that consumer.
    /// </summary>
    public bool Respond(string id, string response, bool includeUndelivered, List<Delivery> deliveries)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(id, out var entry))
            {
                // Answering a message nobody consumed yet takes it out of the delivery line.
                var node = includeUndelivered ? FindPending(id) : null;
                if (node is null)
                    return false;

                entry = node.Value;
                _pending.Remove(node);
                _inFlight[id] = entry;
            }

            if (entry.Owner is not null)
                return false;
            if (entry.Response is null && IsPastRetention(entry))
                return false;

            StoreResponse(entry, response);
            PushResponse(entry, deliveries);
            TrackEmptiness();
            return true;
        }
    }

    public BrokerMessage? TakeResponse(string id)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(id, out var entry) || entry.Response is null)
                return null;

            _inFlight.Remove(id);
            TrackEmptiness();
            return Snapshot(entry);
        }
    }

    /// <summary>
    /// Routes the answers of these messages to a consumer, handing over the ones already there.
    /// Returns how many of the messages were found.
    /// </summary>
    public int Await(Consumer consumer, IEnumerable<string> ids, List<Delivery> deliveries)
    {
        lock (_gate)
        {
            var found = 0;
            foreach (var id in ids)
            {
                if (!_inFlight.TryGetValue(id, out var entry))
                    entry = FindPending(id)?.Value;
                if (entry is null || !entry.Rpc)
                    continue;

                found++;
                entry.ReplyTo = consumer;
                if (entry.Response is not null)
                    PushResponse(entry, deliveries);
            }
            TrackEmptiness();
            return found;
        }
    }

    /// <summary>
    /// The consumer went away holding this message. It is never handed to somebody else, unless
    /// the queue was explicitly set up for that.
    /// </summary>
    public void Abandon(Consumer consumer, string id, List<Delivery> deliveries)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(id, out var entry) || entry.Owner != consumer)
                return;

            _inFlight.Remove(id);
            consumer.Current = null;
            entry.Owner = null;
            _unconfirmed++;

            if (Settings.RedeliverUnconfirmed)
            {
                _pending.AddFirst(entry);
                _emptySince = null;
                _redelivered++;
                Dispatch(deliveries);
            }
            else
            {
                Bury(entry, DeadLetterReason.Unconfirmed, consumer.Description);
            }
            TrackEmptiness();
        }
    }

    /// <summary>
    /// The pending messages, oldest first, without touching them.
    /// </summary>
    public IReadOnlyList<PendingMessage> Peek(int limit)
    {
        lock (_gate)
        {
            var found = new List<PendingMessage>(Math.Min(limit, _pending.Count));
            for (var node = _pending.First; node is not null && found.Count < limit; node = node.Next)
            {
                var entry = node.Value;
                found.Add(new PendingMessage(entry.Id, entry.Body, entry.Rpc, entry.PublishedAt, entry.Ttl > TimeSpan.Zero ? entry.Ttl : null, entry.RequeuedFrom));
            }
            return found;
        }
    }

    /// <summary>
    /// Throws away every pending message. They do not become dead letters: this is somebody's
    /// decision, not something that went wrong.
    /// </summary>
    public int Purge()
    {
        lock (_gate)
        {
            var count = _pending.Count;
            _pending.Clear();
            _purged += count;
            TrackEmptiness();
            return count;
        }
    }

    public QueueActivity GetActivity(DateTimeOffset since)
    {
        lock (_gate)
        {
            return new QueueActivity(
                Name,
                Recent(_publishers, since),
                Recent(_askers, since),
                _consumers.Select(consumer => consumer.Description).ToList());
        }
    }

    public bool Contains(string id)
    {
        lock (_gate)
            return _inFlight.ContainsKey(id) || FindPending(id) is not null;
    }

    /// <summary>
    /// Discards what expired or outlived the retention and reports whether the queue itself
    /// can be let go. Returns true at most once: from then on the queue refuses everything.
    /// </summary>
    public bool Sweep()
    {
        lock (_gate)
        {
            for (var node = _pending.First; node is not null;)
            {
                var next = node.Next;
                BuryIfDead(node);
                node = next;
            }

            foreach (var (id, entry) in _inFlight)
            {
                // A message a connected consumer is processing has no deadline.
                if (entry.Owner is not null)
                    continue;

                // An answer waits for its collector for the retention time, counted from the
                // answer; an unanswered RPC is given up the retention time after it was sent.
                var since = entry.Response is null ? entry.Born : entry.AnsweredAt;
                if (time.GetElapsedTime(since) > Settings.Retention)
                {
                    _inFlight.Remove(id);
                    _dropped++;
                }
            }

            TrackEmptiness();

            if (_consumers.Count == 0 && _emptySince is { } emptySince && time.GetElapsedTime(emptySince) > Settings.EmptyLifetime)
                Removed = true;

            return Removed;
        }
    }

    public QueueSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var processing = _inFlight.Values.Count(e => e.Owner is not null);
            var answered = _inFlight.Values.Count(e => e.Owner is null && e.Response is not null);
            return new QueueSnapshot(Name, _pending.Count, processing, _inFlight.Count - processing - answered, answered,
                _consumers.Count, _published, _delivered, _confirmed, _responded, _redelivered,
                _expired, _notConsumed, _unconfirmed, _dropped)
            {
                Paused = Settings.Paused,
                Purged = _purged
            };
        }
    }

    /// <summary>
    /// The oldest pending message that may still be delivered. Dead ones found on the way are buried.
    /// </summary>
    private LinkedListNode<Entry>? FirstDeliverable()
    {
        while (_pending.First is { } node)
        {
            if (!BuryIfDead(node))
                return node;
        }
        return null;
    }

    private void Note(Dictionary<string, (DateTimeOffset Last, long Count)> who, string? name)
    {
        if (name is null)
            return;
        var now = time.GetUtcNow();
        who[name] = (now, who.GetValueOrDefault(name).Count + 1);

        // Whoever asks chooses how far back to look, so nothing is forgotten on reading; only
        // here, and only what is older than anybody may ask for.
        if (who.Count > PartiesBeforeForgetting)
            foreach (var old in who.Where(pair => pair.Value.Last < now - PartyMemory).Select(pair => pair.Key).ToList())
                who.Remove(old);
    }

    private static List<QueueParty> Recent(Dictionary<string, (DateTimeOffset Last, long Count)> who, DateTimeOffset since) =>
        who.Where(pair => pair.Value.Last >= since)
            .Select(pair => new QueueParty(pair.Key, pair.Value.Last, pair.Value.Count))
            .OrderBy(party => party.Name, StringComparer.Ordinal)
            .ToList();

    private bool BuryIfDead(LinkedListNode<Entry> node)
    {
        var entry = node.Value;
        DeadLetterReason reason;
        if (entry.Ttl > TimeSpan.Zero && time.GetElapsedTime(entry.Born) > entry.Ttl)
        {
            reason = DeadLetterReason.Expired;
            _expired++;
        }
        else if (IsPastRetention(entry))
        {
            reason = DeadLetterReason.NotConsumed;
            _notConsumed++;
        }
        else
        {
            return false;
        }

        _pending.Remove(node);
        Bury(entry, reason, consumer: null);
        return true;
    }

    private void Bury(Entry entry, DeadLetterReason reason, string? consumer) =>
        deadLetters.Add(
            new DeadLetter(entry.Id, Name, entry.Body, entry.Rpc, reason, entry.PublishedAt, time.GetUtcNow(), consumer),
            Settings.DeadLetterLimit,
            Settings.DeadLetterMaxAge);

    private Consumer? NextFreeConsumer()
    {
        for (var i = 0; i < _consumers.Count; i++)
        {
            var index = (_nextConsumer + i) % _consumers.Count;
            if (_consumers[index].TryAcquire())
            {
                _nextConsumer = (index + 1) % _consumers.Count;
                return _consumers[index];
            }
        }
        return null;
    }

    private void StoreResponse(Entry entry, string response)
    {
        if (entry.Response is null)
            _responded++;
        entry.Response = response;
        entry.AnsweredAt = time.GetTimestamp();
    }

    /// <summary>
    /// Hands the answer to whoever is waiting for it on a live connection. Otherwise it stays
    /// until it is collected the 1.x way, asked for again, or the retention runs out.
    /// </summary>
    private void PushResponse(Entry entry, List<Delivery> deliveries)
    {
        if (entry.ReplyTo is not { IsClosed: false } receiver)
            return;

        _inFlight.Remove(entry.Id);
        deliveries.Add(new Delivery(receiver, Snapshot(entry), IsResponse: true));
    }

    private LinkedListNode<Entry>? FindPending(string id)
    {
        for (var node = _pending.First; node is not null; node = node.Next)
        {
            if (node.Value.Id == id)
                return node;
        }
        return null;
    }

    private bool IsPastRetention(Entry entry) =>
        time.GetElapsedTime(entry.Born) > Settings.Retention;

    private void TrackEmptiness()
    {
        if (_pending.Count == 0 && _inFlight.Count == 0)
            _emptySince ??= time.GetTimestamp();
    }

    private BrokerMessage Snapshot(Entry entry) =>
        new(entry.Id, Name, entry.Body, entry.Rpc, entry.Response) { RequeuedFrom = entry.RequeuedFrom };
}
