using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using ZapMQ;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The deployed 1.x .NET wrapper, unmodified, talking to the new server.
/// </summary>
public class WrapperCompatibilityTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string Queue() => "q" + Guid.NewGuid().ToString("N");

    private ZapMQWrapper Wrapper() => new("localhost", server.Port);

    [Fact]
    public async Task Message_sent_by_one_wrapper_reaches_the_handler_of_another()
    {
        var queue = Queue();
        var received = new TaskCompletionSource<ZapJSONMessage>();
        var consumer = Wrapper();
        var publisher = Wrapper();
        try
        {
            consumer.Bind(queue, (ZapJSONMessage message, out bool processing) =>
            {
                processing = false;
                received.TrySetResult(message);
                return null!;
            });

            Assert.True(publisher.SendMessage(queue, new { name = "ação", value = 12.5, path = "D:\\a/b" }));

            var message = await received.Task.WaitAsync(Timeout);
            var body = JObject.Parse(message.Body.ToString()!);
            Assert.Equal("ação", (string)body["name"]!);
            Assert.Equal(12.5, (double)body["value"]!);
            Assert.Equal("D:\\a/b", (string)body["path"]!);
            Assert.False(message.RPC);
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task Rpc_answer_returns_to_the_sender()
    {
        var queue = Queue();
        var answered = new TaskCompletionSource<ZapJSONMessage>();
        var consumer = Wrapper();
        var publisher = Wrapper();
        try
        {
            consumer.Bind(queue, (ZapJSONMessage message, out bool processing) =>
            {
                processing = false;
                var question = JObject.Parse(message.Body.ToString()!);
                return new { doubled = (int)question["n"]! * 2 };
            });

            Assert.True(publisher.SendRPCMessage(queue, new { n = 21 }, message => answered.TrySetResult(message), 5000));

            var response = JObject.Parse((await answered.Task.WaitAsync(Timeout)).Response.ToString()!);
            Assert.Equal(42, (int)response["doubled"]!);
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task Unanswered_rpc_expires_on_the_sender()
    {
        var expired = new TaskCompletionSource<ZapJSONMessage>();
        var publisher = Wrapper();
        try
        {
            publisher.OnRPCExpired = message => expired.TrySetResult(message);

            Assert.True(publisher.SendRPCMessage(Queue(), new { n = 1 }, _ => { }, 500));

            Assert.NotNull(await expired.Task.WaitAsync(Timeout));
        }
        finally
        {
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task Two_consumers_on_the_same_queue_process_each_message_once()
    {
        var queue = Queue();
        const int total = 40;
        var received = new ConcurrentBag<int>();
        var consumers = new[] { Wrapper(), Wrapper(), Wrapper() };
        var publisher = Wrapper();
        try
        {
            foreach (var consumer in consumers)
            {
                // The wrapper's own duplicate guard is switched off: the server alone has to
                // guarantee that a message goes to a single consumer.
                consumer.DeduplicateMessages = false;
                consumer.Bind(queue, (ZapJSONMessage message, out bool processing) =>
                {
                    processing = false;
                    received.Add((int)JObject.Parse(message.Body.ToString()!)["i"]!);
                    return null!;
                });
            }

            for (var i = 0; i < total; i++)
                Assert.True(publisher.SendMessage(queue, new { i }));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (received.Count < total && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            await Task.Delay(1000);

            Assert.Equal(Enumerable.Range(0, total), received.OrderBy(i => i));
        }
        finally
        {
            foreach (var consumer in consumers)
                consumer.StopThreads();
            publisher.StopThreads();
        }
    }
}
