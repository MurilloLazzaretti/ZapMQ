using System.Text.Json;
using ZapMQ.Core;
using ZapMQ.Server.Admin;
using ZapMQ.Server.V2;

namespace ZapMQ.Server.Panel;

/// <summary>
/// What the panel reads and commands, under <c>/api</c>. All of it sits behind the login.
/// </summary>
public static class PanelEndpoints
{
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapPanelApi(this IEndpointRouteBuilder routes, ServerOptions options, DateTimeOffset startedAt)
    {
        var api = routes.MapGroup("/api");

        // ── Session ─────────────────────────────────────────────────────────

        api.MapPost("/login", (LoginRequest request, PanelAuth auth, HttpContext context, ILoggerFactory loggers) =>
        {
            var log = loggers.CreateLogger("ZapMQ.Panel");
            if (!auth.Accepts(request.User, request.Password))
            {
                log.LogWarning("Refused panel login for {User} from {Address}", request.User, context.Connection.RemoteIpAddress);
                return Results.Json(new { error = "Usuário ou senha inválidos" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            context.Response.Cookies.Append(PanelAuth.Cookie, auth.Issue(request.User!), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
                MaxAge = auth.SessionLength
            });
            log.LogInformation("Panel login of {User} from {Address}", request.User, context.Connection.RemoteIpAddress);
            return Results.Json(new { user = request.User, defaultPassword = auth.HasDefaultPassword });
        });

        api.MapPost("/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(PanelAuth.Cookie, new CookieOptions
            {
                Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/"
            });
            return Results.NoContent();
        });

        api.MapGet("/session", (HttpContext context, PanelAuth auth) =>
            auth.Validate(context.Request.Cookies[PanelAuth.Cookie]) is { } user
                ? Results.Json(new { user, defaultPassword = auth.HasDefaultPassword, version = ServerHost.Version })
                : Results.Json(new { user = (string?)null, version = ServerHost.Version }, statusCode: StatusCodes.Status401Unauthorized));

        // ── Overview and charts ─────────────────────────────────────────────

        api.MapGet("/overview", (Broker broker, V2Connections connections, MetricsSampler sampler) =>
            Results.Json(Overview(broker, connections, sampler, startedAt)));

        api.MapGet("/series", (string? range, MetricsSampler sampler) =>
            Results.Json(new { range = range == "day" ? "day" : "hour", points = range == "day" ? sampler.Day() : sampler.Recent() }));

        // Server-sent events: the same overview and list of queues, again every two seconds.
        api.MapGet("/live", async (HttpContext context, Broker broker, V2Connections connections, MetricsSampler sampler, IHostApplicationLifetime lifetime) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            // Tells a reverse proxy not to hold the events back.
            context.Response.Headers["X-Accel-Buffering"] = "no";

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        overview = Overview(broker, connections, sampler, startedAt),
                        queues = Queues(broker)
                    }, Web);
                    await context.Response.WriteAsync($"data: {payload}\n\n", stop.Token);
                    await context.Response.Body.FlushAsync(stop.Token);
                    await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // The browser went away or the service is stopping.
            }
        });

        // ── Queues ──────────────────────────────────────────────────────────

        api.MapGet("/queues", (Broker broker) => Results.Json(Queues(broker)));

        api.MapGet("/queues/{queue}", (string queue, Broker broker, V2Connections connections) =>
        {
            var activity = broker.GetActivity(ActivityWindow).FirstOrDefault(item => item.Queue == queue);
            var dead = broker.GetDeadLetterSummary().FirstOrDefault(item => item.Queue == queue);
            return Results.Json(new
            {
                name = queue,
                exists = broker.GetQueue(queue) is not null,
                snapshot = broker.GetQueue(queue),
                settings = broker.GetQueueOptions(queue) is { } own ? QueueSettingsMapping.ToOptions(own) : null,
                defaults = new
                {
                    retentionSeconds = options.RetentionSeconds,
                    deadLetters = new { maxMessagesPerQueue = options.DeadLetters.MaxMessagesPerQueue, maxAgeHours = options.DeadLetters.MaxAgeHours }
                },
                deadLetters = new { expired = dead?.Expired ?? 0, notConsumed = dead?.NotConsumed ?? 0, unconfirmed = dead?.Unconfirmed ?? 0 },
                publishers = (activity?.Publishers ?? []).Select(Party),
                askers = (activity?.Askers ?? []).Select(Party),
                consumers = connections.All()
                    .Where(connection => connection.GetBoundQueues().Contains(queue))
                    .Select(Describe)
            });
        });

        api.MapGet("/queues/{queue}/messages", (string queue, int? limit, Broker broker) =>
            Results.Json(broker.Peek(queue, Math.Clamp(limit ?? 50, 1, 500)).Select(message => new
            {
                id = message.Id,
                rpc = message.Rpc,
                publishedAt = message.PublishedAt,
                ttlMs = message.Ttl?.TotalMilliseconds,
                requeuedFrom = message.RequeuedFrom,
                body = Body(message.Body)
            })));

        api.MapPut("/queues/{queue}/settings", (string queue, QueueSettingsOptions settings, Broker broker, QueueDefinitionStore store, HttpContext context, ILoggerFactory loggers) =>
        {
            // Pausing has its own action; a form that edits the definition must not undo it.
            settings.Paused = broker.GetQueueOptions(queue)?.Paused ?? false;
            store.Set(queue, QueueSettingsMapping.ToCore(settings));
            Audit(loggers, context, "Queue {Queue} reconfigured: {Settings}", queue, JsonSerializer.Serialize(settings, Web));
            return Results.Json(QueueSettingsMapping.ToOptions(broker.GetQueueOptions(queue)));
        });

        api.MapDelete("/queues/{queue}/settings", (string queue, QueueDefinitionStore store, HttpContext context, ILoggerFactory loggers) =>
        {
            store.Set(queue, null);
            Audit(loggers, context, "Queue {Queue} back to the default settings", queue);
            return Results.NoContent();
        });

        api.MapPost("/queues/{queue}/pause", (string queue, Broker broker, QueueDefinitionStore store, HttpContext context, ILoggerFactory loggers) =>
        {
            store.Set(queue, (broker.GetQueueOptions(queue) ?? new QueueOptions()) with { Paused = true });
            Audit(loggers, context, "Queue {Queue} paused", queue);
            return Results.NoContent();
        });

        api.MapPost("/queues/{queue}/resume", (string queue, Broker broker, QueueDefinitionStore store, HttpContext context, ILoggerFactory loggers) =>
        {
            var resumed = (broker.GetQueueOptions(queue) ?? new QueueOptions()) with { Paused = false };
            // A definition that says nothing else is no definition at all.
            store.Set(queue, resumed == new QueueOptions() ? null : resumed);
            Audit(loggers, context, "Queue {Queue} resumed", queue);
            return Results.NoContent();
        });

        api.MapPost("/queues/{queue}/purge", (string queue, Broker broker, HttpContext context, ILoggerFactory loggers) =>
        {
            var purged = broker.Purge(queue);
            Audit(loggers, context, "Queue {Queue} emptied: {Count} messages thrown away", queue, purged);
            return Results.Json(new { purged });
        });

        // ── Dead letters ────────────────────────────────────────────────────

        api.MapGet("/dead-letters", (Broker broker) => Results.Json(broker.GetDeadLetterSummary()));

        api.MapGet("/dead-letters/{queue}", (string queue, Broker broker) =>
            Results.Json(broker.GetDeadLetters(queue).Select(DeadLetter)));

        api.MapPost("/dead-letters/{queue}/{id}/requeue", (string queue, string id, Broker broker, HttpContext context, ILoggerFactory loggers) =>
        {
            if (broker.RequeueDeadLetter(queue, id) is not { } messageId)
                return Results.NotFound();
            Audit(loggers, context, "Dead letter {Id} of queue {Queue} was requeued as {MessageId}", id, queue, messageId);
            return Results.Json(new { messageId });
        });

        api.MapDelete("/dead-letters/{queue}/{id}", (string queue, string id, Broker broker, HttpContext context, ILoggerFactory loggers) =>
        {
            if (!broker.DiscardDeadLetter(queue, id))
                return Results.NotFound();
            Audit(loggers, context, "Dead letter {Id} of queue {Queue} was discarded", id, queue);
            return Results.NoContent();
        });

        api.MapDelete("/dead-letters/{queue}", (string queue, Broker broker, HttpContext context, ILoggerFactory loggers) =>
        {
            var discarded = broker.DiscardDeadLetters(queue);
            Audit(loggers, context, "The {Count} dead letters of queue {Queue} were discarded", discarded, queue);
            return Results.Json(new { discarded });
        });

        // ── Worker Control ──────────────────────────────────────────────────
        // Each route is one command of the Worker Control administration contract, sent over
        // its queue. The answer goes back as it came, minus the envelope.

        var workers = api.MapGroup("/workers");

        workers.MapGet("/status", (WorkerControlClient client, HttpContext context) => Forward(client, context, "Status"));

        workers.MapGet("/config", (WorkerControlClient client, HttpContext context) => Forward(client, context, "GetConfig"));

        workers.MapPut("/config", (ConfigRequest body, WorkerControlClient client, HttpContext context, ILoggerFactory loggers) =>
            Forward(client, context, "SetConfig", request => request["Config"] = body.Config?.DeepClone(), loggers, "Worker Control: configuration replaced"));

        workers.MapPost("/groups/{group}/enabled", (string group, EnabledRequest body, WorkerControlClient client, HttpContext context, ILoggerFactory loggers) =>
            Forward(client, context, "SetGroupEnabled", request =>
            {
                request["Group"] = group;
                request["Enabled"] = body.Enabled;
            }, loggers, $"Worker Control: group {group} {(body.Enabled ? "enabled" : "disabled")}"));

        workers.MapPost("/groups/{group}/workers", (string group, WorkersRequest body, WorkerControlClient client, HttpContext context, ILoggerFactory loggers) =>
            Forward(client, context, "SetGroupWorkers", request =>
            {
                request["Group"] = group;
                request["TotalWorkers"] = body.TotalWorkers;
            }, loggers, $"Worker Control: group {group} set to {body.TotalWorkers} workers"));

        workers.MapPost("/groups/{group}/restart", (string group, WorkerControlClient client, HttpContext context, ILoggerFactory loggers) =>
            Forward(client, context, "RestartGroup", request => request["Group"] = group, loggers, $"Worker Control: restart of group {group}"));

        workers.MapPost("/processes/{pid:int}/restart", (int pid, WorkerControlClient client, HttpContext context, ILoggerFactory loggers) =>
            Forward(client, context, "RestartWorker", request => request["ProcessId"] = pid, loggers, $"Worker Control: restart of worker {pid}"));

        // The traffic the Worker Control reads from the access log of the reverse proxy.
        static void Period(System.Text.Json.Nodes.JsonObject request, int? minutes, string? kind, string? host, string? app)
        {
            request["Minutes"] = Math.Clamp(minutes ?? 60, 1, 60 * 24 * 366);
            if (kind is "api" or "static")
                request["Kind"] = kind;
            if (!string.IsNullOrEmpty(host))
                request["Host"] = host;
            if (!string.IsNullOrEmpty(app))
                request["App"] = app;
        }

        workers.MapGet("/traffic", (int? minutes, string? kind, string? host, string? app, WorkerControlClient client, HttpContext context) =>
            Forward(client, context, "Traffic", request => Period(request, minutes, kind, host, app)));

        workers.MapGet("/traffic/routes", (int? minutes, string? kind, string? host, string? app, string? search, string? sort, int? limit, WorkerControlClient client, HttpContext context) =>
            Forward(client, context, "TrafficRoutes", request =>
            {
                Period(request, minutes, kind, host, app);
                if (!string.IsNullOrWhiteSpace(search))
                    request["Search"] = search;
                if (!string.IsNullOrEmpty(sort))
                    request["Sort"] = sort;
                request["Limit"] = Math.Clamp(limit ?? 100, 1, 500);
            }));

        // Where the proxy sent the requests, and which processes are behind each address.
        workers.MapGet("/traffic/upstreams", (int? minutes, WorkerControlClient client, HttpContext context) =>
            Forward(client, context, "TrafficUpstreams", request => request["Minutes"] = Math.Clamp(minutes ?? 60, 1, 60 * 24 * 366)));

        // The screens the requests came from; "names" asks how much the screens under each name were used.
        workers.MapGet("/traffic/pages", (int? minutes, string? host, string? search, int? limit, string? names, WorkerControlClient client, HttpContext context) =>
            Forward(client, context, "TrafficPages", request =>
            {
                Period(request, minutes, null, host, null);
                if (!string.IsNullOrWhiteSpace(search))
                    request["Search"] = search;
                request["Limit"] = Math.Clamp(limit ?? 50, 1, 500);
                request["Names"] = new System.Text.Json.Nodes.JsonArray([.. (names ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(100).Select(name => (System.Text.Json.Nodes.JsonNode)name)]);
            }));

        workers.MapGet("/traffic/errors", (string? host, string? app, int? limit, WorkerControlClient client, HttpContext context) =>
            Forward(client, context, "TrafficErrors", request =>
            {
                if (!string.IsNullOrEmpty(host))
                    request["Host"] = host;
                if (!string.IsNullOrEmpty(app))
                    request["App"] = app;
                request["Limit"] = Math.Clamp(limit ?? 100, 1, 1000);
            }));

        // The micro frontends published on the machine of the Worker Control.
        workers.MapGet("/frontends", (WorkerControlClient client, HttpContext context) => Forward(client, context, "Frontends"));

        // The Windows services the Worker Control watches without having started them.
        workers.MapGet("/services/installed", (WorkerControlClient client, HttpContext context) => Forward(client, context, "ListServices"));

        foreach (var (action, command) in new[] { ("start", "StartService"), ("stop", "StopService"), ("restart", "RestartService") })
        {
            workers.MapPost($"/services/{{name}}/{action}", (string name, WorkerControlClient client, HttpContext context, ILoggerFactory loggers) =>
                Forward(client, context, command, request => request["Name"] = name, loggers, $"Worker Control: {action} of the service {name}"));
        }

        workers.MapGet("/events", (string? group, string? kind, DateTimeOffset? from, DateTimeOffset? to, int? limit, WorkerControlClient client, HttpContext context) =>
            Forward(client, context, "Events", request =>
            {
                if (!string.IsNullOrEmpty(group)) request["Group"] = group;
                if (!string.IsNullOrEmpty(kind)) request["Kind"] = kind;
                if (from is not null) request["From"] = from;
                if (to is not null) request["To"] = to;
                request["Limit"] = limit ?? 200;
            }));

        workers.MapGet("/health", (string? group, int? pid, DateTimeOffset? from, DateTimeOffset? to, int? limit, WorkerControlClient client, HttpContext context) =>
            Forward(client, context, "Health", request =>
            {
                if (!string.IsNullOrEmpty(group)) request["Group"] = group;
                if (pid is not null) request["ProcessId"] = pid;
                if (from is not null) request["From"] = from;
                if (to is not null) request["To"] = to;
                request["Limit"] = limit ?? 1000;
            }));

        workers.MapPost("/detach", (WorkerControlClient client, HttpContext context, ILoggerFactory loggers) =>
            Forward(client, context, "DetachAndStop", null, loggers, "Worker Control: asked to stop leaving the workers running"));

        // ── Trace ───────────────────────────────────────────────────────────

        // Server-sent events: the state of the trace of one process ("state") and its lines
        // ("lines"), for as long as the request stays open. Watching is what turns the trace on.
        api.MapGet("/trace/{pid:int}", async (int pid, HttpContext context, TraceHub hub, IHostApplicationLifetime lifetime, ILoggerFactory loggers) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            Audit(loggers, context, "Trace of process {ProcessId} watched", pid);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            using var subscription = hub.Subscribe(pid);
            try
            {
                while (true)
                {
                    // Something goes out now and then even when the process is silent, so that
                    // nothing between here and the browser closes the connection as idle.
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    quiet.CancelAfter(TimeSpan.FromSeconds(15));
                    bool more;
                    try
                    {
                        more = await subscription.Events.WaitToReadAsync(quiet.Token);
                    }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                    {
                        await context.Response.WriteAsync(": idle\n\n", stop.Token);
                        await context.Response.Body.FlushAsync(stop.Token);
                        continue;
                    }
                    if (!more)
                        break;

                    while (subscription.Events.TryRead(out var item))
                    {
                        if (item.State is not null)
                            await context.Response.WriteAsync($"event: state\ndata: {JsonSerializer.Serialize(item.State, Web)}\n\n", stop.Token);
                        if (item.Lines is { Count: > 0 })
                            await context.Response.WriteAsync($"event: lines\ndata: {JsonSerializer.Serialize(item.Lines, Web)}\n\n", stop.Token);
                    }
                    await context.Response.Body.FlushAsync(stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // The browser went away or the service is stopping.
            }
        });

        // The same for several processes at once, over one request: every event says which
        // process it is of. This is how the instances of a group are followed together, when
        // there is no telling which of them will take a message.
        api.MapGet("/trace", async (string? pids, HttpContext context, TraceHub hub, IHostApplicationLifetime lifetime, ILoggerFactory loggers) =>
        {
            var wanted = (pids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => int.TryParse(text, out var pid) && pid > 0 ? pid : 0)
                .Distinct()
                .ToList();
            if (wanted.Count == 0 || wanted.Count > 64 || wanted.Contains(0))
                return Results.Json(new { error = "\"pids\" takes 1 to 64 process ids, separated by commas" }, statusCode: StatusCodes.Status400BadRequest);

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            Audit(loggers, context, "Trace of processes {ProcessIds} watched", string.Join(", ", wanted));

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            // One writer at a time: the events of the processes arrive on their own.
            using var writing = new SemaphoreSlim(1, 1);
            var subscriptions = wanted.Select(pid => (Pid: pid, Subscription: hub.Subscribe(pid))).ToList();

            async Task WriteAsync(string text)
            {
                await writing.WaitAsync(stop.Token);
                try
                {
                    await context.Response.WriteAsync(text, stop.Token);
                    await context.Response.Body.FlushAsync(stop.Token);
                }
                finally
                {
                    writing.Release();
                }
            }

            async Task FollowAsync(int pid, TraceHub.TraceSubscription subscription)
            {
                await foreach (var item in subscription.Events.ReadAllAsync(stop.Token))
                {
                    var text = "";
                    if (item.State is not null)
                        text += $"event: state\ndata: {JsonSerializer.Serialize(new { pid, item.State.State, item.State.Message }, Web)}\n\n";
                    if (item.Lines is { Count: > 0 })
                        text += $"event: lines\ndata: {JsonSerializer.Serialize(new { pid, lines = item.Lines }, Web)}\n\n";
                    if (text.Length > 0)
                        await WriteAsync(text);
                }
            }

            async Task KeepOpenAsync()
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stop.Token);
                    await WriteAsync(": idle\n\n");
                }
            }

            try
            {
                await Task.WhenAll([KeepOpenAsync(), .. subscriptions.Select(item => FollowAsync(item.Pid, item.Subscription))]);
            }
            catch (OperationCanceledException)
            {
                // The browser went away or the service is stopping.
            }
            finally
            {
                // The first to end takes the others with it; nothing is left asking for trace.
                stop.Cancel();
                foreach (var (_, subscription) in subscriptions)
                    subscription.Dispose();
            }
            return Results.Empty;
        });

        // ── The map ─────────────────────────────────────────────────────────

        api.MapGet("/map", (int? minutes, Broker broker, V2Connections connections) =>
            Results.Json(ParkMap.Build(broker, connections, TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 1, (int)Broker.ActivityMemory.TotalMinutes)))));

        // ── Who is connected ────────────────────────────────────────────────

        api.MapGet("/connections", (Broker broker, V2Connections connections) => Results.Json(new
        {
            v2 = connections.All().OrderBy(connection => connection.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(connection => connection.Id, StringComparer.Ordinal).Select(Describe),
            v1 = V1Clients(broker)
        }));
    }

    public sealed record LoginRequest(string? User, string? Password);

    public sealed record ConfigRequest(System.Text.Json.Nodes.JsonNode? Config);

    public sealed record EnabledRequest(bool Enabled);

    public sealed record WorkersRequest(int TotalWorkers);

    /// <summary>
    /// Sends a command to the Worker Control and turns its answer into an HTTP one: what it
    /// answered when all went well; otherwise the reason, with a status that tells whether the
    /// request was wrong (4xx) or the Worker Control could not be reached (503).
    /// </summary>
    private static async Task<IResult> Forward(WorkerControlClient client, HttpContext context, string command,
        Action<System.Text.Json.Nodes.JsonObject>? more = null, ILoggerFactory? loggers = null, string? audit = null)
    {
        var answer = await client.AskAsync(command, context.Items[PanelHost.UserItem] as string, more, context.RequestAborted);
        if (!answer.Reached)
            return Results.Json(new { error = answer.Problem, code = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!answer.Ok)
        {
            var status = answer.ErrorCode switch
            {
                "not-found" => StatusCodes.Status404NotFound,
                "failed" or "history-unavailable" => StatusCodes.Status502BadGateway,
                "invalid-state" => StatusCodes.Status409Conflict,
                "unknown-command" => StatusCodes.Status501NotImplemented,
                _ => StatusCodes.Status400BadRequest
            };
            return Results.Json(new { error = answer.ErrorMessage, code = answer.ErrorCode }, statusCode: status);
        }

        if (audit is not null && loggers is not null)
            Audit(loggers, context, audit);
        // As the Worker Control wrote it: its contract uses its own names for the fields.
        return Results.Content(answer.Body!.ToJsonString(), "application/json; charset=utf-8");
    }

    private static object Overview(Broker broker, V2Connections connections, MetricsSampler sampler, DateTimeOffset startedAt)
    {
        var queues = broker.GetQueues();
        var dead = broker.GetDeadLetterSummary();
        var latest = sampler.Latest;
        return new
        {
            version = ServerHost.Version,
            startedAt,
            totals = broker.GetTotals(),
            rates = new
            {
                published = latest?.PublishedPerSecond ?? 0,
                delivered = latest?.DeliveredPerSecond ?? 0,
                confirmed = latest?.ConfirmedPerSecond ?? 0
            },
            queues = queues.Count,
            pending = queues.Sum(queue => queue.Pending),
            processing = queues.Sum(queue => queue.Processing),
            paused = queues.Count(queue => queue.Paused),
            withoutConsumer = queues.Count(queue => queue.Pending > 0 && queue.Consumers == 0),
            deadLetters = new
            {
                expired = dead.Sum(item => item.Expired),
                notConsumed = dead.Sum(item => item.NotConsumed),
                unconfirmed = dead.Sum(item => item.Unconfirmed)
            },
            connections = connections.Count,
            applications = connections.All().Select(connection => connection.ClientName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            v1Clients = V1Clients(broker).Count
        };
    }

    private static List<object> Queues(Broker broker)
    {
        var dead = broker.GetDeadLetterSummary().ToDictionary(item => item.Queue, StringComparer.Ordinal);
        var definitions = broker.GetAllQueueOptions();
        var listed = new List<object>();
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var queue in broker.GetQueues())
        {
            live.Add(queue.Name);
            dead.TryGetValue(queue.Name, out var letters);
            listed.Add(new
            {
                name = queue.Name,
                exists = true,
                defined = definitions.ContainsKey(queue.Name),
                queue.Paused,
                queue.Pending,
                queue.Processing,
                queue.AwaitingResponse,
                queue.Consumers,
                queue.Published,
                queue.Delivered,
                queue.Confirmed,
                deadLetters = (letters?.Expired ?? 0) + (letters?.NotConsumed ?? 0) + (letters?.Unconfirmed ?? 0)
            });
        }

        // A queue somebody defined shows up even while it has no messages and no consumers.
        foreach (var (name, definition) in definitions.Where(pair => !live.Contains(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            dead.TryGetValue(name, out var letters);
            listed.Add(new
            {
                name,
                exists = false,
                defined = true,
                definition.Paused,
                Pending = 0,
                Processing = 0,
                AwaitingResponse = 0,
                Consumers = 0,
                Published = 0L,
                Delivered = 0L,
                Confirmed = 0L,
                deadLetters = (letters?.Expired ?? 0) + (letters?.NotConsumed ?? 0) + (letters?.Unconfirmed ?? 0)
            });
        }
        return listed;
    }

    private static object Describe(V2Connection connection) => new
    {
        id = connection.Id,
        application = connection.ClientName,
        pid = connection.ProcessId,
        host = connection.Host,
        wrapper = connection.Wrapper,
        connectedAt = connection.ConnectedAt,
        busy = connection.IsBusy,
        queues = connection.GetBoundQueues()
    };

    /// <summary>
    /// A 1.x client says nothing about itself: they are told apart by address only.
    /// </summary>
    private static List<object> V1Clients(Broker broker)
    {
        var clients = new SortedDictionary<string, (DateTimeOffset Last, SortedSet<string> Asks, SortedSet<string> Publishes)>(StringComparer.Ordinal);
        foreach (var activity in broker.GetActivity(ActivityWindow))
        {
            foreach (var (party, asks) in activity.Askers.Select(party => (party, true)).Concat(activity.Publishers.Select(party => (party, false))))
            {
                if (!party.Name.StartsWith("v1:", StringComparison.Ordinal))
                    continue;
                var address = party.Name[3..];
                if (!clients.TryGetValue(address, out var client))
                    client = (party.LastSeen, new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                (asks ? client.Asks : client.Publishes).Add(activity.Queue);
                clients[address] = (party.LastSeen > client.Last ? party.LastSeen : client.Last, client.Asks, client.Publishes);
            }
        }
        return clients.Select(pair => (object)new
        {
            address = pair.Key,
            lastSeen = pair.Value.Last,
            consumes = pair.Value.Asks,
            publishes = pair.Value.Publishes
        }).ToList();
    }

    /// <summary>
    /// "v1:address" or "v2:connection|application|host|pid", as the protocol layers record them.
    /// </summary>
    private static object Party(QueueParty party)
    {
        if (party.Name.StartsWith("v2:", StringComparison.Ordinal) && party.Name[3..].Split('|') is [var id, var application, var host, var pid])
            return new { protocol = "v2", connection = id, application, host, pid, lastSeen = party.LastSeen, count = party.Count };
        return new { protocol = "v1", connection = (string?)null, application = party.Name[(party.Name.IndexOf(':') + 1)..], host = (string?)null, pid = (string?)null, lastSeen = party.LastSeen, count = party.Count };
    }

    private static object DeadLetter(DeadLetter letter) => new
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
        body = Body(letter.Body)
    };

    private static JsonElement Body(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static void Audit(ILoggerFactory loggers, HttpContext context, string message, params object?[] arguments) =>
        loggers.CreateLogger("ZapMQ.Panel").LogInformation(message + " (by {User})", [.. arguments, context.Items[PanelHost.UserItem]]);
}
