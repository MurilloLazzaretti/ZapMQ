namespace ZapMQ.Core;

public sealed class BrokerOptions
{
    /// <summary>
    /// Longest a message may stay in the broker, counted from publication, whatever its state.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// How long a queue is kept after its last message leaves.
    /// </summary>
    public TimeSpan EmptyQueueLifetime { get; set; } = TimeSpan.FromMinutes(1);
}
