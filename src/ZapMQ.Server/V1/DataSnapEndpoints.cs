using Microsoft.AspNetCore.Http.Features;
using ZapMQ.Core;

namespace ZapMQ.Server.V1;

/// <summary>
/// The 1.x protocol: DataSnap REST calls to <c>TZapMethods</c>, every parameter in the URL path.
/// Existing wrappers keep talking to the server through these routes.
/// </summary>
public static class DataSnapEndpoints
{
    private const string Prefix = "/datasnap/rest/TZapMethods/";

    public static void MapDataSnap(this IEndpointRouteBuilder routes) =>
        routes.Map(Prefix + "{**call}", Handle);

    private static async Task Handle(HttpContext context, Broker broker, ILoggerFactory loggers)
    {
        byte[] payload;
        try
        {
            payload = V1Message.Envelope(Invoke(broker, ReadCall(context)));
        }
        catch (V1Exception error)
        {
            loggers.CreateLogger(typeof(DataSnapEndpoints)).LogWarning("Rejected 1.x call: {Reason}", error.Message);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            payload = V1Message.Error(error.Message);
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload);
    }

    /// <summary>
    /// Splits the call into method and parameters from the request line as it was sent. The
    /// decoded path cannot be used: a message containing an escaped slash would be split in two.
    /// </summary>
    private static string[] ReadCall(HttpContext context)
    {
        var target = context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget;
        var start = target.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new V1Exception("Invalid request");

        var segments = target[(start + Prefix.Length)..].Split('/');
        for (var i = 0; i < segments.Length; i++)
            segments[i] = Uri.UnescapeDataString(segments[i]);
        return segments;
    }

    private static string Invoke(Broker broker, string[] call)
    {
        var method = call[0];

        if (method.Equals("GetMessage", StringComparison.OrdinalIgnoreCase))
        {
            var queue = Parameters(call, 1)[0];
            return broker.Take(queue) is { } message ? V1Message.Serialize(message) : string.Empty;
        }

        if (method.Equals("UpdateMessage", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = Parameters(call, 2);
            var publication = V1Message.ParsePublication(parameters[1]);
            return broker.Publish(parameters[0], publication.Body, publication.Rpc, publication.Ttl);
        }

        if (method.Equals("GetRPCResponse", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = Parameters(call, 2);
            return broker.TakeResponse(parameters[0], parameters[1]) is { } message
                ? V1Message.Serialize(message)
                : string.Empty;
        }

        if (method.Equals("UpdateRPCResponse", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = Parameters(call, 3);
            var response = V1Message.ParseResponse(parameters[2]);
            return response is not null && broker.Respond(parameters[0], parameters[1], response)
                ? "OK"
                : string.Empty;
        }

        throw new V1Exception($"TZapMethods.{method} method not found in the server method list");
    }

    /// <summary>
    /// Returns the parameters of a call. The JSON payload is always the last one, so a client
    /// that left a slash unescaped inside it still gets its payload back in one piece.
    /// </summary>
    private static string[] Parameters(string[] call, int count)
    {
        if (call.Length - 1 < count || string.IsNullOrEmpty(call[1]))
            throw new V1Exception("Invalid number of parameters");

        var parameters = new string[count];
        Array.Copy(call, 1, parameters, 0, count - 1);
        parameters[count - 1] = string.Join('/', call, count, call.Length - count);
        return parameters;
    }
}
