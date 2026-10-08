using System.Collections.Concurrent;

namespace ZapMQ.Core;

/// <summary>
/// In-memory message broker. Transport-agnostic: the protocol layers translate requests into
/// these calls.
/// </summary>
/// <remarks>
/// A message goes to a single consumer and is never handed out again on the broker's own
/// initiative. Consumers either ask for messages (<see cref="Take"/>, the 1.x way) or are bound
/// to queues and get them pushed (<see cref="Bind"/>), in which case they confirm each one.
/// </remarks>
public sealed class Broker
{
    private readonly BrokerOptions _options;
    private readonly TimeProvider _time;
    private readonly DeadLetterStore _deadLetters;
    // Queue names are case-sensitive, as they have always been.
    private readonly ConcurrentDictionary<string, MessageQueue> _queues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, QueueOptions> _queueOptions;
    private long _published, _delivered, _confirmed;

    public Broker(BrokerOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new BrokerOptions();
        _time = time ?? TimeProvider.System;
        _deadLetters = new DeadLetterStore(_time);
        _queueOptions = new ConcurrentDictionary<string, QueueOptions>(_options.Queues, StringComparer.Ordinal);
    }

    // ── Publishing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a message to a queue, creating the queue on first use, and returns the message id.
    /// A <paramref name="ttl"/> of zero or less means the message only leaves by delivery or
    /// retention. <paramref name="replyTo"/> is the consumer that gets the answer pushed when the
    /// message is an RPC one.
    /// </summary>
    /// <remarks>
    /// <paramref name="publisher"/> names whoever is publishing, for the record of who uses the
    /// queue; it changes nothing in the delivery.
    /// </remarks>
    public string Publish(string queue, string body, bool rpc = false, TimeSpan ttl = default, Consumer? replyTo = null, string? publisher = null) =>
        Publish(queue, body, rpc, ttl, replyTo, requeuedFrom: null, publisher);

    private string Publish(string queue, string body, bool rpc, TimeSpan ttl, Consumer? replyTo, string? requeuedFrom, string? publisher)
    {
        ArgumentException.ThrowIfNullOrEmpty(queue);
        ArgumentNullException.ThrowIfNull(body);

        // Same shape the 1.x server produced: upper-case GUID wrapped in braces.
        var id = Guid.NewGuid().ToString("B").ToUpperInvariant();
        var deliveries = new List<Delivery>();

        while (true)
        {
            var target = GetOrAddQueue(queue);
            if (target.TryPublish(id, body, rpc, ttl, replyTo, requeuedFrom, publisher, deliveries))
                break;

            // The sweeper retired this instance between the lookup and the publish.
            _queues.TryRemove(new KeyValuePair<string, MessageQueue>(queue, target));
        }

        Interlocked.Increment(ref _published);
        Hand(deliveries);
        return id;
    }

    // ── Consuming by asking (1.x) ───────────────────────────────────────────

    /// <summary>
    /// Delivers the oldest pending message of the queue to this caller only, or null when there
    /// is none. Delivery ends a plain message; an RPC one then waits for its response.
    /// </summary>
    /// <remarks>
    /// <paramref name="asker"/> names whoever is asking, for the record of who uses the queue.
    /// </remarks>
    public BrokerMessage? Take(string queue, string? asker = null)
    {
        var message = _queues.TryGetValue(queue, out var target) ? target.Take(asker) : null;
        if (message is not null)
            Interlocked.Increment(ref _delivered);
        return message;
    }

