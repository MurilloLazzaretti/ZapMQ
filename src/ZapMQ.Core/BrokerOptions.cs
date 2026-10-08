namespace ZapMQ.Core;

public sealed class BrokerOptions
{
    /// <summary>
    /// Longest a message may wait in a queue without being consumed, counted from publication.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// How long a queue is kept after its last message leaves.
    /// </summary>
    public TimeSpan EmptyQueueLifetime { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Dead letters kept per queue. Zero keeps none.
    /// </summary>
    public int DeadLetterLimit { get; set; } = 1000;

    /// <summary>
    /// Longest a dead letter is kept. Zero keeps none.
    /// </summary>
    public TimeSpan DeadLetterMaxAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Settings of individual queues, by name. A queue that is not here uses the values above.
    /// </summary>
    public Dictionary<string, QueueOptions> Queues { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// What can be set for one queue. A null value means "use the broker default".
/// </summary>
public sealed record QueueOptions
{
    public TimeSpan? Retention { get; init; }

    public int? DeadLetterLimit { get; init; }

    public TimeSpan? DeadLetterMaxAge { get; init; }

    /// <summary>
    /// Puts a message that was delivered but not confirmed back at the head of the queue instead
    /// of dead-lettering it. Only for queues where handling the same message twice does no harm.
    /// </summary>
    public bool RedeliverUnconfirmed { get; init; }

    /// <summary>
    /// A paused queue keeps receiving and hands nothing to anybody.
    /// </summary>
    public bool Paused { get; init; }
}
