using System.Collections.Concurrent;

namespace ZapMQ.Core;

/// <summary>
/// In-memory message broker. Transport-agnostic: the protocol layers translate requests into
/// these calls.
/// </summary>
public sealed class Broker(BrokerOptions? options = null, TimeProvider? time = null)
{
    private readonly BrokerOptions _options = options ?? new BrokerOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // Queue names are case-sensitive, as they have always been.
    private readonly ConcurrentDictionary<string, MessageQueue> _queues = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a message to a queue, creating the queue on first use, and returns the message id.
    /// A <paramref name="ttl"/> of zero or less means the message only leaves by delivery or retention.
    /// </summary>
    public string Publish(string queue, string body, bool rpc = false, TimeSpan ttl = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queue);
        ArgumentNullException.ThrowIfNull(body);

        // Same shape the 1.x server produced: upper-case GUID wrapped in braces.
        var id = Guid.NewGuid().ToString("B").ToUpperInvariant();

        while (true)
        {
            var target = _queues.GetOrAdd(queue, name => new MessageQueue(name, _time, _options));
            if (target.TryPublish(id, body, rpc, ttl))
                return id;

            // The sweeper retired this instance between the lookup and the publish.
            _queues.TryRemove(new KeyValuePair<string, MessageQueue>(queue, target));
        }
    }

    /// <summary>
    /// Delivers the oldest pending message of the queue to this caller only, or null when there
    /// is none. A delivered message is never handed out again.
    /// </summary>
    public BrokerMessage? Take(string queue) =>
        _queues.TryGetValue(queue, out var target) ? target.Take() : null;

    /// <summary>
    /// Stores the response of an RPC message that has already been delivered.
    /// </summary>
    public bool Respond(string queue, string id, string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return _queues.TryGetValue(queue, out var target) && target.Respond(id, response);
    }

    /// <summary>
    /// Returns an answered RPC message and removes it, or null while there is no response.
    /// </summary>
    public BrokerMessage? TakeResponse(string queue, string id) =>
        _queues.TryGetValue(queue, out var target) ? target.TakeResponse(id) : null;

    /// <summary>
    /// Discards expired and over-retention messages and lets go of queues that stayed empty.
    /// Meant to be called periodically.
    /// </summary>
    public void Sweep()
    {
        foreach (var pair in _queues)
        {
            if (pair.Value.Sweep())
                _queues.TryRemove(pair);
        }
    }

    public IReadOnlyList<QueueSnapshot> GetQueues() =>
        _queues.Values.Select(q => q.GetSnapshot()).OrderBy(q => q.Name, StringComparer.Ordinal).ToList();
}