    /// <summary>
    /// Stores the response of an RPC message that was taken. With
    /// <paramref name="includeUndelivered"/>, a message still waiting for delivery is accepted
    /// too and is then never delivered; only the 1.x protocol allows that.
    /// </summary>
    public bool Respond(string queue, string id, string response, bool includeUndelivered = false)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!_queues.TryGetValue(queue, out var target))
            return false;

        var deliveries = new List<Delivery>();
        var stored = target.Respond(id, response, includeUndelivered, deliveries);
        Hand(deliveries);
        return stored;
    }

    public bool Contains(string queue, string id) =>
        _queues.TryGetValue(queue, out var target) && target.Contains(id);

    /// <summary>
    /// Returns an answered RPC message and removes it, or null while there is no response.
    /// </summary>
    public BrokerMessage? TakeResponse(string queue, string id) =>
        _queues.TryGetValue(queue, out var target) ? target.TakeResponse(id) : null;

    // ── Consuming by push ───────────────────────────────────────────────────

    /// <summary>
    /// Starts pushing the messages of a queue to a consumer.
    /// </summary>
    public void Bind(Consumer consumer, string queue)
    {
        ArgumentException.ThrowIfNullOrEmpty(queue);
        if (consumer.IsClosed)
            return;

        lock (consumer.Queues)
        {
            if (!consumer.Queues.Contains(queue))
                consumer.Queues.Add(queue);
        }

        var deliveries = new List<Delivery>();
        while (true)
        {
            var target = GetOrAddQueue(queue);
            if (target.Bind(consumer, deliveries))
                break;

            _queues.TryRemove(new KeyValuePair<string, MessageQueue>(queue, target));
        }
        Hand(deliveries);
    }

    /// <summary>
    /// Stops pushing the messages of a queue to a consumer. A message it already holds stays its.
    /// </summary>
    public void Unbind(Consumer consumer, string queue)
    {
        lock (consumer.Queues)
            consumer.Queues.Remove(queue);

        if (_queues.TryGetValue(queue, out var target))
            target.Unbind(consumer);
    }

    /// <summary>
    /// The consumer finished the message it was pushed and is free for the next one.
    /// </summary>
    public bool Confirm(Consumer consumer, string queue, string id)
    {
        if (!_queues.TryGetValue(queue, out var target) || !target.Confirm(consumer, id))
            return false;

        Interlocked.Increment(ref _confirmed);
        Free(consumer);
        return true;
    }

    /// <summary>
    /// The consumer finished the RPC message it was pushed, with this answer.
    /// </summary>
    public bool Respond(Consumer consumer, string queue, string id, string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!_queues.TryGetValue(queue, out var target))
            return false;

        var deliveries = new List<Delivery>();
        if (!target.Respond(consumer, id, response, deliveries))
            return false;

        Interlocked.Increment(ref _confirmed);
        Hand(deliveries);
        Free(consumer);
        return true;
    }

    /// <summary>
    /// Routes the answers of RPC messages to a consumer that reconnected, handing over the ones
    /// that arrived meanwhile. Returns how many of the messages still exist.
    /// </summary>
    public int Await(Consumer consumer, string queue, IEnumerable<string> ids)
    {
        if (!_queues.TryGetValue(queue, out var target))
            return 0;

        var deliveries = new List<Delivery>();
        var found = target.Await(consumer, ids, deliveries);
        Hand(deliveries);
        return found;
    }

    /// <summary>
    /// The consumer is gone. The message it had not confirmed becomes a dead letter, or goes back
    /// to its queue when the queue is set up to redeliver.
    /// </summary>
    public void Disconnect(Consumer consumer)
    {
        consumer.Close();

        string[] queues;
        lock (consumer.Queues)
        {
            queues = [.. consumer.Queues];
            consumer.Queues.Clear();
        }

        foreach (var queue in queues)
        {
            if (_queues.TryGetValue(queue, out var target))
                target.Unbind(consumer);
        }

        // Read only now: a message is assigned under the lock of a queue the consumer is bound
        // to, and every one of those locks has just been taken and released above.
        if (consumer.Current is { } current && _queues.TryGetValue(current.Queue, out var holder))
        {
            var deliveries = new List<Delivery>();
            holder.Abandon(consumer, current.Id, deliveries);
            Hand(deliveries);
        }
    }

    // ── Dead letters ────────────────────────────────────────────────────────

    public IReadOnlyList<DeadLetterSummary> GetDeadLetterSummary() => _deadLetters.Summarize();

    public IReadOnlyList<DeadLetter> GetDeadLetters(string queue) => _deadLetters.List(queue);

    public DeadLetter? GetDeadLetter(string queue, string id) => _deadLetters.Find(queue, id);

    /// <summary>
    /// Publishes a copy of a dead letter to its queue and removes the dead letter. Returns the id
    /// of the copy, or null when the dead letter does not exist.
    /// </summary>
    public string? RequeueDeadLetter(string queue, string id)
    {
        var letter = _deadLetters.Remove(queue, id);
        return letter is null ? null : Publish(queue, letter.Body, letter.Rpc, default, replyTo: null, requeuedFrom: letter.Id, publisher: null);
    }

    public bool DiscardDeadLetter(string queue, string id) => _deadLetters.Remove(queue, id) is not null;

    public int DiscardDeadLetters(string queue) => _deadLetters.Clear(queue);

    // ── Queue settings ──────────────────────────────────────────────────────

    /// <summary>
    /// The settings given to a queue, or null when it uses the broker defaults.
    /// </summary>
    public QueueOptions? GetQueueOptions(string queue) => _queueOptions.GetValueOrDefault(queue);

    /// <summary>
    /// Changes the settings of a queue, effective immediately. Null goes back to the defaults.
    /// </summary>
    public void ConfigureQueue(string queue, QueueOptions? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(queue);
        if (options is null)
            _queueOptions.TryRemove(queue, out _);
        else
            _queueOptions[queue] = options;

        if (_queues.TryGetValue(queue, out var target))
        {
            target.Settings = Resolve(queue);
            // A queue that was paused and no longer is has messages to hand out.
            var deliveries = new List<Delivery>();
            target.Dispatch(deliveries);
            Hand(deliveries);
        }
    }

    /// <summary>
    /// Every queue with settings of its own.
    /// </summary>
    public IReadOnlyDictionary<string, QueueOptions> GetAllQueueOptions() =>
        new Dictionary<string, QueueOptions>(_queueOptions, StringComparer.Ordinal);

    /// <summary>
    /// Pauses or resumes a queue, keeping its other settings.
    /// </summary>
    public void SetPaused(string queue, bool paused) =>
        ConfigureQueue(queue, (GetQueueOptions(queue) ?? new QueueOptions()) with { Paused = paused });

    // ── Inspection ──────────────────────────────────────────────────────────

    public QueueSnapshot? GetQueue(string queue) =>
        _queues.TryGetValue(queue, out var target) ? target.GetSnapshot() : null;

    /// <summary>
    /// The pending messages of a queue, oldest first, without consuming them.
    /// </summary>
    public IReadOnlyList<PendingMessage> Peek(string queue, int limit = 100) =>
        _queues.TryGetValue(queue, out var target) ? target.Peek(limit) : [];

    /// <summary>
    /// Throws away the pending messages of a queue and returns how many there were. Messages
    /// being processed are not touched.
    /// </summary>
    public int Purge(string queue) =>
        _queues.TryGetValue(queue, out var target) ? target.Purge() : 0;

    /// <summary>
    /// Who has published to each queue, asked it for messages or is bound to it, lately.
    /// </summary>
    /// <summary>
    /// The longest window <see cref="GetActivity"/> can answer for.
    /// </summary>
    public static TimeSpan ActivityMemory => MessageQueue.PartyMemory;

    public IReadOnlyList<QueueActivity> GetActivity(TimeSpan window)
    {
        var since = _time.GetUtcNow() - window;
        return _queues.Values.Select(queue => queue.GetActivity(since)).OrderBy(activity => activity.Queue, StringComparer.Ordinal).ToList();
    }

    public BrokerTotals GetTotals() =>
        new(Interlocked.Read(ref _published), Interlocked.Read(ref _delivered), Interlocked.Read(ref _confirmed), _deadLetters.Total);

    // ── Housekeeping ────────────────────────────────────────────────────────

    /// <summary>
    /// Buries expired and unconsumed messages, prunes old dead letters and lets go of queues
    /// that stayed empty and unbound. Meant to be called periodically.
    /// </summary>
    public void Sweep()
    {
        foreach (var pair in _queues)
        {
            if (pair.Value.Sweep())
                _queues.TryRemove(pair);
        }

        _deadLetters.Prune(queue =>
        {
            var settings = Resolve(queue);
            return (settings.DeadLetterLimit, settings.DeadLetterMaxAge);
        });
    }

    public IReadOnlyList<QueueSnapshot> GetQueues() =>
        _queues.Values.Select(q => q.GetSnapshot()).OrderBy(q => q.Name, StringComparer.Ordinal).ToList();

    private MessageQueue GetOrAddQueue(string queue) =>
        _queues.GetOrAdd(queue, name => new MessageQueue(name, _time, Resolve(name), _deadLetters));

    private QueueSettings Resolve(string queue)
    {
        if (_options.DisposablePrefixes.Exists(prefix => queue.StartsWith(prefix, StringComparison.Ordinal)))
            return new QueueSettings(_options.DisposableRetention, _options.EmptyQueueLifetime, DeadLetterLimit: 0, TimeSpan.Zero, RedeliverUnconfirmed: false, Paused: false);

        var own = _queueOptions.GetValueOrDefault(queue);
        return new QueueSettings(
            own?.Retention ?? _options.Retention,
            _options.EmptyQueueLifetime,
            own?.DeadLetterLimit ?? _options.DeadLetterLimit,
            own?.DeadLetterMaxAge ?? _options.DeadLetterMaxAge,
            own?.RedeliverUnconfirmed ?? false,
            own?.Paused ?? false);
    }

    /// <summary>
    /// A consumer finished its message: it may take the next one from any queue it is bound to,
    /// starting after the queue that served it last so that none of them starves.
    /// </summary>
    private void Free(Consumer consumer)
    {
        consumer.Release();

        string[] queues;
        lock (consumer.Queues)
        {
            var start = consumer.LastQueue is { } last ? consumer.Queues.IndexOf(last) + 1 : 0;
            queues = [.. consumer.Queues.Skip(start), .. consumer.Queues.Take(start)];
        }

        var deliveries = new List<Delivery>();
        foreach (var queue in queues)
        {
            if (consumer.IsBusy || consumer.IsClosed)
                break;
            if (_queues.TryGetValue(queue, out var target))
                target.Dispatch(deliveries);
        }
        Hand(deliveries);
    }

    /// <summary>
    /// Hands deliveries over outside every lock. A consumer that fails here is on its way out:
    /// whoever owns it reports the disconnection, which settles the message it was given.
    /// </summary>
    private void Hand(List<Delivery> deliveries)
    {
        foreach (var delivery in deliveries)
        {
            try
            {
                if (delivery.IsResponse)
                {
                    delivery.Consumer.DeliverResponse(delivery.Message);
                }
                else
                {
                    Interlocked.Increment(ref _delivered);
                    delivery.Consumer.Deliver(delivery.Message);
                }
            }
            catch
            {
                // See the summary above.
            }
        }
    }
}
