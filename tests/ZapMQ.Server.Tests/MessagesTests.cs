using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ZapMQ.Server.Tests;

/// <summary>
/// Looking at the messages that go through a queue, and publishing one by hand.
/// </summary>
public class MessagesTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static string Queue() => "q" + Guid.NewGuid().ToString("N")[..10];

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    private static Task<JObject> Publish(V2Client client, string queue, object body, bool rpc = false) =>
        client.RequestAsync("publish", queue, frame =>
        {
            frame["body"] = JToken.FromObject(body);
            frame["rpc"] = rpc;
        });

    /// <summary>
    /// Reads the steps a watch sends until the given one arrives, or gives up.
    /// </summary>
    private sealed class Watch(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public List<JToken> Steps { get; } = [];

        public async Task<JToken> UntilAsync(Func<JToken, bool> wanted)
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (Steps.FirstOrDefault(wanted) is { } found)
                    return found;
                var line = await reader.ReadLineAsync(patience.Token) ?? throw new EndOfStreamException();
                if (line.StartsWith("data: "))
                    Steps.AddRange(JArray.Parse(line[6..]));
            }
        }

        public void Dispose() => response.Dispose();
    }

    private async Task<Watch> WatchAsync(string queue)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"api/queues/{queue}/watch");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await server.Admin.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        // The first line says the watch is on; before it, a message could pass unseen.
        while (await reader.ReadLineAsync() is { } line && !line.StartsWith(": watching"))
        {
        }
        return new Watch(response, reader);
    }

    [Fact]
    public async Task Whoever_watches_a_queue_sees_each_step_of_each_message_with_its_body()
    {
        var queue = Queue();
        await using var consumer = await V2Client.ConnectAsync(server.Port, "billing");
        await using var publisher = await V2Client.ConnectAsync(server.Port, "orders");
        await consumer.RequestAsync("bind", queue);
        using var watch = await WatchAsync(queue);

        var id = (string)(await Publish(publisher, queue, new { pedido = 42, cliente = "João" }))["messageId"]!;
        var delivery = await consumer.NextPushAsync();
        await consumer.RequestAsync("ack", queue, frame => frame["messageId"] = delivery["message"]!["id"]);

        var confirmed = await watch.UntilAsync(step => (string)step["kind"]! == "confirmed");
        Assert.Equal(["published", "delivered", "confirmed"], watch.Steps.Select(step => (string)step["kind"]!));
        Assert.All(watch.Steps, step => Assert.Equal(id, (string)step["id"]!));

        var published = watch.Steps[0];
        Assert.Equal(42, (int)JObject.Parse((string)published["body"]!)["pedido"]!);
        Assert.Equal("orders (pid 4242)", (string)published["who"]!);
        Assert.False((bool)published["rpc"]!);
        Assert.Equal("billing (pid 4242)", (string)watch.Steps[1]["who"]!);
        // Only the publication carries the body.
        Assert.Null(confirmed["body"]);

        // Watching took nothing: the message was delivered to its consumer, once.
        Assert.True(await consumer.HasNoPushAsync());
    }

    [Fact]
    public async Task A_queue_nobody_watches_keeps_nothing_and_one_set_up_for_it_keeps_its_last_messages()
    {
        var (plain, kept) = (Queue(), Queue());
        (await server.Admin.PutAsync($"api/queues/{kept}/settings", JsonContent.Create(new { keepRecent = 2 }))).EnsureSuccessStatusCode();
        await using var publisher = await V2Client.ConnectAsync(server.Port, "orders");

        await Publish(publisher, plain, new { n = 0 });
        for (var n = 1; n <= 5; n++)
            await Publish(publisher, kept, new { n });

        Assert.Empty(JArray.Parse(await server.Admin.GetStringAsync($"api/queues/{plain}/recent")));
        var recent = JArray.Parse(await server.Admin.GetStringAsync($"api/queues/{kept}/recent"));
        Assert.Equal([1, 2, 3, 4, 5], recent.Select(step => (int)JObject.Parse((string)step["body"]!)["n"]!));
        Assert.Equal(2, (int)JObject.Parse(await server.Admin.GetStringAsync($"api/queues/{kept}"))["settings"]!["keepRecent"]!);

        // The definition is written down, and going back to the defaults stops the keeping.
        Assert.Contains("\"KeepRecent\": 2", File.ReadAllText(server.DefinitionsFile));
        (await server.Admin.DeleteAsync($"api/queues/{kept}/settings")).EnsureSuccessStatusCode();
        Assert.Empty(JArray.Parse(await server.Admin.GetStringAsync($"api/queues/{kept}/recent")));
    }

    [Fact]
    public async Task A_message_published_by_hand_is_delivered_like_any_other_and_says_who_sent_it()
    {
        var queue = Queue();
        await using var consumer = await V2Client.ConnectAsync(server.Port, "billing");
        await consumer.RequestAsync("bind", queue);

        var response = await server.Admin.PostAsync($"api/queues/{queue}/publish", Json("""{ "body": { "pedido": 7, "itens": ["a", "ç"] }, "ttlMs": 5000 }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = JObject.Parse(await response.Content.ReadAsStringAsync());
        var delivery = await consumer.NextPushAsync();
        Assert.Equal((string)answer["id"]!, (string)delivery["message"]!["id"]!);
        Assert.Equal("ç", (string)delivery["message"]!["body"]!["itens"]![1]!);

        var detail = JObject.Parse(await server.Admin.GetStringAsync($"api/queues/{queue}"));
        Assert.Equal(("panel", "panel:admin"), ((string)detail["publishers"]![0]!["protocol"]!, (string)detail["publishers"]![0]!["application"]!));

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Admin.PostAsync($"api/queues/{queue}/publish", Json("""{ "ttlMs": 5 }"""))).StatusCode);
    }

    [Fact]
    public async Task A_question_asked_by_hand_brings_its_answer_or_says_nobody_answered()
    {
        var queue = Queue();
        await using var responder = await V2Client.ConnectAsync(server.Port, "calculator");
        await responder.RequestAsync("bind", queue);
        _ = Task.Run(async () =>
        {
            var delivery = await responder.NextPushAsync();
            await responder.RequestAsync("respond", queue, frame =>
            {
                frame["messageId"] = delivery["message"]!["id"];
                frame["response"] = new JObject { ["total"] = (int)delivery["message"]!["body"]!["a"]! + 1 };
            });
        });

        var answered = JObject.Parse(await (await server.Admin.PostAsync($"api/queues/{queue}/publish", Json("""{ "body": { "a": 41 }, "rpc": true }"""))).Content.ReadAsStringAsync());
        Assert.True((bool)answered["answered"]!);
        Assert.Equal(42, (int)answered["response"]!["total"]!);

        var silent = JObject.Parse(await (await server.Admin.PostAsync($"api/queues/{Queue()}/publish", Json("""{ "body": 1, "rpc": true, "ttlMs": 300 }"""))).Content.ReadAsStringAsync());
        Assert.False((bool)silent["answered"]!);
    }

    [Fact]
    public async Task Bodies_are_saved_under_a_name_for_each_queue()
    {
        var queue = Queue();

        (await server.Admin.PutAsync($"api/queues/{queue}/models/{Uri.EscapeDataString("Pedido de teste")}", Json("""{ "body": { "pedido": 1 }, "ttlMs": 9000, "rpc": true }"""))).EnsureSuccessStatusCode();
        (await server.Admin.PutAsync($"api/queues/{queue}/models/Outro", Json("""{ "body": [1, 2] }"""))).EnsureSuccessStatusCode();
        (await server.Admin.PutAsync($"api/queues/{queue}/models/{Uri.EscapeDataString("pedido de TESTE")}", Json("""{ "body": { "pedido": 2 } }"""))).EnsureSuccessStatusCode();

        var models = JArray.Parse(await server.Admin.GetStringAsync($"api/queues/{queue}/models"));
        Assert.Equal(["Outro", "pedido de TESTE"], models.Select(model => (string)model["name"]!));
        Assert.Equal(2, (int)models[1]["body"]!["pedido"]!);
        Assert.Empty(JArray.Parse(await server.Admin.GetStringAsync($"api/queues/{Queue()}/models")));

        Assert.Equal(HttpStatusCode.OK, (await server.Admin.DeleteAsync($"api/queues/{queue}/models/Outro")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Admin.DeleteAsync($"api/queues/{queue}/models/Outro")).StatusCode);
        Assert.Single(JArray.Parse(await server.Admin.GetStringAsync($"api/queues/{queue}/models")));
    }
}
