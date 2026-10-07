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
    long Dropped);

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
