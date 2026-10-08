using System.Net;
using System.Net.Http.Json;
using Newtonsoft.Json.Linq;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The panel routes that talk to the Worker Control over its administration queue. Here the
/// Worker Control is a v2 client that answers what the test tells it to.
/// </summary>
[Collection("worker control queue")]
public class WorkerControlBridgeTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private HttpClient Api => server.Admin;

    /// <summary>
    /// Stands for the Worker Control: consumes its queue and answers each request with what
    /// the given function returns, keeping what was asked.
    /// </summary>
    private async Task<(V2Client Client, List<JObject> Asked)> Pretend(Func<JObject, JObject> answer)
    {
        var client = await V2Client.ConnectAsync(server.Port, "WorkerControl");
        var asked = new List<JObject>();
        await client.RequestAsync("bind", "WorkerControlAdmin");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var delivery = await client.NextPushAsync();
                var request = (JObject)delivery["message"]!["body"]!;
                lock (asked)
                    asked.Add(request);
                await client.RequestAsync("respond", "WorkerControlAdmin", frame =>
                {
                    frame["messageId"] = delivery["message"]!["id"];
                    frame["response"] = answer(request);
                });
            }
        });
        return (client, asked);
    }

    [Fact]
    public async Task Without_the_worker_control_connected_the_panel_says_so_and_leaves_nothing_behind()
    {
        long Published() => (long?)JArray.Parse(Api.GetStringAsync("api/queues").Result).FirstOrDefault(queue => (string?)queue["name"] == "WorkerControlAdmin")?["published"] ?? 0;
        var before = Published();

        var response = await Api.GetAsync("api/workers/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unreachable", (string?)body["code"]);
        Assert.Contains("não está conectado", (string?)body["error"]);
        // Nothing was published to a queue nobody consumes.
        Assert.Equal(before, Published());
    }

    [Fact]
    public async Task A_command_goes_to_the_queue_and_its_answer_comes_back_as_it_is()
    {
        var (worker, asked) = await Pretend(request => new JObject
        {
            ["Ok"] = true,
            ["Version"] = "2.0.0",
            ["Groups"] = new JArray(new JObject { ["Name"] = "Pedidos", ["Workers"] = new JArray() }),
            ["Echo"] = request["Command"]
        });
        await using var _ = worker;

        var status = JObject.Parse(await Api.GetStringAsync("api/workers/status"));

        Assert.True((bool)status["Ok"]!);
        Assert.Equal("Status", (string?)status["Echo"]);
        Assert.Equal("Pedidos", (string?)status["Groups"]![0]!["Name"]);
        var request = Assert.Single(asked);
        Assert.Equal("Status", (string?)request["Command"]);
        Assert.Equal(1, (int)request["Version"]!);
        // Who is logged in to the panel goes along, for the history of the Worker Control.
        Assert.Equal("admin", (string?)request["By"]);
    }

    [Fact]
    public async Task The_actions_carry_what_they_refer_to()
    {
        var (worker, asked) = await Pretend(_ => new JObject { ["Ok"] = true });
        await using var __ = worker;

        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsJsonAsync("api/workers/groups/Pedidos/workers", new { totalWorkers = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsJsonAsync("api/workers/groups/Pedidos/enabled", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("api/workers/groups/Pedidos/restart", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("api/workers/processes/4812/restart", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("api/workers/events?group=Pedidos&kind=WorkerCrashed&limit=10")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("api/workers/health?pid=4812")).StatusCode);
        // The configuration goes as it is written, with the names of the Worker Control.
        var config = new StringContent("""{ "config": { "ZapMQHost": "localhost", "WorkerGroups": [] } }""", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await Api.PutAsync("api/workers/config", config)).StatusCode);

        Assert.Equal(
            ["SetGroupWorkers", "SetGroupEnabled", "RestartGroup", "RestartWorker", "Events", "Health", "SetConfig"],
            asked.Select(request => (string?)request["Command"]));
        Assert.Equal(5, (int)asked[0]["TotalWorkers"]!);
        Assert.Equal("Pedidos", (string?)asked[0]["Group"]);
        Assert.False((bool)asked[1]["Enabled"]!);
        Assert.Equal(4812, (int)asked[3]["ProcessId"]!);
        Assert.Equal("WorkerCrashed", (string?)asked[4]["Kind"]);
        Assert.Equal(10, (int)asked[4]["Limit"]!);
        Assert.Equal(4812, (int)asked[5]["ProcessId"]!);
        Assert.Equal("localhost", (string?)asked[6]["Config"]!["ZapMQHost"]);
    }

    [Theory]
    [InlineData("not-found", HttpStatusCode.NotFound)]
    [InlineData("invalid-config", HttpStatusCode.BadRequest)]
    [InlineData("invalid-request", HttpStatusCode.BadRequest)]
    [InlineData("failed", HttpStatusCode.BadGateway)]
    [InlineData("invalid-state", HttpStatusCode.Conflict)]
    [InlineData("unknown-command", HttpStatusCode.NotImplemented)]
    public async Task A_refusal_of_the_worker_control_comes_back_with_its_reason(string code, HttpStatusCode expected)
    {
        var (worker, _) = await Pretend(_ => new JObject
        {
            ["Ok"] = false,
            ["Error"] = new JObject { ["Code"] = code, ["Message"] = "Porque não" }
        });
        await using var __ = worker;

        var response = await Api.PostAsync("api/workers/groups/Nope/restart", null);

        Assert.Equal(expected, response.StatusCode);
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, (string?)body["code"]);
        Assert.Equal("Porque não", (string?)body["error"]);
    }

    [Fact]
    public async Task These_routes_ask_for_the_session_too()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.PanelPort}/") };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/workers/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("api/workers/groups/Pedidos/restart", null)).StatusCode);
    }

    [Fact]
    public async Task The_services_of_the_machine_are_asked_for_and_acted_on_by_name()
    {
        var (worker, asked) = await Pretend(request => new JObject { ["Ok"] = true, ["Command"] = request["Command"] });
        await using var _ = worker;

        var installed = JObject.Parse(await Api.GetStringAsync("api/workers/services/installed"));
        Assert.Equal("ListServices", (string?)installed["Command"]);

        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("api/workers/services/Meu%20Servico/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("api/workers/services/Orders/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("api/workers/services/Orders/restart", null)).StatusCode);

        lock (asked)
        {
            Assert.Equal(["ListServices", "StartService", "StopService", "RestartService"], asked.Select(request => (string?)request["Command"]));
            Assert.Equal("Meu Servico", (string?)asked[1]["Name"]);
            Assert.Equal("admin", (string?)asked[3]["By"]);
        }
    }
}
