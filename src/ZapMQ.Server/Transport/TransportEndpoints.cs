using Microsoft.AspNetCore.Mvc;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Transport;

/// <summary>
/// The transport as the panel sees it, under <c>/api/transport</c>. All of it behind the login.
/// </summary>
public static class TransportEndpoints
{
    /// <summary>
    /// The most a package may weigh when it is brought in as a file.
    /// </summary>
    private const long MaxPackage = 512L * 1024 * 1024;

    public sealed record AddObjectRequest(string? Database, string? Kind, string? Schema, string? Name, bool Drop);

    public sealed record AddScriptRequest(string? Database, string? Title, string? Script);

    public sealed record AddTargetRequest(string? Kind, string? Name);

    public sealed record WriteFileRequest(string? Kind, string? Name, int Instance, string? Path, string? Content, string? Sha256, bool Restart);

    /// <summary>
    /// Asks the Worker Control and gives its answer as it came, with its own names for the fields.
    /// </summary>
    private static async Task<IResult> Relay(WorkerControlClient worker, HttpContext context, string command, Action<System.Text.Json.Nodes.JsonObject>? more = null,
        ILoggerFactory? loggers = null, string? audit = null, TimeSpan? patience = null)
    {
        var answer = patience is { } wait
            ? await worker.AskAsync(command, User(context), wait, more, context.RequestAborted)
            : await worker.AskAsync(command, User(context), more, context.RequestAborted);
        if (!answer.Reached)
            return Results.Json(new { error = answer.Problem, code = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        if (!answer.Ok)
            return Results.Json(new { error = answer.ErrorMessage, code = answer.ErrorCode }, statusCode: answer.ErrorCode switch
            {
                "not-found" => StatusCodes.Status404NotFound,
                "invalid-state" => StatusCodes.Status409Conflict,
                "unknown-command" => StatusCodes.Status501NotImplemented,
                _ => StatusCodes.Status400BadRequest
            });
        if (audit is not null && loggers is not null)
            loggers.CreateLogger("ZapMQ.Panel").LogInformation(audit + " (by {User})", User(context));
        return Results.Content(answer.Body!.ToJsonString(), "application/json; charset=utf-8");
    }

    public sealed record CloseRequest(string? Name, string? Description, List<string>? Items, bool KeepOrder);

    public sealed record ApproveRequest(DateTimeOffset? At);

    public sealed record RejectRequest(string? Reason);

    private static string User(HttpContext context) => (string)context.Items[PanelHost.UserItem]!;

    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TransportRefused refused)
        {
            return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
        }
    }

    /// <summary>
    /// An item without the list of its files, which is given apart, compared.
    /// </summary>
    private static object Shown(PackageItem item) => new
    {
        item.Number, item.Kind, item.Action, item.Name, item.Version, item.Fingerprint, item.Size, files = item.Files.Count
    };

    private static object Summary(PackageRecord record) => new
    {
        id = record.Manifest.Id, name = record.Manifest.Name, description = record.Manifest.Description, origin = record.Manifest.Origin,
        createdAt = record.Manifest.CreatedAt, createdBy = record.Manifest.CreatedBy, items = record.Manifest.Items.Count,
        status = record.Status, received = record.Received, deliveries = record.Deliveries, applyAt = record.ApplyAt, approvedBy = record.ApprovedBy, size = record.Size,
        changedAt = record.History.LastOrDefault()?.At ?? record.Manifest.CreatedAt
    };

    private static object Detail(PackageRecord record, TransportStore store) => new
    {
        environment = store.Environment,
        package = Summary(record),
        sha256 = record.Sha256,
        items = record.Manifest.Items.Select(item => new
        {
            item.Number, item.Kind, item.Action, item.ObjectKind, item.Variety, item.Schema, item.Name, item.Title, item.Database, item.Fingerprint, item.Base, item.Size,
            item.Version, files = item.Files.Count, uses = item.Uses.Count
        }),
        history = record.History,
        // What was there before is given with the item, when it is asked for.
        results = record.Results.Select(result => new { result.Number, result.Status, result.Did, result.Problem, result.Batch, result.Line, result.Messages, result.At, result.Backup, hasPrevious = result.Previous is not null })
    };

