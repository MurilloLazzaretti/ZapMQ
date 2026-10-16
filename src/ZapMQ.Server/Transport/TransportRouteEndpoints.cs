using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Transport;

/// <summary>
/// The routes between environments: their configuration in the panel, under the login and changed
/// by the master only, and the door through which another environment hands packages in, outside
/// the login and open only to what an authorized origin signed.
/// </summary>
public static class TransportRouteEndpoints
{
    public sealed record RouteRequest(string? Name, string? Url, string? Key, bool AllowHttp);

    public sealed record OriginRequest(string? Name);

    public sealed record SendRequest(string? Route);

    public sealed record CompleteRequest(long Size, string? Sha256);

    public sealed record CompareObjectRequest(string? Kind, string? Schema, string? Name);

    public sealed record CompareTargetRequest(string? Kind, string? Name);

    private const string Inbound = "/api/transport/inbound";

    private static string User(HttpContext context) => (string)context.Items[PanelHost.UserItem]!;

    private static IResult? NotMaster(HttpContext context, PanelUserStore users) =>
        users.Find(User(context)) is { Master: true } ? null : Results.Json(new { error = "Só o usuário master configura as rotas de transporte" }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult Refused(TransportRefused refused) => Results.Json(new { error = refused.Message }, statusCode: refused.Status);

    private static object Shown(TransportRoute route) => new
    {
        id = route.Id, name = route.Name, url = route.Url, allowHttp = route.AllowHttp, createdAt = route.CreatedAt, createdBy = route.CreatedBy,
        lastUsedAt = route.LastUsedAt, lastProblem = route.LastProblem,
        // Whether this machine can still read the key: a file brought from another one cannot.
        keyReadable = TransportSecret.Unprotect(route.Key) is not null
    };

    private static object Shown(TransportOrigin origin) => new { name = origin.Name, createdAt = origin.CreatedAt, createdBy = origin.CreatedBy, lastSeenAt = origin.LastSeenAt };

    public static void MapTransportRoutes(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/transport");

        api.MapGet("/routes", (TransportRouteStore store, TransportStore packages) =>
            Results.Json(new { environment = packages.Environment, routes = store.Routes().Select(Shown), origins = store.Origins().Select(Shown) }));

        api.MapPost("/routes", (RouteRequest request, TransportRouteStore store, PanelUserStore users, HttpContext context, TimeProvider time, ILoggerFactory loggers) =>
        {
            if (NotMaster(context, users) is { } refused)
                return refused;
            try
            {
                var route = store.AddRoute(request.Name, request.Url, request.Key, request.AllowHttp, User(context), time.GetUtcNow());
                loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: route {Name} to {Url} added (by {User})", route.Name, route.Url, User(context));
                return Results.Json(Shown(route), statusCode: StatusCodes.Status201Created);
            }
            catch (TransportRefused error)
            {
                return Refused(error);
            }
        });

        api.MapPut("/routes/{id}", (string id, RouteRequest request, TransportRouteStore store, PanelUserStore users, HttpContext context, ILoggerFactory loggers) =>
        {
            if (NotMaster(context, users) is { } refused)
                return refused;
            try
            {
                var route = store.ChangeRoute(id, request.Name, request.Url, request.Key, request.AllowHttp);
                loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: route {Name} to {Url} changed{Key} (by {User})", route.Name, route.Url,
                    string.IsNullOrWhiteSpace(request.Key) ? "" : ", with a new key", User(context));
                return Results.Json(Shown(route));
            }
            catch (TransportRefused error)
            {
                return Refused(error);
            }
        });

        api.MapDelete("/routes/{id}", (string id, TransportRouteStore store, PanelUserStore users, HttpContext context, ILoggerFactory loggers) =>
        {
            if (NotMaster(context, users) is { } refused)
                return refused;
            if (!store.RemoveRoute(id))
                return Results.Json(new { error = "Não existe essa rota" }, statusCode: StatusCodes.Status404NotFound);
            loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: route {Id} removed (by {User})", id, User(context));
            return Results.NoContent();
        });

        api.MapPost("/routes/{id}/test", async (string id, TransportSender sender, HttpContext context) =>
        {
            try
            {
                return Results.Json(new { environment = await sender.TestAsync(id, context.RequestAborted) });
            }
            catch (TransportRefused error)
            {
                return Refused(error);
            }
        });

        // The key of a new origin is in this answer and nowhere else, ever again.
        api.MapPost("/origins", (OriginRequest request, TransportRouteStore store, PanelUserStore users, HttpContext context, TimeProvider time, ILoggerFactory loggers) =>
        {
            if (NotMaster(context, users) is { } refused)
                return refused;
            try
            {
                var (origin, key) = store.AddOrigin(request.Name, User(context), time.GetUtcNow());
                loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: {Origin} may send packages here from now on (by {User})", origin.Name, User(context));
                return Results.Json(new { origin = Shown(origin), key }, statusCode: StatusCodes.Status201Created);
            }
            catch (TransportRefused error)
            {
                return Refused(error);
            }
        });

        api.MapDelete("/origins/{name}", (string name, TransportRouteStore store, PanelUserStore users, HttpContext context, ILoggerFactory loggers) =>
        {
            if (NotMaster(context, users) is { } refused)
                return refused;
            if (!store.RemoveOrigin(name))
                return Results.Json(new { error = "Não existe essa origem" }, statusCode: StatusCodes.Status404NotFound);
            loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: {Origin} may no longer send packages here (by {User})", name, User(context));
            return Results.NoContent();
        });

        api.MapPost("/packages/{id}/send", (string id, SendRequest request, TransportSender sender, HttpContext context, ILoggerFactory loggers) =>
        {
            try
            {
                var sending = sender.Start(id, request.Route ?? "", User(context));
                loggers.CreateLogger("ZapMQ.Panel").LogInformation("Transport: package {Id} being sent to {Route} (by {User})", id, sending.Route, User(context));
                return Results.Json(sending, statusCode: StatusCodes.Status202Accepted);
            }
            catch (TransportRefused error)
            {
                return Refused(error);
            }
        });

        api.MapGet("/packages/{id}/sends", (string id, TransportSender sender) => Results.Json(sender.Of(id)));

        // This environment beside another one, reached by a route: what differs, and which side is newer.
        api.MapPost("/compare/{route}", async (string route, Comparison comparison, HttpContext context) =>
        {
            try
            {
                return Results.Json(await comparison.Compare(route, User(context), context.RequestAborted));
            }
            catch (TransportRefused error)
            {
                return Refused(error);
            }
        });

        // The last comparison with the route, while it is kept.
        api.MapGet("/compare/{route}", (string route, Comparison comparison) =>
            comparison.Last(route) is { } last ? Results.Content(last.ToJsonString(), "application/json; charset=utf-8") : Results.NoContent());

        api.MapGet("/compare/{route}/object", async (string route, string? kind, string? schema, string? name, Comparison comparison, HttpContext context) =>
        {
            try
            {
                return Results.Json(await comparison.Object(route, kind, schema, name, User(context), context.RequestAborted));
            }
            catch (TransportRefused error)
            {
                return Refused(error);
            }
        });

        api.MapGet("/compare/{route}/target", async (string route, string? kind, string? name, Comparison comparison, HttpContext context) =>
        {
            try
            {
                return Results.Json(await comparison.Target(route, kind, name, User(context), context.RequestAborted));
            }
            catch (TransportRefused error)
            {
                return Refused(error);
            }
        });

        MapInbound(routes);
    }

