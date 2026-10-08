using Microsoft.AspNetCore.Http.Features;
using ZapMQ.Core;

namespace ZapMQ.Server.V1;

/// <summary>
/// The 1.x protocol: DataSnap REST calls to <c>TZapMethods</c>, every parameter in the URL path.
/// Existing wrappers keep talking to the server through these routes, so the answers follow the
/// 1.x server case by case (see tests/contract).
/// </summary>
public static class DataSnapEndpoints
{
    private const string Prefix = "/datasnap/rest/TZapMethods/";

    private sealed record Method(string Name, int Parameters, Func<Broker, string[], string> Invoke);

    private static readonly Method[] Methods =
    [
        new("GetMessage", 1, GetMessage),
        new("UpdateMessage", 2, UpdateMessage),
        new("GetRPCResponse", 2, GetRpcResponse),
        new("UpdateRPCResponse", 3, UpdateRpcResponse)
    ];

    public static void MapDataSnap(this IEndpointRouteBuilder routes) =>
        routes.Map(Prefix + "{**call}", Handle);

    private static async Task Handle(HttpContext context, Broker broker, V1Callers callers, ILoggerFactory loggers)
    {
        byte[] payload;
        try
        {
            // The handlers run right here, on this thread, before anything is awaited.
            _caller = callers.Party(context);
            payload = V1Message.Envelope(Invoke(broker, context.Request.Method, ReadCall(context)));
        }
        catch (Exception error) when (error is V1Exception or ArgumentException)
        {
            // The start of the request line goes along: it is what tells apart a client that
            // speaks the protocol differently from one that sent bad data.
            var target = context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget;
            loggers.CreateLogger(typeof(DataSnapEndpoints)).LogWarning(
                "Rejected 1.x call: {Reason} | {Verb} {Target}", error.Message, context.Request.Method, target.Length > 200 ? target[..200] : target);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            payload = V1Message.Error(error.Message);
        }

        // Same headers a DataSnap server sends. Its clients read the session from Pragma; the
        // broker keeps no session, but the response keeps the shape those clients are used to.
        context.Response.Headers.Pragma = $"dssession={Random.Shared.Next(100000, 999999)}.{Random.Shared.Next(100000, 999999)}.{Random.Shared.Next(100000, 999999)},dssessionexpires=1000";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers.Server = "DatasnapHTTPService/2011";
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload);
    }

    /// <summary>
    /// Splits the call into method and parameters from the request line as it was sent. The
    /// decoded path cannot be used: a message containing an escaped slash would be split in two.
    /// </summary>
    /// <summary>
    /// A 1.x client says nothing about itself; who uses a queue is told by <see cref="V1Callers"/>.
    /// </summary>
    [ThreadStatic]
    private static string? _caller;

    private static string[] ReadCall(HttpContext context)
    {
        var target = context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget;
        var start = target.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new V1Exception("Invalid request");

        var path = target[(start + Prefix.Length)..];
        var query = path.IndexOf('?');
        if (query >= 0)
            path = path[..query];

        // The Delphi DataSnap client ends every URL with a slash; that is not one more parameter.
        var segments = path.TrimEnd('/').Split('/');
        for (var i = 0; i < segments.Length; i++)
            segments[i] = Uri.UnescapeDataString(segments[i]);
        return segments;
    }

    private static string Invoke(Broker broker, string verb, string[] call)
    {
        // DataSnap maps the HTTP verb to a method-name prefix, and no method here has one: only
        // GET reaches a method.
        var name = verb.ToUpperInvariant() switch
        {
            "GET" => call[0],
            "POST" => "update" + call[0],
            "PUT" => "accept" + call[0],
            "DELETE" => "cancel" + call[0],
            _ => call[0]
        };

        var method = Array.Find(Methods, m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new V1Exception($"TZapMethods.{name} method not found in the server method list");

        var received = call.Length - 1;
        if (received > method.Parameters)
            throw new V1Exception(
                $"Server method TZapMethods.{method.Name} consumed only {method.Parameters} input parameters out of {received}. " +
                "Either call with more parameters or change method definition.");

        // Parameters left out arrive as empty text.
        var parameters = new string[method.Parameters];
        for (var i = 0; i < parameters.Length; i++)
            parameters[i] = i < received ? call[i + 1] : string.Empty;

        return method.Invoke(broker, parameters);
    }

    private static string GetMessage(Broker broker, string[] parameters) =>
        broker.Take(parameters[0], _caller) is { } message ? V1Message.Serialize(message) : string.Empty;

    private static string UpdateMessage(Broker broker, string[] parameters)
    {
        var publication = V1Message.ParsePublication(parameters[1]);
        return broker.Publish(parameters[0], publication.Body, publication.Rpc, publication.Ttl, publisher: _caller);
    }

    private static string GetRpcResponse(Broker broker, string[] parameters) =>
        broker.TakeResponse(parameters[0], parameters[1]) is { } message ? V1Message.Serialize(message) : string.Empty;

    private static string UpdateRpcResponse(Broker broker, string[] parameters)
    {
        switch (V1Message.ParseResponse(parameters[2], out var response))
        {
            case V1Message.ResponseKind.Object:
                return broker.Respond(parameters[0], parameters[1], response, includeUndelivered: true) ? "OK" : string.Empty;

            // The message is looked up before the payload is examined, so this only fails for a
            // message that exists.
            case V1Message.ResponseKind.NotAnObject when broker.Contains(parameters[0], parameters[1]):
                throw V1Message.NotAnObjectError();

            default:
                return string.Empty;
        }
    }
}
