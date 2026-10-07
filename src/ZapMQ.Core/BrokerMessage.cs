namespace ZapMQ.Core;

/// <summary>
/// A message as handed out by the broker. <see cref="Body"/> and <see cref="Response"/> are
/// opaque to the core: the protocol layers decide what they contain.
/// </summary>
public sealed record BrokerMessage(string Id, string Queue, string Body, bool Rpc, string? Response);

public sealed record QueueSnapshot(
    string Name,
    int Pending,
    int AwaitingResponse,
    int Answered,
    long Published,
    long Delivered,
    long Responded,
    long Expired,
    long Dropped);