    /// <summary>
    /// What another environment calls. Each request is read whole, checked against its signature,
    /// and only then looked at.
    /// </summary>
    private static void MapInbound(IEndpointRouteBuilder routes)
    {
        var inbound = routes.MapGroup(Inbound);

        static async Task<(byte[] Body, string Path)> Read(HttpContext context)
        {
            using var memory = new MemoryStream();
            await context.Request.Body.CopyToAsync(memory, context.RequestAborted);
            var below = context.Request.Path.Value![Inbound.Length..];
            return (memory.ToArray(), below);
        }

        static T? Parsed<T>(byte[] body) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        static IResult Unsigned(string problem) => Results.Json(new { error = "Recusado por este ambiente: " + problem }, statusCode: StatusCodes.Status401Unauthorized);

        inbound.MapPost("/hello", async (HttpContext context, TransportInbound door, TransportStore store) =>
        {
            var (body, path) = await Read(context);
            var (origin, problem) = door.Check(context, path, body);
            if (origin is null)
                return Unsigned(problem!);
            return Results.Json(new { environment = store.Environment, version = ServerHost.Version });
        });

        // What another environment asks to compare itself with this one: what is here, never what is in the tables.
        inbound.MapPost("/compare/inventory", async (HttpContext context, TransportInbound door, Comparison comparison) =>
        {
            var (body, path) = await Read(context);
            var (origin, problem) = door.Check(context, path, body);
            if (origin is null)
                return Unsigned(problem!);
            return Results.Json(await comparison.Here("transporte:" + origin, context.RequestAborted));
        });

        inbound.MapPost("/compare/object", async (HttpContext context, TransportInbound door, Comparison comparison) =>
        {
            var (body, path) = await Read(context);
            var (origin, problem) = door.Check(context, path, body);
            if (origin is null)
                return Unsigned(problem!);
            var request = Parsed<CompareObjectRequest>(body);
            if (request is not { Kind.Length: > 0, Schema.Length: > 0, Name.Length: > 0 })
                return Results.Json(new { error = "Pedido inválido" }, statusCode: StatusCodes.Status400BadRequest);
            return Results.Json(new { script = await comparison.Script(request.Kind!, request.Schema!, request.Name!, "transporte:" + origin, context.RequestAborted) });
        });

        inbound.MapPost("/compare/target", async (HttpContext context, TransportInbound door, Comparison comparison) =>
        {
            var (body, path) = await Read(context);
            var (origin, problem) = door.Check(context, path, body);
            if (origin is null)
                return Unsigned(problem!);
            var request = Parsed<CompareTargetRequest>(body);
            if (request is not { Kind.Length: > 0, Name.Length: > 0 })
                return Results.Json(new { error = "Pedido inválido" }, statusCode: StatusCodes.Status400BadRequest);
            return Results.Json(new { files = await comparison.Files(request.Kind!, request.Name!, "transporte:" + origin, context.RequestAborted) });
        });

        inbound.MapGet("/packages/{id}", async (string id, HttpContext context, TransportInbound door) =>
        {
            var (body, path) = await Read(context);
            var (origin, problem) = door.Check(context, path, body);
            if (origin is null)
                return Unsigned(problem!);
            return Results.Json(new { received = door.Received(origin, id), here = door.Has(id) });
        });

        inbound.MapPut("/packages/{id}/parts/{offset:long}", async (string id, long offset, HttpContext context, TransportInbound door) =>
        {
            var (body, path) = await Read(context);
            var (origin, problem) = door.Check(context, path, body);
            if (origin is null)
                return Unsigned(problem!);
            var (ok, received, refused) = door.Part(origin, id, offset, body);
            return ok ? Results.Json(new { received }) : Results.Json(new { error = refused, received }, statusCode: StatusCodes.Status409Conflict);
        }).WithMetadata(new RequestSizeLimitAttribute(TransportInbound.MaxPart + 1024));

        inbound.MapPost("/packages/{id}/complete", async (string id, HttpContext context, TransportInbound door, TransportStore store) =>
        {
            var (body, path) = await Read(context);
            var (origin, problem) = door.Check(context, path, body);
            if (origin is null)
                return Unsigned(problem!);
            CompleteRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<CompleteRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                request = null;
            }
            if (request is null)
                return Results.Json(new { error = "Pedido inválido" }, statusCode: StatusCodes.Status400BadRequest);
            var (record, alreadyHere, refused) = door.Complete(origin, id, request.Size, request.Sha256);
            if (refused is not null)
                return Results.Json(new { error = refused }, statusCode: StatusCodes.Status422UnprocessableEntity);
            return Results.Json(new { environment = store.Environment, id, alreadyHere, status = record?.Status ?? "Pending" });
        });
    }
}
