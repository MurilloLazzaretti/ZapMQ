using System.Collections.Concurrent;
using ZapMQ.Core;

namespace ZapMQ.Server.V2;

/// <summary>
/// The connections currently open, for the metrics and for an orderly shutdown.
/// </summary>
public sealed class V2Connections
{
    private readonly ConcurrentDictionary<string, V2Connection> _open = new();

    internal void Add(V2Connection connection) => _open[connection.Id] = connection;

    internal void Remove(V2Connection connection) => _open.TryRemove(connection.Id, out _);

    public int Count => _open.Count;

    /// <summary>
    /// The connections as they are, for whoever needs more than the summary below.
    /// </summary>
    internal IReadOnlyList<V2Connection> All() => [.. _open.Values];

    public IReadOnlyList<object> Describe() =>
        _open.Values
            .OrderBy(connection => connection.Id, StringComparer.Ordinal)
            .Select(connection => (object)new
            {
                id = connection.Id,
                client = connection.ClientName,
                pid = connection.ProcessId,
                host = connection.Host,
                wrapper = connection.Wrapper,
                connectedAt = connection.ConnectedAt,
                busy = connection.IsBusy,
                queues = connection.GetBoundQueues()
            })
            .ToList();

    /// <summary>
    /// Tells every client the server is going away and gives the messages being processed a
    /// moment to be confirmed.
    /// </summary>
    internal async Task DrainAsync(TimeSpan patience)
    {
        var bye = V2Frames.Bye("shutting-down");
        foreach (var connection in _open.Values)
            connection.Send(bye);

        var deadline = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < deadline && _open.Values.Any(connection => connection.IsBusy))
            await Task.Delay(50);
    }
}

public static class V2Endpoint
{
    public const string Path = "/v2";

    public static void MapV2(this WebApplication app, V2Options options)
    {
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(options.PingSeconds),
            KeepAliveTimeout = TimeSpan.FromSeconds(options.PingTimeoutSeconds)
        });

        app.Map(Path, async (HttpContext context, Broker broker, V2Connections connections, ILoggerFactory loggers, IHostApplicationLifetime lifetime) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }

            var logger = loggers.CreateLogger("ZapMQ.V2");
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var connection = new V2Connection(socket, broker, options, context.Connection.RemoteIpAddress?.ToString() ?? "", logger);

            connections.Add(connection);
            try
            {
                await connection.RunAsync(lifetime.ApplicationStopping);
            }
            finally
            {
                connections.Remove(connection);
                logger.LogInformation("v2 client disconnected: {Client}", connection.Description);
            }
        });
    }
}
