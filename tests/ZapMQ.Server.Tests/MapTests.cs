using Newtonsoft.Json.Linq;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The drawing of who talks to whom through the broker.
/// </summary>
public class MapTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static string Name(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private async Task<JObject> Map(string query = "") => JObject.Parse(await server.Admin.GetStringAsync("api/map" + query));

    private static Task<JObject> Publish(V2Client client, string queue) =>
        client.RequestAsync("publish", queue, frame => frame["body"] = new JObject { ["n"] = 1 });

    private Task PublishV1(string queue) =>
        server.Http.GetStringAsync($"UpdateMessage/{queue}/{Uri.EscapeDataString("{\"Id\":\"\",\"Body\":{\"n\":1},\"RPC\":false,\"TTL\":0}")}");

    private static JToken? Link(JObject map, string application, string queue, string kind) =>
        map["links"]!.FirstOrDefault(link => (string)link["application"]! == application && (string)link["queue"]! == queue && (string)link["kind"]! == kind);

    [Fact]
    public async Task Applications_are_joined_to_the_queues_they_publish_in_and_consume()
    {
        var (orders, billing, queue) = (Name("orders"), Name("billing"), Name("invoices"));
        await using var publisher = await V2Client.ConnectAsync(server.Port, orders);
        await using var consumer = await V2Client.ConnectAsync(server.Port, billing);
        await using var another = await V2Client.ConnectAsync(server.Port, billing);
        await consumer.RequestAsync("bind", queue);
        await another.RequestAsync("bind", queue);
        await Publish(publisher, queue);
        await Publish(publisher, queue);

        var map = await Map();

        var from = Link(map, "v2:" + orders, queue, "publish")!;
        Assert.Equal(2, (long)from["count"]!);
        Assert.False((bool)from["bound"]!);
        Assert.True((bool)Link(map, "v2:" + billing, queue, "consume")!["bound"]!);
        Assert.Null(Link(map, "v2:" + orders, queue, "consume"));

        var application = map["applications"]!.Single(item => (string)item["id"]! == "v2:" + billing);
        Assert.Equal(billing, (string)application["name"]!);
        // The two connections say they are the same process of the same machine.
        var instance = Assert.Single(application["instances"]!);
        Assert.Equal(2, (int)instance["connections"]!);
        Assert.Equal(4242, (int)instance["pid"]!);
        Assert.Equal("TESTHOST", (string)instance["host"]!);

        var drawn = map["queues"]!.Single(item => (string)item["name"]! == queue);
        Assert.True((bool)drawn["exists"]!);
        Assert.Equal(2, (int)drawn["consumers"]!);
        Assert.Equal(2, (long)drawn["published"]!);
    }

    [Fact]
    public async Task The_queues_of_each_process_are_counted_on_the_application_and_not_drawn()
    {
        var worker = Name("worker");
        await using var client = await V2Client.ConnectAsync(server.Port, worker);
        await using var supervisor = await V2Client.ConnectAsync(server.Port, Name("supervisor"));
        await client.RequestAsync("bind", "987654");
        await client.RequestAsync("bind", "987654SS");
        await client.RequestAsync("bind", "987654TR");
        await Publish(supervisor, "987654");

        var map = await Map();

        Assert.Equal(3, (int)map["applications"]!.Single(item => (string)item["id"]! == "v2:" + worker)["internalQueues"]!);
        Assert.DoesNotContain(map["queues"]!, queue => ((string)queue["name"]!).StartsWith("987654"));
        Assert.DoesNotContain(map["links"]!, link => ((string)link["queue"]!).StartsWith("987654"));
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("1234SS", true)]
    [InlineData("1234TR", true)]
    [InlineData("WorkerControlAdmin", true)]
    [InlineData("WorkerControl.Canary.77", true)]
    [InlineData("zapmq.probe", true)]
    [InlineData("Email", false)]
    [InlineData("1234X", false)]
    [InlineData("Pedidos2024", false)]
    public void Internal_queues_are_told_by_name(string queue, bool expected) =>
        Assert.Equal(expected, ParkMap.IsInternal(queue));

    [Fact]
    public async Task A_client_of_the_first_protocol_is_drawn_by_its_address()
    {
        var queue = Name("legacy");
        await PublishV1(queue);

        var map = await Map();

        var link = map["links"]!.Single(item => (string)item["queue"]! == queue);
        Assert.Equal("publish", (string)link["kind"]!);
        var application = map["applications"]!.Single(item => (string)item["id"]! == (string)link["application"]!);
        Assert.Equal("v1", (string)application["protocol"]!);
        Assert.Empty(application["instances"]!);
    }

    [Fact]
    public async Task An_application_that_left_stays_while_what_it_did_is_recent()
    {
        var (gone, queue) = (Name("gone"), Name("work"));
        await using var consumer = await V2Client.ConnectAsync(server.Port, Name("stays"));
        await consumer.RequestAsync("bind", queue);
        await using (var publisher = await V2Client.ConnectAsync(server.Port, gone))
            await Publish(publisher, queue);

        var map = await Map("?minutes=5");

        Assert.Equal(5, (int)map["windowMinutes"]!);
        Assert.NotNull(Link(map, "v2:" + gone, queue, "publish"));
        Assert.Empty(map["applications"]!.Single(item => (string)item["id"]! == "v2:" + gone)["instances"]!);
    }
}
