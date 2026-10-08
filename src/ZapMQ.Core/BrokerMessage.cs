namespace ZapMQ.Core;

/// <summary>
/// A message as handed out by the broker. <see cref="Body"/> and <see cref="Response"/> are
/// opaque to the core: the protocol layers decide what they contain.
/// </summary>
public sealed record BrokerMessage(string Id, string Queue, string Body, bool Rpc, string? Response)
{
    /// <summary>
    /// Id of the dead letter this message is a copy of, when it was put back by hand.
    /// </summary>
    public string? RequeuedFrom { get; init; }
}

public sealed record QueueSnapshot(
    string Name,
    int Pending,
    int Processing,
    int AwaitingResponse,
    int Answered,
    int Consumers,
    long Published,
    long Delivered,
    long Confirmed,
    long Responded,
    long Redelivered,
    long Expired,
    long NotConsumed,
    long Unconfirmed,
    long Dropped)
{
    /// <summary>
    /// The queue receives and hands nothing out.
    /// </summary>
    public bool Paused { get; init; }

    /// <summary>
    /// Messages thrown away by somebody emptying the queue.
    /// </summary>
    public long Purged { get; init; }
}

/// <summary>
/// A message still waiting in a queue, as shown to whoever inspects it.
/// </summary>
public sealed record PendingMessage(string Id, string Body, bool Rpc, DateTimeOffset PublishedAt, TimeSpan? Ttl, string? RequeuedFrom);

/// <summary>
/// Somebody who has been publishing to a queue, or asking it for messages.
/// </summary>
public sealed record QueueParty(string Name, DateTimeOffset LastSeen, long Count);

/// <summary>
/// Who has been using a queue recently.
/// </summary>
public sealed record QueueActivity(string Queue, IReadOnlyList<QueueParty> Publishers, IReadOnlyList<QueueParty> Askers, IReadOnlyList<string> Consumers);

/// <summary>
/// Counters of the whole broker since it started. They keep counting after a queue is let go.
/// </summary>
public sealed record BrokerTotals(long Published, long Delivered, long Confirmed, long DeadLettered);

public enum DeadLetterReason
{
    /// <summary>Its TTL ran out before anybody consumed it.</summary>
    Expired,

    /// <summary>Nobody consumed it within the retention of the queue.</summary>
    NotConsumed,

    /// <summary>It was delivered and the consumer went away before confirming it.</summary>
    Unconfirmed
}

public sealed record DeadLetter(
    string Id,
    string Queue,
    string Body,
    bool Rpc,
    DeadLetterReason Reason,
    DateTimeOffset PublishedAt,
    DateTimeOffset DiedAt,
    string? Consumer);

public sealed record DeadLetterSummary(string Queue, int Expired, int NotConsumed, int Unconfirmed);
