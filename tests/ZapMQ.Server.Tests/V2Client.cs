using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Newtonsoft.Json.Linq;

namespace ZapMQ.Server.Tests;

/// <summary>
/// A bare v2 client for the tests: sends frames, pairs replies with requests and keeps what the
/// server pushes.
/// </summary>
public sealed class V2Client : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _waiting = new();
    private readonly Channel<JObject> _pushed = Channel.CreateUnbounded<JObject>();
    private readonly Channel<JObject> _unpaired = Channel.CreateUnbounded<JObject>();
    private Task _receiving = Task.CompletedTask;
    private long _nextId;

    public Task Closed => _receiving;

    public static async Task<V2Client> ConnectAsync(int port, string name = "test-client", bool hello = true)
    {
        var client = new V2Client();
        await client._socket.ConnectAsync(new Uri($"ws://localhost:{port}/v2"), CancellationToken.None);
        client._receiving = Task.Run(client.ReceiveAsync);
        if (hello)
        {
            var reply = await client.RequestAsync(new JObject
            {
                ["op"] = "hello",
                ["protocol"] = 2,
                ["client"] = new JObject { ["name"] = name, ["pid"] = 4242, ["host"] = "TESTHOST", ["wrapper"] = "test/1.0" }
            });
            if (!(bool)reply["ok"]!)
                throw new InvalidOperationException(reply.ToString());
        }
        return client;
    }

    /// <summary>
    /// Sends a request and returns its reply.
    /// </summary>
    public async Task<JObject> RequestAsync(JObject frame)
    {
        var id = Interlocked.Increment(ref _nextId);
        frame["id"] = id;
        var reply = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = reply;
        await SendRawAsync(frame.ToString(Newtonsoft.Json.Formatting.None));
        return await reply.Task.WaitAsync(Patience);
    }

    public Task<JObject> RequestAsync(string op, string queue, Action<JObject>? more = null)
    {
        var frame = new JObject { ["op"] = op, ["queue"] = queue };
        more?.Invoke(frame);
        return RequestAsync(frame);
    }

    public Task SendRawAsync(string text) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>
    /// The next frame the server sent on its own (deliver, response, bye).
    /// </summary>
    public async Task<JObject> NextPushAsync()
    {
        using var timeout = new CancellationTokenSource(Patience);
        return await _pushed.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>
    /// The next reply that answers no request of ours, as happens for a frame without an id.
    /// </summary>
    public async Task<JObject> NextUnpairedAsync()
    {
        using var timeout = new CancellationTokenSource(Patience);
        return await _unpaired.Reader.ReadAsync(timeout.Token);
    }

    public async Task<bool> HasNoPushAsync(int milliseconds = 400)
    {
        await Task.Delay(milliseconds);
        return !_pushed.Reader.TryPeek(out _);
    }

    /// <summary>
    /// Cuts the connection without a closing handshake, like a process that died.
    /// </summary>
    public void Drop() => _socket.Abort();

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            catch (Exception)
            {
                // The server may have closed first.
            }
        }
        _socket.Dispose();
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        using var frame = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                frame.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (received.MessageType == WebSocketMessageType.Close)
                        return;
                    frame.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                var message = JObject.Parse(Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length));
                if (message["op"] is not null)
                    _pushed.Writer.TryWrite(message);
                else if (message["re"] is { } re && _waiting.TryRemove((long)re, out var reply))
                    reply.TrySetResult(message);
                else
                    _unpaired.Writer.TryWrite(message);
            }
        }
        catch (Exception)
        {
            // Dropped on purpose or closed by the server.
        }
    }
}
