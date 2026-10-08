using System.Text.Json;
using ZapMQ.Core;

namespace ZapMQ.Server.Admin;

/// <summary>
/// Administration over plain HTTP: dead letters and the settings of individual queues.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdmin(this IEndpointRouteBuilder routes)
    {
        var admin = routes.MapGroup("/admin");

        admin.MapGet("/dead-letters", (Broker broker) => Results.Json(broker.GetDeadLetterSummary()));

        admin.MapGet("/dead-letters/{queue}", (string queue, Broker broker) =>
            Results.Json(broker.GetDeadLetters(queue).Select(Describe)));

        admin.MapGet("/dead-letters/{queue}/{id}", (string queue, string id, Broker broker) =>
            broker.GetDeadLetter(queue, id) is { } letter ? Results.Json(Describe(letter)) : Results.NotFound());

        admin.MapPost("/dead-letters/{queue}/{id}/requeue", (string queue, string id, Broker broker, ILoggerFactory loggers) =>
        {
            if (broker.RequeueDeadLetter(queue, id) is not { } messageId)
                return Results.NotFound();

            loggers.CreateLogger("ZapMQ.Admin").LogInformation(
                "Dead letter {Id} of queue {Queue} was requeued as {MessageId}", id, queue, messageId);
            return Results.Json(new { messageId });
        });

        admin.MapDelete("/dead-letters/{queue}/{id}", (string queue, string id, Broker broker) =>
            broker.DiscardDeadLetter(queue, id) ? Results.NoContent() : Results.NotFound());

        admin.MapDelete("/dead-letters/{queue}", (string queue, Broker broker) =>
            Results.Json(new { discarded = broker.DiscardDeadLetters(queue) }));

        admin.MapGet("/queues/{queue}", (string queue, Broker broker) =>
            Results.Json(QueueSettingsMapping.ToOptions(broker.GetQueueOptions(queue))));

        admin.MapPut("/queues/{queue}", (string queue, QueueSettingsOptions settings, Broker broker, ILoggerFactory loggers) =>
        {
            broker.ConfigureQueue(queue, QueueSettingsMapping.ToCore(settings));
            loggers.CreateLogger("ZapMQ.Admin").LogInformation(
                "Queue {Queue} reconfigured: {Settings}", queue, JsonSerializer.Serialize(settings));
            return Results.Json(QueueSettingsMapping.ToOptions(broker.GetQueueOptions(queue)));
        });

        admin.MapDelete("/queues/{queue}", (string queue, Broker broker) =>
        {
            broker.ConfigureQueue(queue, null);
            return Results.NoContent();
        });
    }

    private static object Describe(DeadLetter letter)
    {
        using var body = JsonDocument.Parse(letter.Body);
        return new
        {
            id = letter.Id,
            queue = letter.Queue,
            reason = letter.Reason switch
            {
                DeadLetterReason.Expired => "expired",
                DeadLetterReason.NotConsumed => "not-consumed",
                _ => "unconfirmed"
            },
            rpc = letter.Rpc,
            publishedAt = letter.PublishedAt,
            diedAt = letter.DiedAt,
            consumer = letter.Consumer,
            body = body.RootElement.Clone()
        };
    }
}

/// <summary>
/// Between the settings as they are written in configuration and as the broker takes them.
/// </summary>
public static class QueueSettingsMapping
{
    public static QueueOptions ToCore(QueueSettingsOptions settings) => new()
    {
        Retention = settings.RetentionSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
        RedeliverUnconfirmed = settings.RedeliverUnconfirmed,
        Paused = settings.Paused,
        KeepRecent = Math.Clamp(settings.KeepRecent ?? 0, 0, 500),
        DeadLetterLimit = settings.DeadLetters?.MaxMessagesPerQueue,
        DeadLetterMaxAge = settings.DeadLetters?.MaxAgeHours is { } hours ? TimeSpan.FromHours(hours) : null
    };

    public static QueueSettingsOptions ToOptions(QueueOptions? options) => new()
    {
        RetentionSeconds = options?.Retention is { } retention ? (int)retention.TotalSeconds : null,
        RedeliverUnconfirmed = options?.RedeliverUnconfirmed ?? false,
        Paused = options?.Paused ?? false,
        KeepRecent = options?.KeepRecent is > 0 and var kept ? kept : null,
        DeadLetters = options?.DeadLetterLimit is null && options?.DeadLetterMaxAge is null
            ? null
            : new QueueDeadLetterOptions
            {
                MaxMessagesPerQueue = options.DeadLetterLimit,
                MaxAgeHours = options.DeadLetterMaxAge is { } age ? (int)age.TotalHours : null
            }
    };
}
