namespace ZapMQ.Core;

/// <summary>
/// A client the broker pushes messages to. It holds at most one unconfirmed message at a time,
/// whatever the number of queues it is bound to.
/// </summary>
public abstract class Consumer
{
    private const int Free = 0, Busy = 1, Closed = 2;

    private int _state;

    /// <summary>
    /// Who this is, for logs and dead letters.
    /// </summary>
    public abstract string Description { get; }

    public bool IsClosed => Volatile.Read(ref _state) == Closed;

    public bool IsBusy => Volatile.Read(ref _state) == Busy;

    // Bound queues in binding order, and where the last message came from, so that a consumer
    // bound to several queues is served from each in turn. Guarded by the lock on the list.
    internal List<string> Queues { get; } = [];

    internal string? LastQueue { get; set; }

    /// <summary>
    /// The queues this consumer is bound to, in binding order.
    /// </summary>
    public IReadOnlyList<string> GetBoundQueues()
    {
        lock (Queues)
            return [.. Queues];
    }

    /// <summary>
    /// Queue and id of the message this consumer has not confirmed yet. Set and cleared under
    /// the lock of that queue.
    /// </summary>
    internal (string Queue, string Id)? Current { get; set; }

    /// <summary>
    /// Hands a message over. Must not block: the broker calls it while dispatching.
    /// </summary>
    protected internal abstract void Deliver(BrokerMessage message);

    /// <summary>
    /// Hands over the answer to an RPC message this consumer published. Must not block.
    /// </summary>
    protected internal abstract void DeliverResponse(BrokerMessage message);

    internal bool TryAcquire() => Interlocked.CompareExchange(ref _state, Busy, Free) == Free;

    internal void Release() => Interlocked.CompareExchange(ref _state, Free, Busy);

    internal void Close() => Volatile.Write(ref _state, Closed);
}