    public static void MapTransport(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/transport");

        api.MapGet("/summary", (TransportStore store) =>
        {
            var packages = store.Packages();
            return Results.Json(new
            {
                environment = store.Environment, area = store.Area().Count,
                pending = packages.Count(record => record.Status == "Pending"),
                scheduled = packages.Count(record => record.Status == "Approved"),
                troubled = packages.Count(record => record.Status is "Partial" or "Failed")
            });
        });

        // ── The area ────────────────────────────────────────────────────────

        api.MapGet("/area", (TransportStore store) => Results.Json(new { environment = store.Environment, items = store.Area() }));

        api.MapPost("/area/objects", (AddObjectRequest request, TransportService transport, HttpContext context) => Guarded(async () =>
            Results.Json(await transport.AddObject(request.Database, request.Kind, request.Schema, request.Name, request.Drop, User(context), context.RequestAborted), statusCode: StatusCodes.Status201Created)));

        api.MapPost("/area/scripts", (AddScriptRequest request, TransportService transport, HttpContext context) => Guarded(() =>
            Task.FromResult(Results.Json(transport.AddScript(request.Database, request.Title, request.Script, User(context)), statusCode: StatusCodes.Status201Created))));

        // What can be replaced on the machine of this environment.
        api.MapGet("/targets", (TransportService transport, HttpContext context) => Guarded(async () =>
            Results.Content((await transport.Targets(User(context), context.RequestAborted)).ToJsonString(), "application/json; charset=utf-8")));

        // Where the Worker Control takes new versions from, by kind.
        api.MapGet("/settings", (WorkerControlClient worker, HttpContext context) => Relay(worker, context, "TransportSettings"));

        api.MapPut("/settings/inboxes", (Dictionary<string, string?> inboxes, WorkerControlClient worker, HttpContext context, ILoggerFactory loggers) =>
            Relay(worker, context, "SetTransportInboxes", request =>
            {
                var said = new System.Text.Json.Nodes.JsonObject();
                foreach (var (kind, path) in inboxes)
                    said[kind] = path;
                request["Inboxes"] = said;
            }, loggers, "Transport: inboxes changed"));

        // The files of an application that belong to the environment: its configuration.
        api.MapGet("/files", (string? kind, string? name, WorkerControlClient worker, HttpContext context) =>
            Relay(worker, context, "TransportFiles", request => { request["Kind"] = kind; request["Name"] = name; }));

        api.MapGet("/file", (string? kind, string? name, int? instance, string? path, WorkerControlClient worker, HttpContext context, ILoggerFactory loggers) =>
            Relay(worker, context, "TransportFile", request => { request["Kind"] = kind; request["Name"] = name; request["Instance"] = instance ?? 0; request["Path"] = path; },
                // Who looked at a configuration is worth knowing: it may have what gives access to other things.
                loggers, $"Transport: file {path} of {kind} {name} opened"));

        api.MapPut("/file", (WriteFileRequest body, WorkerControlClient worker, HttpContext context, ILoggerFactory loggers) =>
            Relay(worker, context, "SetTransportFile", request =>
            {
                request["Kind"] = body.Kind;
                request["Name"] = body.Name;
                request["Instance"] = body.Instance;
                request["Path"] = body.Path;
                request["Content"] = body.Content;
                request["Sha256"] = body.Sha256;
                request["Restart"] = body.Restart;
            }, loggers, $"Transport: file {body.Path} of {body.Kind} {body.Name} written{(body.Restart ? ", asking for a restart" : "")}", TransportService.ReplacePatience));

        // What a target is running right now, packed and put in the area.
        api.MapPost("/area/running", (AddTargetRequest request, TransportService transport, HttpContext context) => Guarded(async () =>
            Results.Json(await transport.AddRunning(request.Kind, request.Name, incoming: false, User(context), context.RequestAborted), statusCode: StatusCodes.Status201Created)));

        // The new version somebody left for a target in the inbox of the machine.
        api.MapPost("/area/incoming", (AddTargetRequest request, TransportService transport, HttpContext context) => Guarded(async () =>
            Results.Json(await transport.AddRunning(request.Kind, request.Name, incoming: true, User(context), context.RequestAborted), statusCode: StatusCodes.Status201Created)));

        // The zip of a published folder, brought by somebody.
        api.MapPost("/area/upload", (string? kind, string? name, string? version, HttpContext context, TransportService transport) => Guarded(async () =>
        {
            using var memory = new MemoryStream();
            await context.Request.Body.CopyToAsync(memory, context.RequestAborted);
            return Results.Json(transport.AddUpload(kind, name, version, memory.ToArray(), User(context)), statusCode: StatusCodes.Status201Created);
        })).WithMetadata(new RequestSizeLimitAttribute(MaxPackage));

        api.MapDelete("/area/{id}", (string id, TransportStore store) => store.Remove(id) ? Results.NoContent() : Results.NotFound());

        // When each object last went into a package made here: what changed after that was not carried yet.
        api.MapGet("/packaged", (TransportService transport) => Results.Json(transport.LastPackaged()));

        // ── Packages ────────────────────────────────────────────────────────

        api.MapGet("/packages", (TransportStore store) => Results.Json(new { environment = store.Environment, packages = store.Packages().Select(Summary) }));

        api.MapPost("/packages", (CloseRequest request, TransportService transport, TransportStore store, HttpContext context) => Guarded(async () =>
            Results.Json(Detail(await transport.Close(request.Name, request.Description, request.Items, request.KeepOrder, User(context), context.RequestAborted), store), statusCode: StatusCodes.Status201Created)));

        api.MapPost("/packages/import", (HttpContext context, TransportStore store, TimeProvider time, ILoggerFactory loggers) => Guarded(async () =>
        {
            using var memory = new MemoryStream();
            await context.Request.Body.CopyToAsync(memory, context.RequestAborted);
            if (memory.Length == 0)
                throw new TransportRefused("Nenhum arquivo foi enviado");
            var record = store.Import(memory.ToArray(), User(context), time.GetUtcNow());
            loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: package {Name} ({Id}) from {Origin} received (by {User})", record.Manifest.Name, record.Manifest.Id, record.Manifest.Origin, User(context));
            return Results.Json(Detail(record, store), statusCode: StatusCodes.Status201Created);
        })).WithMetadata(new RequestSizeLimitAttribute(MaxPackage));

        api.MapGet("/packages/{id}", (string id, TransportStore store) =>
            store.Find(id) is { } record ? Results.Json(Detail(record, store)) : Results.Json(new { error = "Não existe esse pacote neste ambiente" }, statusCode: StatusCodes.Status404NotFound));

        // Only what was made here goes away; what arrived is refused and stays.
        api.MapDelete("/packages/{id}", (string id, TransportStore store, HttpContext context, TimeProvider time, ILoggerFactory loggers) => Guarded(() =>
        {
            var gone = store.Delete(id, User(context), time.GetUtcNow());
            loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: package {Name} ({Id}), made here with {Items} items, deleted (by {User})", gone.Manifest.Name, id, gone.Manifest.Items.Count, User(context));
            return Task.FromResult(Results.NoContent());
        }));

        api.MapGet("/packages/{id}/download", (string id, TransportStore store, HttpContext context, TimeProvider time) =>
        {
            if (store.Find(id) is not { } record || !File.Exists(store.FilePath(id)))
                return Results.NotFound();
            store.Update(id, downloaded => downloaded.History.Add(new HistoryEntry { At = time.GetUtcNow(), By = User(context), What = "downloaded" }));
            var name = string.Concat(record.Manifest.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')).Trim('-');
            return Results.File(store.FilePath(id), "application/zip", $"{(name.Length == 0 ? "pacote" : name)}-{id[..8]}.zpkg");
        });

        // How every item stands against what this environment has.
        api.MapGet("/packages/{id}/check", (string id, TransportStore store, TransportService transport, HttpContext context) => Guarded(async () =>
        {
            var record = store.Find(id) ?? throw new TransportRefused("Não existe esse pacote neste ambiente", StatusCodes.Status404NotFound);
            return Results.Json(new { checks = await transport.Check(record, User(context), context.RequestAborted) });
        }));

        // One item: what it brings, what is here now, and what was here before it was applied.
        api.MapGet("/packages/{id}/items/{number:int}", (string id, int number, TransportStore store, TransportService transport, HttpContext context) => Guarded(async () =>
        {
            var record = store.Find(id) ?? throw new TransportRefused("Não existe esse pacote neste ambiente", StatusCodes.Status404NotFound);
            var item = record.Manifest.Items.FirstOrDefault(candidate => candidate.Number == number) ?? throw new TransportRefused("O pacote não tem esse item", StatusCodes.Status404NotFound);
            var result = record.Results.FirstOrDefault(candidate => candidate.Number == number);
            if (item.Kind != "database")
            {
                // File by file against what the target has here; without a target, only what comes.
                try
                {
                    var (target, changes) = await transport.CompareFiles(item, User(context), context.RequestAborted);
                    return Results.Json(new { item = Shown(item), target = target is null ? null : System.Text.Json.JsonSerializer.Deserialize<object>(target.ToJsonString()), changes = target is null ? item.Files.Select(file => new FileChange(file.Path, "added", file.Size)) : changes, problem = (string?)null });
                }
                catch (TransportRefused refused)
                {
                    return Results.Json(new { item = Shown(item), target = (object?)null, changes = item.Files.Select(file => new FileChange(file.Path, "added", file.Size)), problem = refused.Message });
                }
            }
            string? brought, current = null, currentFingerprint = null, problem = null;
            try
            {
                (brought, current, currentFingerprint) = await transport.Compare(record, item, User(context), context.RequestAborted);
            }
            catch (TransportRefused refused)
            {
                // The item is still worth seeing without what is here.
                brought = item.Action == "Drop" ? null : store.Script(id, number);
                problem = refused.Message;
            }
            return Results.Json(new { item, script = brought, current, currentFingerprint, previous = result?.Previous, previousFingerprint = result?.PreviousFingerprint, problem });
        }));

        api.MapPost("/packages/{id}/approve", (string id, ApproveRequest request, TransportService transport, TransportStore store, HttpContext context, ILoggerFactory loggers) => Guarded(async () =>
        {
            var record = await transport.Approve(id, request.At, User(context), context.RequestAborted);
            loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: package {Name} ({Id}) approved to be applied at {At} (by {User})", record.Manifest.Name, id, record.ApplyAt, User(context));
            return Results.Json(Detail(record, store));
        }));

        api.MapPost("/packages/{id}/reject", (string id, RejectRequest request, TransportService transport, TransportStore store, HttpContext context, ILoggerFactory loggers) => Guarded(() =>
        {
            var record = transport.Reject(id, request.Reason, User(context));
            loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: package {Name} ({Id}) is now {Status} (by {User})", record.Manifest.Name, id, record.Status, User(context));
            return Task.FromResult(Results.Json(Detail(record, store)));
        }));
    }
}
