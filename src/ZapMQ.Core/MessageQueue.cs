namespace ZapMQ.Core;

internal sealed class MessageQueue(string name, TimeProvider time, BrokerOptions options)
{
    private sealed class Entry
    {
        public required string Id;
        public required string Body;
        public required bool Rpc;
        public required TimeSpan Ttl;
        public required long Born;
        public string? Response;
    }

    private readonly object _gate = new();
    // Publication order, so the head is always the oldest pending message.
    private readonly LinkedList<Entry> _pending = new();
    // RPC messages already delivered, waiting for the response to be written or collected.
    private readonly Dictionary<string, Entry> _inFlight = new(StringComparer.Ordinal);
    private long? _emptySince;
    private long _published, _delivered, _responded, _expired, _dropped;

    public string Name { get; } = name;

    /// <summary>
    /// Set once the broker has let go of this queue. A publisher that still holds the instance
    /// must fetch a new one instead of adding to a queue nobody will read.
    /// </summary>
    public bool Removed { get; private set; }

    public bool TryPublish(string id, string body, bool rpc, TimeSpan ttl)
    {
        lock (_gate)
        {
            if (Removed)
                return false;

            _pending.AddLast(new Entry { Id = id, Body = body, Rpc = rpc, Ttl = ttl, Born = time.GetTimestamp() });
            _emptySince = null;
            _published++;
            return true;
        }
    }

    /// <summary>
    /// Hands the oldest deliverable message to exactly one caller. Selecting the message and
    /// taking it out of the pending list happen under the same lock, so two concurrent callers
    /// can never receive the same message.
    /// </summary>
    public BrokerMessage? Take()
    {
        lock (_gate)
        {
            try
            {
                while (_pending.First is { } node)
                {
                    var entry = node.Value;
                    _pending.RemoveFirst();

                    if (IsExpired(entry)) { _expired++; continue; }
                    if (IsPastRetention(entry)) { _dropped++; continue; }

                    if (entry.Rpc)
                        _inFlight[entry.Id] = entry;

                    _delivered++;
                    return Snapshot(entry);
                }
                return null;
            }
            finally
            {
                TrackEmptiness();
            }
        }
    }

    public bool Respond(string id, string response)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(id, out var entry) || IsPastRetention(entry))
                return false;

            if (entry.Response is null)
                _responded++;
            entry.Response = response;
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
    /// Discards what expired or outlived the retention and reports whether the queue itself
    /// can be let go. Returns true at most once: from then on the queue refuses new messages.
    /// </summary>
    public bool Sweep()
    {
        lock (_gate)
        {
            for (var node = _pending.First; node is not null;)
            {
                var next = node.Next;
                if (IsExpired(node.Value)) { _pending.Remove(node); _expired++; }
                else if (IsPastRetention(node.Value)) { _pending.Remove(node); _dropped++; }
                node = next;
            }

            foreach (var (id, entry) in _inFlight)
            {
                if (IsPastRetention(entry))
                {
                    _inFlight.Remove(id);
                    _dropped++;
                }
            }

            TrackEmptiness();

            if (_emptySince is { } since && time.GetElapsedTime(since) > options.EmptyQueueLifetime)
                Removed = true;

            return Removed;
        }
    }

    public QueueSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var answered = _inFlight.Values.Count(e => e.Response is not null);
            return new QueueSnapshot(Name, _pending.Count, _inFlight.Count - answered, answered,
                _published, _delivered, _responded, _expired, _dropped);
        }
    }

    private bool IsExpired(Entry entry) =>
        entry.Ttl > TimeSpan.Zero && time.GetElapsedTime(entry.Born) > entry.Ttl;

    private bool IsPastRetention(Entry entry) =>
        time.GetElapsedTime(entry.Born) > options.Retention;

    private void TrackEmptiness()
    {
        if (_pending.Count == 0 && _inFlight.Count == 0)
            _emptySince ??= time.GetTimestamp();
    }

    private BrokerMessage Snapshot(Entry entry) =>
        new(entry.Id, Name, entry.Body, entry.Rpc, entry.Response);
}
