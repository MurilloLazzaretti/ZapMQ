using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using ZapMQ.Core;
using ZapMQ.Server.V1;

namespace ZapMQ.Server.V2;

/// <summary>
/// One client connected over the v2 protocol: a WebSocket carrying one JSON object per text
/// frame. See docs/PROTOCOLO-V2.md.
/// </summary>
internal sealed class V2Connection(WebSocket socket, Broker broker, V2Options options, string remoteAddress, ILogger logger) : Consumer
{
    private static long _sequence;

    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private bool _greeted;

    public string Id { get; } = "c-" + Interlocked.Increment(ref _sequence).ToString("D6");

    public string ClientName { get; private set; } = "unknown";

    public int ProcessId { get; private set; }

    public string Host { get; private set; } = remoteAddress;

    public string Wrapper { get; private set; } = "";

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public override string Description => $"{ClientName} (pid {ProcessId} @ {Host}) {Id}";

    protected override void Deliver(BrokerMessage message) => Send(V2Frames.Deliver(message));

    protected override void DeliverResponse(BrokerMessage message) => Send(V2Frames.Response(message));

    public void Send(byte[] frame) => _outbox.Writer.TryWrite(frame);

    /// <summary>
    /// Serves the connection until either side closes it.
    /// </summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        var sending = SendLoopAsync(stopping);
        try
        {
            await ReceiveLoopAsync(stopping);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException)
        {
            // The client went away without a closing handshake, or the server is stopping.
        }
        finally
        {
            // From here on the broker hands this connection nothing; what it held is settled.
            broker.Disconnect(this);
            _outbox.Writer.TryComplete();
            try
            {
                await sending.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Whatever was still queued cannot be sent anymore.
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token);
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync(stopping))
                await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, stopping);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The receive loop notices the same failure and ends the connection.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken stopping)
    {
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();

        while (socket.State == WebSocketState.Open)
        {
            frame.SetLength(0);
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), stopping);
                if (received.MessageType == WebSocketMessageType.Close)
                    return;

                if (frame.Length + received.Count > options.MaxFrameBytes)
                {
                    Send(V2Frames.Error(null, "too-large", $"A frame cannot exceed {options.MaxFrameBytes} bytes"));
                    return;
                }
                frame.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);

            if (!Handle(frame.GetBuffer().AsMemory(0, (int)frame.Length)))
                return;
        }
    }

    /// <summary>
    /// Handles one frame. Returns false when the connection has to end.
    /// </summary>
    private bool Handle(ReadOnlyMemory<byte> frame)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            Send(V2Frames.Error(null, "invalid-request", "The frame is not valid JSON"));
            return true;
        }

        using (document)
        {
            var root = document.RootElement;
            long? id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idElement)
                       && idElement.TryGetInt64(out var number) ? number : null;

            if (root.ValueKind != JsonValueKind.Object || Text(root, "op") is not { } op)
            {
                Send(V2Frames.Error(id, "invalid-request", "The frame must be an object with an \"op\""));
                return true;
            }

            if (!_greeted && op != "hello")
            {
                Send(V2Frames.Error(id, "hello-required", "The first frame of a connection must be \"hello\""));
                return false;
            }

            try
            {
                Send(Execute(op, id ?? 0, root));
            }
            catch (V2Exception error)
            {
                Send(V2Frames.Error(id, error.Code, error.Message));
                return error.Code != "unsupported-protocol";
            }
            return true;
        }
    }

    private byte[] Execute(string op, long id, JsonElement request)
    {
        switch (op)
        {
            case "hello":
                return Hello(id, request);

            case "bind":
                broker.Bind(this, Queue(request));
                return V2Frames.Ok(id);

            case "unbind":
                broker.Unbind(this, Queue(request));
                return V2Frames.Ok(id);

            case "publish":
            {
                var queue = Queue(request);
                // As in 1.x, a body that is not an object becomes an empty one.
                var body = request.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Object
                    ? DelphiJson.Write(bodyElement)
                    : "{}";
                var rpc = request.TryGetProperty("rpc", out var rpcElement) && rpcElement.ValueKind == JsonValueKind.True;
                var ttl = request.TryGetProperty("ttlMs", out var ttlElement) && ttlElement.TryGetInt64(out var milliseconds) && milliseconds > 0
                    ? TimeSpan.FromMilliseconds(milliseconds)
                    : TimeSpan.Zero;

                var messageId = broker.Publish(queue, body, rpc, ttl, replyTo: rpc ? this : null);
                return V2Frames.Ok(id, writer => writer.WriteString("messageId", messageId));
            }

            case "ack":
                return broker.Confirm(this, Queue(request), MessageId(request))
                    ? V2Frames.Ok(id)
                    : throw NotFound();

            case "respond":
            {
                var queue = Queue(request);
                var messageId = MessageId(request);
                // An answer that is not an object cannot be stored. The message is confirmed
                // without it and whoever asked sees the RPC expire, as in 1.x.
                var settled = request.TryGetProperty("response", out var responseElement) && responseElement.ValueKind == JsonValueKind.Object
                    ? broker.Respond(this, queue, messageId, DelphiJson.Write(responseElement))
                    : broker.Confirm(this, queue, messageId);
                return settled ? V2Frames.Ok(id) : throw NotFound();
            }

            case "ping":
                return V2Frames.Ok(id);

            case "await":
            {
                var queue = Queue(request);
                if (!request.TryGetProperty("messageIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                    throw new V2Exception("invalid-request", "\"messageIds\" must be a list");

                var found = broker.Await(this, queue, ids.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToList());
                return V2Frames.Ok(id, writer => writer.WriteNumber("found", found));
            }

            default:
                throw new V2Exception("unknown-op", $"Unknown operation \"{op}\"");
        }
    }

    private byte[] Hello(long id, JsonElement request)
    {
        if (!request.TryGetProperty("protocol", out var protocol) || !protocol.TryGetInt32(out var version) || version != 2)
            throw new V2Exception("unsupported-protocol", "This server speaks protocol 2");

        if (request.TryGetProperty("client", out var client) && client.ValueKind == JsonValueKind.Object)
        {
            ClientName = Text(client, "name") ?? ClientName;
            Host = Text(client, "host") ?? Host;
            Wrapper = Text(client, "wrapper") ?? Wrapper;
            if (client.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var processId))
                ProcessId = processId;
        }

        _greeted = true;
        logger.LogInformation("v2 client connected: {Client}", Description);
        return V2Frames.Ok(id, writer =>
        {
            writer.WriteNumber("protocol", 2);
            writer.WriteString("server", ServerHost.Version);
            writer.WriteString("connection", Id);
        });
    }

    private static string Queue(JsonElement request) =>
        Text(request, "queue") is { Length: > 0 } queue
            ? queue
            : throw new V2Exception("queue-required", "Inform the Queue name");

    private static string MessageId(JsonElement request) =>
        Text(request, "messageId") ?? throw new V2Exception("invalid-request", "\"messageId\" is required");

    private static V2Exception NotFound() =>
        new("not-found", "This connection holds no such message");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed class V2Exception(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
