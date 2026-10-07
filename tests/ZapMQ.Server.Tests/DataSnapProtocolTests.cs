using System.Net;
using Newtonsoft.Json.Linq;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The 1.x wire protocol, exercised with plain HTTP requests.
/// </summary>
public class DataSnapProtocolTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static string Queue() => "q" + Guid.NewGuid().ToString("N");

    private async Task<string> Call(string path)
    {
        using var response = await server.Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var envelope = JObject.Parse(await response.Content.ReadAsStringAsync());
        return (string)((JArray)envelope["result"]!).Single()!;
    }

    private Task<string> Publish(string queue, string json) =>
        Call($"UpdateMessage/{queue}/{Uri.EscapeDataString(json)}");

    [Fact]
    public async Task GetMessage_on_an_empty_queue_answers_an_empty_result()
    {
        using var response = await server.Http.GetAsync($"GetMessage/{Queue()}");

        Assert.Equal("{\"result\":[\"\"]}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Published_message_comes_back_in_the_1x_envelope()
    {
        var queue = Queue();

        var id = await Publish(queue, "{\"Id\":null,\"Body\":{\"name\":\"x\",\"n\":1.50},\"RPC\":false,\"TTL\":0,\"Response\":null}");
        var delivered = await Call($"GetMessage/{queue}");

        Assert.Equal(
            "{\"Id\":\"" + id + "\",\"Body\":{\"name\":\"x\",\"n\":1.50},\"RPC\":false,\"TTL\":0,\"Response\":{}}",
            delivered);
        Assert.Equal(string.Empty, await Call($"GetMessage/{queue}"));
    }

    [Fact]
    public async Task Body_survives_slashes_accents_spaces_and_reserved_characters()
    {
        var queue = Queue();
        var body = new JObject
        {
            ["path"] = "D:\\files\\a b/c.bmp",
            ["url"] = "http://host:80/x?y=1&z=2#frag",
            ["text"] = "ação çãõ ñ € + % \"quoted\"",
            ["nested"] = new JObject { ["list"] = new JArray(1, "two", JValue.CreateNull(), true) }
        };

        await Publish(queue, new JObject { ["Body"] = body, ["RPC"] = false, ["TTL"] = 0 }.ToString());
        var delivered = JObject.Parse(await Call($"GetMessage/{queue}"));

        Assert.True(JToken.DeepEquals(body, delivered["Body"]));
    }

    [Fact]
    public async Task Payload_with_unescaped_slashes_is_kept_whole()
    {
        var queue = Queue();

        await Call($"UpdateMessage/{queue}/" + "{\"Body\":{\"path\":\"a/b/c\"},\"RPC\":false,\"TTL\":0}".Replace("\"", "%22").Replace("{", "%7B").Replace("}", "%7D"));
        var delivered = JObject.Parse(await Call($"GetMessage/{queue}"));

        Assert.Equal("a/b/c", (string)delivered["Body"]!["path"]!);
    }

    [Fact]
    public async Task Large_message_fits_in_the_url()
    {
        var queue = Queue();
        var text = new string('x', 300_000);

        await Publish(queue, new JObject { ["Body"] = new JObject { ["text"] = text }, ["RPC"] = false, ["TTL"] = 0 }.ToString());
        var delivered = JObject.Parse(await Call($"GetMessage/{queue}"));

        Assert.Equal(text, (string)delivered["Body"]!["text"]!);
    }

    [Fact]
    public async Task Body_that_is_not_an_object_becomes_an_empty_object()
    {
        var queue = Queue();

        await Publish(queue, "{\"Body\":\"text\",\"RPC\":false,\"TTL\":0}");
        var delivered = JObject.Parse(await Call($"GetMessage/{queue}"));

        Assert.Equal("{}", delivered["Body"]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public async Task Rpc_round_trip()
    {
        var queue = Queue();

        var id = await Publish(queue, "{\"Body\":{\"ask\":1},\"RPC\":true,\"TTL\":0}");
        Assert.Equal(string.Empty, await Call($"GetRPCResponse/{queue}/{Uri.EscapeDataString(id)}"));

        var delivered = JObject.Parse(await Call($"GetMessage/{queue}"));
        Assert.True((bool)delivered["RPC"]!);
        Assert.Equal(id, (string)delivered["Id"]!);

        Assert.Equal("OK", await Call($"UpdateRPCResponse/{queue}/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString("{\"answer\":42}")}"));

        var answered = await Call($"GetRPCResponse/{queue}/{Uri.EscapeDataString(id)}");
        Assert.Equal("{\"Id\":\"" + id + "\",\"Body\":{\"ask\":1},\"RPC\":true,\"TTL\":0,\"Response\":{\"answer\":42}}", answered);
        Assert.Equal(string.Empty, await Call($"GetRPCResponse/{queue}/{Uri.EscapeDataString(id)}"));
    }

    [Fact]
    public async Task UpdateRPCResponse_for_an_unknown_message_answers_empty()
    {
        Assert.Equal(string.Empty, await Call($"UpdateRPCResponse/{Queue()}/%7Bnope%7D/%7B%7D"));
    }

    [Fact]
    public async Task Message_expires_after_its_ttl()
    {
        var queue = Queue();

        await Publish(queue, "{\"Body\":{},\"RPC\":false,\"TTL\":200}");
        await Task.Delay(600);

        Assert.Equal(string.Empty, await Call($"GetMessage/{queue}"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public async Task Invalid_publication_is_an_error(string payload)
    {
        using var response = await server.Http.GetAsync($"UpdateMessage/{Queue()}/{Uri.EscapeDataString(payload)}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Invalid JSON format", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!);
    }

    [Fact]
    public async Task Unknown_method_is_an_error()
    {
        using var response = await server.Http.GetAsync("Nope/x");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_consumers_never_receive_the_same_message()
    {
        var queue = Queue();
        const int total = 300;
        for (var i = 0; i < total; i++)
            await Publish(queue, "{\"Body\":{\"i\":" + i + "},\"RPC\":false,\"TTL\":0}");

        var consumers = Enumerable.Range(0, 12).Select(async _ =>
        {
            var mine = new List<string>();
            while (await Call($"GetMessage/{queue}") is { Length: > 0 } message)
                mine.Add((string)JObject.Parse(message)["Id"]!);
            return mine;
        });
        var received = (await Task.WhenAll(consumers)).SelectMany(ids => ids).ToList();

        Assert.Equal(total, received.Count);
        Assert.Equal(total, received.Distinct().Count());
    }
}
