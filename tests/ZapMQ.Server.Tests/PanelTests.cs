using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The panel port: its login and the API the interface is built on.
/// </summary>
public class PanelTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static string Queue() => "q" + Guid.NewGuid().ToString("N");

    private HttpClient Api => server.Admin;

    private HttpClient Anonymous() => new() { BaseAddress = new Uri($"http://localhost:{server.PanelPort}/") };

    private async Task<JToken> Get(string path) => JToken.Parse(await Api.GetStringAsync(path));

    private Task Publish(string queue, string body = "{\"n\":1}") =>
        server.Http.GetStringAsync($"UpdateMessage/{queue}/{Uri.EscapeDataString("{\"Id\":\"\",\"Body\":" + body + ",\"RPC\":false,\"TTL\":0}")}");

    [Fact]
    public async Task Nothing_of_the_panel_answers_on_the_messaging_port()
    {
        using var messaging = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.Port}/") };

        Assert.Equal(HttpStatusCode.NotFound, (await messaging.GetAsync("api/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await messaging.GetAsync("admin/dead-letters")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await messaging.PostAsJsonAsync("api/login", new { user = "admin", password = "admin" })).StatusCode);
        // What other services read stays where it was.
        Assert.Equal(HttpStatusCode.OK, (await messaging.GetAsync("health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await messaging.GetAsync("metrics")).StatusCode);
    }

    [Fact]
    public async Task Without_a_session_only_the_way_in_answers()
    {
        using var anonymous = Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/queues")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("admin/dead-letters")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("api/queues/x/purge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("health")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("api/login", new { user = "admin", password = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("api/login", new { user = "", password = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/overview")).StatusCode);
    }

    [Fact]
    public async Task Logging_in_opens_a_session_and_logging_out_ends_it()
    {
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://localhost:{server.PanelPort}/") };

        var login = await client.PostAsJsonAsync("api/login", new { user = "admin", password = "admin" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var session = JObject.Parse(await client.GetStringAsync("api/session"));
        Assert.Equal("admin", (string?)session["user"]);
        // The initial password is still in use, and the panel says so.
        Assert.True((bool)session["defaultPassword"]!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/overview")).StatusCode);

        await client.PostAsync("api/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/overview")).StatusCode);
    }

    [Fact]
    public async Task A_session_cookie_that_was_tampered_with_or_expired_is_refused()
    {
        var auth = server.Services.GetRequiredService<PanelAuth>();
        var good = auth.Issue("admin");

        Assert.Equal("admin", auth.Validate(good));
        Assert.Null(auth.Validate(good[..^1] + (good[^1] == 'A' ? 'B' : 'A')));
        Assert.Null(auth.Validate(good.Replace(good.Split('.')[1], "9999999999")));
        Assert.Null(auth.Validate("nonsense"));
        Assert.Null(auth.Validate(null));
    }

    [Fact]
    public async Task The_panel_answers_under_its_base_path_too()
    {
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://localhost:{server.PanelPort}/zapmq/") };

        var login = await client.PostAsJsonAsync("api/login", new { user = "admin", password = "admin" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        // The cookie is for the path the panel was reached through.
        Assert.Contains("path=/zapmq", Assert.Single(login.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health")).StatusCode);
    }

    [Fact]
    public async Task The_overview_and_the_series_follow_what_goes_through_the_broker()
    {
        var sampler = server.Services.GetRequiredService<MetricsSampler>();
        var queue = Queue();
        sampler.Sample();
        var before = await Get("api/overview");
        await Publish(queue);
        await Publish(queue);
        await Task.Delay(1100);
        sampler.Sample();

        var overview = await Get("api/overview");
        Assert.Equal(ServerHost.Version, (string?)overview["version"]);
        Assert.Equal((long)before["totals"]!["published"]! + 2, (long)overview["totals"]!["published"]!);
        Assert.True((int)overview["pending"]! >= 2);
        Assert.True((int)overview["queues"]! >= 1);
        Assert.True((int)overview["withoutConsumer"]! >= 1);
        Assert.True((double)overview["rates"]!["published"]! > 0);

        var series = await Get("api/series");
        Assert.Equal("hour", (string?)series["range"]);
        var last = series["points"]!.Last();
        Assert.True((double)last["publishedPerSecond"]! > 0);
        Assert.True((int)last["pending"]! >= 2);
        Assert.Equal("day", (string?)(await Get("api/series?range=day"))["range"]);
    }

    [Fact]
    public async Task A_queue_shows_its_messages_and_who_uses_it()
    {
        var queue = Queue();
        await using var consumer = await V2Client.ConnectAsync(server.Port, "billing-service");
        await using var publisher = await V2Client.ConnectAsync(server.Port, "shop-api");
        await consumer.RequestAsync("bind", queue);
        await publisher.RequestAsync("publish", queue, frame => frame["body"] = new JObject { ["n"] = 1 });
        await consumer.NextPushAsync();
        await publisher.RequestAsync("publish", queue, frame => frame["body"] = new JObject { ["n"] = 2, ["texto"] = "ação" });
        await Publish(queue, "{\"n\":3}");

        var listed = (await Get("api/queues")).Single(item => (string?)item["name"] == queue);
        Assert.True((bool)listed["exists"]!);
        Assert.False((bool)listed["defined"]!);
        Assert.Equal(2, (int)listed["pending"]!);
        Assert.Equal(1, (int)listed["processing"]!);
        Assert.Equal(1, (int)listed["consumers"]!);

        var detail = await Get($"api/queues/{queue}");
        Assert.Equal(JTokenType.Null, detail["settings"]!.Type);
        Assert.Equal(180, (int)detail["defaults"]!["retentionSeconds"]!);
        var shop = detail["publishers"]!.Single(party => (string?)party["protocol"] == "v2");
        Assert.Equal("shop-api", (string?)shop["application"]);
        Assert.Equal(2, (int)shop["count"]!);
        Assert.Single(detail["publishers"]!, party => (string?)party["protocol"] == "v1");
        Assert.Equal("billing-service", (string?)Assert.Single(detail["consumers"]!)["application"]);

        var messages = await Get($"api/queues/{queue}/messages");
        Assert.Equal(2, messages.Count());
        Assert.Equal("ação", (string?)messages[0]!["body"]!["texto"]);
        Assert.Equal(3, (int)messages[1]!["body"]!["n"]!);
        // Looking does not consume.
        Assert.Equal(2, (int)(await Get($"api/queues/{queue}"))["snapshot"]!["pending"]!);

        var connections = await Get("api/connections");
        Assert.Contains(connections["v2"]!, item => (string?)item["application"] == "billing-service" && item["queues"]!.Any(bound => (string?)bound == queue) && (bool)item["busy"]!);
        Assert.Contains(connections["v1"]!, item => item["publishes"]!.Any(name => (string?)name == queue));
    }

    [Fact]
    public async Task The_definition_of_a_queue_can_be_edited_and_is_written_to_the_file()
    {
        var queue = Queue();

        var saved = await Api.PutAsJsonAsync($"api/queues/{queue}/settings", new
        {
            retentionSeconds = 3600,
            redeliverUnconfirmed = true,
            deadLetters = new { maxMessagesPerQueue = 50 }
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // The queue has no message yet and is listed all the same.
        var listed = (await Get("api/queues")).Single(item => (string?)item["name"] == queue);
        Assert.False((bool)listed["exists"]!);
        Assert.True((bool)listed["defined"]!);
        var detail = await Get($"api/queues/{queue}");
        Assert.Equal(3600, (int)detail["settings"]!["retentionSeconds"]!);
        Assert.True((bool)detail["settings"]!["redeliverUnconfirmed"]!);
        Assert.Equal(50, (int)detail["settings"]!["deadLetters"]!["maxMessagesPerQueue"]!);

        var file = JObject.Parse(await File.ReadAllTextAsync(server.DefinitionsFile));
        Assert.Equal(3600, (int)file[queue]!["RetentionSeconds"]!);
        Assert.True((bool)file[queue]!["RedeliverUnconfirmed"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"api/queues/{queue}/settings")).StatusCode);
        Assert.DoesNotContain(await Get("api/queues"), item => (string?)item["name"] == queue);
        Assert.Null(JObject.Parse(await File.ReadAllTextAsync(server.DefinitionsFile))[queue]);
    }

    [Fact]
    public async Task What_the_file_holds_is_in_force_when_the_service_starts()
    {
        var file = Path.Combine(Path.GetTempPath(), "zapmq-test-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(file, """{ "orders": { "RetentionSeconds": 900, "Paused": true } }""");
        var app = ServerHost.Build([], builder => Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(builder.Configuration, new Dictionary<string, string?>
        {
            ["ZapMQ:Port"] = "0",
            ["ZapMQ:Panel:Enabled"] = "false",
            ["ZapMQ:QueueDefinitionsFile"] = file,
            ["ZapMQ:Queues:orders:RetentionSeconds"] = "60",
            ["ZapMQ:Queues:other:RetentionSeconds"] = "30"
        }));
        try
        {
            var broker = app.Services.GetRequiredService<Core.Broker>();

            // The file goes on top of the settings; what it does not mention stays.
            Assert.Equal(TimeSpan.FromSeconds(900), broker.GetQueueOptions("orders")!.Retention);
            Assert.True(broker.GetQueueOptions("orders")!.Paused);
            Assert.Equal(TimeSpan.FromSeconds(30), broker.GetQueueOptions("other")!.Retention);
        }
        finally
        {
            await app.DisposeAsync();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_queue_can_be_paused_resumed_and_emptied()
    {
        var queue = Queue();
        await Publish(queue);
        await Publish(queue);
        await Api.PutAsJsonAsync($"api/queues/{queue}/settings", new { retentionSeconds = 600 });

        Assert.Equal(HttpStatusCode.NoContent, (await Api.PostAsync($"api/queues/{queue}/pause", null)).StatusCode);
        Assert.True((bool)(await Get($"api/queues/{queue}"))["snapshot"]!["paused"]!);
        Assert.Equal("{\"result\":[\"\"]}", await server.Http.GetStringAsync($"GetMessage/{queue}"));
        Assert.True((bool)JObject.Parse(await File.ReadAllTextAsync(server.DefinitionsFile))[queue]!["Paused"]!);

        // Editing the definition does not undo the pause.
        await Api.PutAsJsonAsync($"api/queues/{queue}/settings", new { retentionSeconds = 900 });
        Assert.True((bool)(await Get($"api/queues/{queue}"))["snapshot"]!["paused"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await Api.PostAsync($"api/queues/{queue}/resume", null)).StatusCode);
        Assert.False((bool)(await Get($"api/queues/{queue}"))["snapshot"]!["paused"]!);
        Assert.Equal(900, (int)(await Get($"api/queues/{queue}"))["settings"]!["retentionSeconds"]!);
        Assert.NotEqual("{\"result\":[\"\"]}", await server.Http.GetStringAsync($"GetMessage/{queue}"));

        var purged = JObject.Parse(await (await Api.PostAsync($"api/queues/{queue}/purge", null)).Content.ReadAsStringAsync());
        Assert.Equal(1, (int)purged["purged"]!);
        Assert.Equal(0, (int)(await Get($"api/queues/{queue}"))["snapshot"]!["pending"]!);
        Assert.Empty(await Get($"api/dead-letters/{queue}"));
    }

    [Fact]
    public async Task Dead_letters_can_be_seen_put_back_and_discarded()
    {
        var queue = Queue();
        await using var consumer = await V2Client.ConnectAsync(server.Port, "fragile-service");
        await consumer.RequestAsync("bind", queue);
        await Publish(queue, "{\"n\":1}");
        await Publish(queue, "{\"n\":2}");
        await consumer.NextPushAsync();
        consumer.Drop();

        JToken letters = new JArray();
        for (var attempt = 0; attempt < 50 && !letters.Any(); attempt++)
        {
            await Task.Delay(100);
            letters = await Get($"api/dead-letters/{queue}");
        }
        var letter = Assert.Single(letters);
        Assert.Equal("unconfirmed", (string?)letter["reason"]);
        Assert.Contains("fragile-service", (string?)letter["consumer"]);
        Assert.Equal(1, (int)letter["body"]!["n"]!);
        Assert.Contains(await Get("api/dead-letters"), item => (string?)item["queue"] == queue && (int)item["unconfirmed"]! == 1);
        Assert.Equal(1, (int)(await Get("api/queues")).Single(item => (string?)item["name"] == queue)["deadLetters"]!);

        var requeued = await Api.PostAsync($"api/dead-letters/{queue}/{Uri.EscapeDataString((string)letter["id"]!)}/requeue", null);
        Assert.Equal(HttpStatusCode.OK, requeued.StatusCode);
        Assert.Empty(await Get($"api/dead-letters/{queue}"));
        Assert.Equal(2, (int)(await Get($"api/queues/{queue}"))["snapshot"]!["pending"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.DeleteAsync($"api/dead-letters/{queue}/nope")).StatusCode);
    }

    [Fact]
    public async Task The_live_stream_keeps_sending_the_state_of_the_broker()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/live");
        using var response = await Api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<JObject>();
        while (events.Count < 2)
        {
            var line = await reader.ReadLineAsync(patience.Token);
            if (line is not null && line.StartsWith("data: "))
                events.Add(JObject.Parse(line[6..]));
        }

        Assert.All(events, e =>
        {
            Assert.Equal(ServerHost.Version, (string?)e["overview"]!["version"]);
            Assert.Equal(JTokenType.Array, e["queues"]!.Type);
        });
    }

    [Fact]
    public async Task Without_the_interface_built_in_the_port_says_so()
    {
        using var anonymous = Anonymous();

        var page = await anonymous.GetAsync("");

        // The test build carries no interface; a real one answers the page of the panel here.
        var text = await page.Content.ReadAsStringAsync();
        Assert.True(text.Contains("does not carry the panel interface") || text.Contains("<base href=\"/\">"), text[..Math.Min(200, text.Length)]);
    }
}
