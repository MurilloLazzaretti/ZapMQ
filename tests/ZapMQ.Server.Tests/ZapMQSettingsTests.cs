using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The settings of the ZapMQ itself, written from the panel to a file of their own and taken
/// when the service starts.
/// </summary>
public sealed class ZapMQSettingsTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task The_master_writes_them_for_the_next_start_and_what_is_wrong_is_refused()
    {
        var described = JObject.Parse(await server.Admin.GetStringAsync("api/settings/zapmq"));
        var environment = described["settings"]!.Single(setting => (string?)setting["key"] == "Transport.Environment");
        Assert.Equal(("TEST", null), ((string?)environment["current"], (string?)environment["saved"]));

        var written = await server.Admin.PutAsJsonAsync("api/settings/zapmq", new Dictionary<string, object> { ["Transport.Environment"] = "QAS", ["Panel.SessionHours"] = 10, ["V2.Enabled"] = true, ["LogLevel"] = "Warning" });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        var after = JObject.Parse(await written.Content.ReadAsStringAsync());
        // Written, not in use: that is for the next start.
        var now = after["settings"]!.Single(setting => (string?)setting["key"] == "Transport.Environment");
        Assert.Equal(("TEST", "QAS"), ((string?)now["current"], (string?)now["saved"]));
        var file = JObject.Parse(File.ReadAllText((string)after["file"]!));
        Assert.Equal((10, "Warning"), ((int)file["ZapMQ"]!["Panel"]!["SessionHours"]!, (string?)file["ZapMQ"]!["LogLevel"]));

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Admin.PutAsJsonAsync("api/settings/zapmq", new Dictionary<string, object> { ["Port"] = 70000 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Admin.PutAsJsonAsync("api/settings/zapmq", new Dictionary<string, object> { ["LogLevel"] = "Chatty" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Admin.PutAsJsonAsync("api/settings/zapmq", new Dictionary<string, object> { ["Panel.User"] = "someone" })).StatusCode);

        await server.Admin.PostAsJsonAsync("api/users", new { login = "settingless", name = "Settingless", password = "a-long-password" });
        using var other = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://localhost:{server.PanelPort}/") };
        await other.PostAsJsonAsync("api/login", new { user = "settingless", password = "a-long-password" });
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("api/settings/zapmq")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync("api/settings/zapmq", new Dictionary<string, object>())).StatusCode);
    }

    [Fact]
    public async Task What_was_written_is_taken_when_the_service_starts_over_the_file_of_the_program()
    {
        var file = Path.Combine(Path.GetTempPath(), "zapmq-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, """{ "ZapMQ": { "Transport": { "Environment": "FROM-PANEL" }, "RetentionSeconds": 240 } }""");
        try
        {
            await using var app = ServerHost.Build([], builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZapMQ:SettingsFile"] = file,
                ["ZapMQ:Port"] = "0",
                ["ZapMQ:Panel:Enabled"] = "false"
            }));
            var settings = JObject.Parse(System.Text.Json.JsonSerializer.Serialize(app.Services.GetRequiredService<ZapMQSettings>().Describe()));
            string? Current(string key) => (string?)settings["settings"]!.Single(setting => (string?)setting["key"] == key)["current"];
            Assert.Equal(("FROM-PANEL", "240"), (Current("Transport.Environment"), Current("RetentionSeconds")));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
