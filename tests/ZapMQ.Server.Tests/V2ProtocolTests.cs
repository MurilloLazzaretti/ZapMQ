using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The v2 protocol (docs/PROTOCOLO-V2.md), exercised over a real WebSocket.
/// </summary>
public class V2ProtocolTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static string Queue() => "q" + Guid.NewGuid().ToString("N");

    private Task<V2Client> Connect(string name = "test-client") => V2Client.ConnectAsync(server.Port, name);

    private HttpClient Admin => server.Admin;

    private static Task<JObject> Publish(V2Client client, string queue, object body, bool rpc = false, int ttlMs = 0) =>
        client.RequestAsync("publish", queue, frame =>
        {
            frame["body"] = JToken.FromObject(body);
            frame["rpc"] = rpc;
            frame["ttlMs"] = ttlMs;
        });

    private static Task<JObject> Ack(V2Client client, JObject delivery) =>
        client.RequestAsync("ack", (string)delivery["queue"]!, frame => frame["messageId"] = delivery["message"]!["id"]);

    private async Task<JArray> DeadLetters(string queue) =>
        JArray.Parse(await Admin.GetStringAsync($"admin/dead-letters/{queue}"));

    private async Task<JArray> DeadLettersEventually(string queue)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var letters = await DeadLetters(queue);
            if (letters.Count > 0)
                return letters;
            await Task.Delay(100);
        }
        return [];
    }

    [Fact]
    public async Task Plain_http_request_to_the_endpoint_is_told_to_upgrade()
    {
        using var response = await Admin.GetAsync("v2");

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task Hello_is_answered_with_the_protocol_and_the_connection_id()
    {
        await using var client = await V2Client.ConnectAsync(server.Port, hello: false);

        var reply = await client.RequestAsync(new JObject { ["op"] = "hello", ["protocol"] = 2 });

        Assert.True((bool)reply["ok"]!);
        Assert.Equal(2, (int)reply["protocol"]!);
        Assert.Equal("2.0.0", (string)reply["server"]!);
        Assert.StartsWith("c-", (string)reply["connection"]!);
    }

    [Fact]
    public async Task Anything_before_hello_is_refused_and_ends_the_connection()
    {
        await using var client = await V2Client.ConnectAsync(server.Port, hello: false);

        var reply = await client.RequestAsync("bind", Queue());

        Assert.Equal("hello-required", (string)reply["error"]!["code"]!);
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Another_protocol_version_is_refused()
    {
        await using var client = await V2Client.ConnectAsync(server.Port, hello: false);

        var reply = await client.RequestAsync(new JObject { ["op"] = "hello", ["protocol"] = 3 });

        Assert.Equal("unsupported-protocol", (string)reply["error"]!["code"]!);
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Ping_is_answered()
    {
        await using var client = await Connect();

        Assert.True((bool)(await client.RequestAsync(new JObject { ["op"] = "ping" }))["ok"]!);
    }

    [Fact]
    public async Task With_v2_turned_off_the_server_answers_the_old_protocol_only()
    {
        var app = ServerHost.Build([], builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ZapMQ:Port"] = "0", ["ZapMQ:V2:Enabled"] = "false" }));
        await app.StartAsync();
        try
        {
            var port = new Uri(app.Urls.First().Replace("[::]", "localhost").Replace("0.0.0.0", "localhost")).Port;
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/") };

            await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => V2Client.ConnectAsync(port));
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("v2")).StatusCode);
            Assert.Equal("{\"result\":[\"\"]}", await http.GetStringAsync("datasnap/rest/TZapMethods/GetMessage/" + Queue()));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Bad_requests_get_an_error_and_the_connection_goes_on()
    {
        await using var client = await Connect();

        await client.SendRawAsync("not json");
        Assert.Equal("invalid-request", (string)(await client.NextUnpairedAsync())["error"]!["code"]!);

        Assert.Equal("unknown-op", (string)(await client.RequestAsync(new JObject { ["op"] = "dance" }))["error"]!["code"]!);
        Assert.Equal("queue-required", (string)(await client.RequestAsync(new JObject { ["op"] = "publish" }))["error"]!["code"]!);
        Assert.Equal("queue-required", (string)(await client.RequestAsync("bind", ""))["error"]!["code"]!);
        Assert.Equal("invalid-request", (string)(await client.RequestAsync("ack", Queue()))["error"]!["code"]!);
        Assert.Equal("not-found", (string)(await client.RequestAsync("ack", Queue(), f => f["messageId"] = "{NOPE}"))["error"]!["code"]!);

        Assert.True((bool)(await client.RequestAsync("bind", Queue()))["ok"]!);
    }

    [Fact]
    public async Task Published_message_is_pushed_to_the_bound_client()
    {
        var queue = Queue();
        await using var consumer = await Connect();
        await using var publisher = await Connect();
        await consumer.RequestAsync("bind", queue);

        var published = await Publish(publisher, queue, new { name = "ação", path = "D:\\a/b", n = 1.5 });
        var delivery = await consumer.NextPushAsync();

        Assert.True((bool)published["ok"]!);
        Assert.Equal("deliver", (string)delivery["op"]!);
        Assert.Equal(queue, (string)delivery["queue"]!);
        Assert.Equal((string?)published["messageId"], (string?)delivery["message"]!["id"]);
        Assert.False((bool)delivery["message"]!["rpc"]!);
        Assert.True(JToken.DeepEquals(JObject.FromObject(new { name = "ação", path = "D:\\a/b", n = 1.5 }), delivery["message"]!["body"]));
    }

    [Fact]
    public async Task Body_that_is_not_an_object_becomes_an_empty_object()
    {
        var queue = Queue();
        await using var client = await Connect();
        await client.RequestAsync("publish", queue, frame => frame["body"] = "text");

        await using var consumer = await Connect();
        await consumer.RequestAsync("bind", queue);

        Assert.Equal("{}", (await consumer.NextPushAsync())["message"]!["body"]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public async Task Next_message_is_pushed_only_after_the_current_one_is_confirmed()
    {
        var queue = Queue();
        await using var consumer = await Connect();
        await using var publisher = await Connect();
        await consumer.RequestAsync("bind", queue);
        await Publish(publisher, queue, new { i = 1 });
        await Publish(publisher, queue, new { i = 2 });

        var first = await consumer.NextPushAsync();
        Assert.True(await consumer.HasNoPushAsync());

        Assert.True((bool)(await Ack(consumer, first))["ok"]!);
        var second = await consumer.NextPushAsync();

        Assert.Equal(1, (int)first["message"]!["body"]!["i"]!);
        Assert.Equal(2, (int)second["message"]!["body"]!["i"]!);
        Assert.Equal("not-found", (string)(await Ack(consumer, first))["error"]!["code"]!);
    }

    [Fact]
    public async Task Rpc_answer_is_pushed_to_the_client_that_asked()
    {
        var queue = Queue();
        await using var worker = await Connect("worker");
        await using var asker = await Connect("asker");
        await worker.RequestAsync("bind", queue);

        var asked = await Publish(asker, queue, new { n = 21 }, rpc: true);
        var delivery = await worker.NextPushAsync();
        Assert.True((bool)delivery["message"]!["rpc"]!);

        var responded = await worker.RequestAsync("respond", queue, frame =>
        {
            frame["messageId"] = delivery["message"]!["id"];
            frame["response"] = new JObject { ["doubled"] = 42 };
        });
        var answer = await asker.NextPushAsync();

        Assert.True((bool)responded["ok"]!);
        Assert.Equal("response", (string)answer["op"]!);
        Assert.Equal((string?)asked["messageId"], (string?)answer["messageId"]);
        Assert.Equal(42, (int)answer["message"]!["response"]!["doubled"]!);
        Assert.Equal(21, (int)answer["message"]!["body"]!["n"]!);
        Assert.True(await asker.HasNoPushAsync(200));
    }

    [Fact]
    public async Task Message_of_a_client_that_dies_becomes_a_dead_letter_and_is_not_redelivered()
    {
        var queue = Queue();
        var dying = await Connect("dying-worker");
        await using var other = await Connect("other-worker");
        await using var publisher = await Connect();
        await dying.RequestAsync("bind", queue);
        var published = await Publish(publisher, queue, new { i = 1 });
        await dying.NextPushAsync();
        await other.RequestAsync("bind", queue);

        dying.Drop();

        var letter = (JObject)(await DeadLettersEventually(queue)).Single();
        Assert.Equal((string?)published["messageId"], (string?)letter["id"]);
        Assert.Equal("unconfirmed", (string)letter["reason"]!);
        Assert.Contains("dying-worker (pid 4242 @ TESTHOST)", (string)letter["consumer"]!);
        Assert.Equal(1, (int)letter["body"]!["i"]!);
        Assert.True(await other.HasNoPushAsync());
    }

    [Fact]
    public async Task Dead_letter_can_be_requeued_by_hand_and_is_then_delivered_as_a_copy()
    {
        var queue = Queue();
        var dying = await Connect();
        await using var publisher = await Connect();
        await dying.RequestAsync("bind", queue);
        var published = await Publish(publisher, queue, new { i = 1 });
        await dying.NextPushAsync();
        dying.Drop();
        var deadId = (string)(await DeadLettersEventually(queue)).Single()["id"]!;

        await using var consumer = await Connect();
        await consumer.RequestAsync("bind", queue);
        using var requeued = await Admin.PostAsync($"admin/dead-letters/{queue}/{Uri.EscapeDataString(deadId)}/requeue", null);
        var delivery = await consumer.NextPushAsync();

        Assert.Equal(HttpStatusCode.OK, requeued.StatusCode);
        Assert.Equal(1, (int)delivery["message"]!["body"]!["i"]!);
        Assert.Equal((string?)published["messageId"], (string?)delivery["message"]!["requeuedFrom"]);
        Assert.NotEqual((string?)published["messageId"], (string?)delivery["message"]!["id"]);
        Assert.Empty(await DeadLetters(queue));

        using var again = await Admin.PostAsync($"admin/dead-letters/{queue}/{Uri.EscapeDataString(deadId)}/requeue", null);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Queue_set_to_redeliver_hands_the_message_to_another_client()
    {
        var queue = Queue();
        using var configured = await Admin.PutAsJsonAsync($"admin/queues/{queue}", new { redeliverUnconfirmed = true });
        var dying = await Connect();
        await using var other = await Connect();
        await using var publisher = await Connect();
        await dying.RequestAsync("bind", queue);
        var published = await Publish(publisher, queue, new { i = 1 });
        await dying.NextPushAsync();
        await other.RequestAsync("bind", queue);

        dying.Drop();
        var delivery = await other.NextPushAsync();

        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.Equal((string?)published["messageId"], (string?)delivery["message"]!["id"]);
        Assert.Empty(await DeadLetters(queue));
    }

    [Fact]
    public async Task Queue_settings_can_be_read_changed_and_reset()
    {
        var queue = Queue();

        var initial = JObject.Parse(await Admin.GetStringAsync($"admin/queues/{queue}"));
        using var changed = await Admin.PutAsJsonAsync($"admin/queues/{queue}", new
        {
            retentionSeconds = 3600,
            redeliverUnconfirmed = true,
            deadLetters = new { maxMessagesPerQueue = 5, maxAgeHours = 2 }
        });
        var read = JObject.Parse(await Admin.GetStringAsync($"admin/queues/{queue}"));
        using var reset = await Admin.DeleteAsync($"admin/queues/{queue}");
        var after = JObject.Parse(await Admin.GetStringAsync($"admin/queues/{queue}"));

        Assert.False((bool)initial["redeliverUnconfirmed"]!);
        Assert.Equal(JTokenType.Null, initial["retentionSeconds"]!.Type);
        Assert.Equal((3600, true, 5, 2), ((int)read["retentionSeconds"]!, (bool)read["redeliverUnconfirmed"]!,
            (int)read["deadLetters"]!["maxMessagesPerQueue"]!, (int)read["deadLetters"]!["maxAgeHours"]!));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.False((bool)after["redeliverUnconfirmed"]!);
    }

    [Fact]
    public async Task Expired_message_shows_up_among_the_dead_letters_and_can_be_discarded()
    {
        var queue = Queue();
        await using var publisher = await Connect();
        var published = await Publish(publisher, queue, new { i = 1 }, ttlMs: 100);

        var letter = (JObject)(await DeadLettersEventually(queue)).Single();
        var summary = JArray.Parse(await Admin.GetStringAsync("admin/dead-letters"));
        var single = JObject.Parse(await Admin.GetStringAsync($"admin/dead-letters/{queue}/{Uri.EscapeDataString((string)letter["id"]!)}"));
        using var discarded = await Admin.DeleteAsync($"admin/dead-letters/{queue}/{Uri.EscapeDataString((string)letter["id"]!)}");

        Assert.Equal((string?)published["messageId"], (string?)letter["id"]);
        Assert.Equal("expired", (string)letter["reason"]!);
        Assert.Equal(1, (int)summary.Single(s => (string)s["queue"]! == queue)["expired"]!);
        Assert.Equal((string?)letter["id"], (string?)single["id"]);
        Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);
        Assert.Empty(await DeadLetters(queue));
    }

    [Fact]
    public async Task Message_published_by_1x_is_pushed_to_a_v2_client()
    {
        var queue = Queue();
        await using var consumer = await Connect();
        await consumer.RequestAsync("bind", queue);

        await server.Http.GetAsync($"UpdateMessage/{queue}/{Uri.EscapeDataString("{\"Id\":\"\",\"Body\":{\"from\":\"v1\"},\"RPC\":false,\"TTL\":0}")}");

        Assert.Equal("v1", (string)(await consumer.NextPushAsync())["message"]!["body"]!["from"]!);
    }

    [Fact]
    public async Task Message_published_by_v2_is_handed_to_a_1x_client_that_asks()
    {
        var queue = Queue();
        await using var publisher = await Connect();
        var published = await Publish(publisher, queue, new { from = "v2", path = "a/b" });

        var envelope = JObject.Parse(await server.Http.GetStringAsync($"GetMessage/{queue}"));
        var message = JObject.Parse((string)envelope["result"]![0]!);

        Assert.Equal((string?)published["messageId"], (string?)message["Id"]);
        Assert.Equal("v2", (string)message["Body"]!["from"]!);
        Assert.Equal("a/b", (string)message["Body"]!["path"]!);
    }

    [Fact]
    public async Task Rpc_asked_by_v2_and_answered_by_1x_reaches_the_asker()
    {
        var queue = Queue();
        await using var asker = await Connect();
        var asked = await Publish(asker, queue, new { n = 1 }, rpc: true);
        var id = Uri.EscapeDataString((string)asked["messageId"]!);

        await server.Http.GetAsync($"GetMessage/{queue}");
        await server.Http.GetAsync($"UpdateRPCResponse/{queue}/{id}/{Uri.EscapeDataString("{\"answer\":\"from v1\"}")}");

        Assert.Equal("from v1", (string)(await asker.NextPushAsync())["message"]!["response"]!["answer"]!);
    }

    [Fact]
    public async Task Rpc_asked_by_1x_and_answered_by_v2_is_collected_by_1x()
    {
        var queue = Queue();
        await using var worker = await Connect();
        await worker.RequestAsync("bind", queue);

        var published = JObject.Parse(await server.Http.GetStringAsync(
            $"UpdateMessage/{queue}/{Uri.EscapeDataString("{\"Id\":\"\",\"Body\":{\"n\":1},\"RPC\":true,\"TTL\":0}")}"));
        var id = (string)published["result"]![0]!;
        var delivery = await worker.NextPushAsync();
        await worker.RequestAsync("respond", queue, frame =>
        {
            frame["messageId"] = delivery["message"]!["id"];
            frame["response"] = new JObject { ["answer"] = "from v2" };
        });

        var envelope = JObject.Parse(await server.Http.GetStringAsync($"GetRPCResponse/{queue}/{Uri.EscapeDataString(id)}"));
        var message = JObject.Parse((string)envelope["result"]![0]!);

        Assert.Equal("from v2", (string)message["Response"]!["answer"]!);
    }

    [Fact]
    public async Task Answer_that_arrived_while_the_asker_was_away_is_handed_over_after_await()
    {
        var queue = Queue();
        await using var worker = await Connect();
        var asker = await Connect();
        var asked = await Publish(asker, queue, new { n = 1 }, rpc: true);
        asker.Drop();
        await Task.Delay(300);

        await worker.RequestAsync("bind", queue);
        var delivery = await worker.NextPushAsync();
        await worker.RequestAsync("respond", queue, frame =>
        {
            frame["messageId"] = delivery["message"]!["id"];
            frame["response"] = new JObject { ["late"] = true };
        });

        await using var back = await Connect();
        var awaited = await back.RequestAsync("await", queue, frame => frame["messageIds"] = new JArray((string)asked["messageId"]!));

        Assert.Equal(1, (int)awaited["found"]!);
        Assert.True((bool)(await back.NextPushAsync())["message"]!["response"]!["late"]!);
    }

    [Fact]
    public async Task Metrics_list_the_connected_clients_and_their_queues()
    {
        var queue = Queue();
        await using var client = await Connect("metrics-probe");
        await client.RequestAsync("bind", queue);

        var metrics = JObject.Parse(await Admin.GetStringAsync("metrics"));
        var connection = metrics["connections"]!.Single(c => (string)c["client"]! == "metrics-probe");

        Assert.Equal((4242, "TESTHOST", "test/1.0"), ((int)connection["pid"]!, (string)connection["host"]!, (string)connection["wrapper"]!));
        Assert.Equal([queue], connection["queues"]!.Select(q => (string)q!));
        Assert.Equal(1, (int)metrics["queues"]!.Single(q => (string)q["name"]! == queue)["consumers"]!);
    }

    [Fact]
    public async Task Frame_above_the_limit_ends_the_connection()
    {
        await using var client = await Connect();

        await client.SendRawAsync("{\"op\":\"publish\",\"queue\":\"x\",\"body\":{\"t\":\"" + new string('x', 5 * 1024 * 1024) + "\"}}");

        Assert.Equal("too-large", (string)(await client.NextUnpairedAsync())["error"]!["code"]!);
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pushed_and_asking_clients_on_the_same_queue_receive_each_message_once()
    {
        var queue = Queue();
        const int total = 300;
        await using var publisher = await Connect();
        var pushed = new List<V2Client>();
        for (var i = 0; i < 3; i++)
        {
            var client = await Connect();
            await client.RequestAsync("bind", queue);
            pushed.Add(client);
        }

        var received = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var stop = new CancellationTokenSource();
        var pushedWork = pushed.Select(client => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                JObject delivery;
                try { delivery = await client.NextPushAsync(); }
                catch (OperationCanceledException) { break; }
                received.Add((int)delivery["message"]!["body"]!["i"]!);
                await Ack(client, delivery);
            }
        })).ToArray();
        var askingWork = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var envelope = JObject.Parse(await server.Http.GetStringAsync($"GetMessage/{queue}"));
                if ((string)envelope["result"]![0]! is { Length: > 0 } text)
                    received.Add((int)JObject.Parse(text)["Body"]!["i"]!);
            }
        })).ToArray();

        for (var i = 0; i < total; i++)
            await Publish(publisher, queue, new { i });

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (received.Count < total && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await Task.Delay(300);
        stop.Cancel();
        await Task.WhenAll(askingWork);
        foreach (var client in pushed)
            await client.DisposeAsync();

        Assert.Equal(Enumerable.Range(0, total), received.OrderBy(i => i));
    }
}
