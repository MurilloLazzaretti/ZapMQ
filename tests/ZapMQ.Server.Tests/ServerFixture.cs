using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using ZapMQ.Server;

namespace ZapMQ.Server.Tests;

/// <summary>
/// A real server on a free local port: the 1.x protocol depends on the raw request line, which
/// an in-memory test server does not reproduce.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public int Port { get; private set; }

    /// <summary>
    /// Rooted at the 1.x routes.
    /// </summary>
    public HttpClient Http { get; private set; } = null!;

    /// <summary>
    /// Rooted at the panel port and logged in: administration, metrics, health.
    /// </summary>
    public HttpClient Admin { get; private set; } = null!;

    /// <summary>
    /// The port of the panel.
    /// </summary>
    public int PanelPort { get; private set; }

    private string _definitions = "";

    /// <summary>
    /// Where this server keeps the queue settings made through the panel.
    /// </summary>
    public string DefinitionsFile => _definitions;

    public IServiceProvider Services => _app!.Services;

    public async Task InitializeAsync()
    {
        _definitions = Path.Combine(Path.GetTempPath(), "zapmq-test-" + Guid.NewGuid().ToString("N") + ".json");
        _app = ServerHost.Build([], builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ZapMQ:Port"] = "0",
            ["ZapMQ:Panel:Port"] = "0",
            ["ZapMQ:QueueDefinitionsFile"] = _definitions
        }));
        await _app.StartAsync();

        // In the order they were opened: messaging first, panel second.
        var ports = _app.Urls.Select(url => new Uri(url.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost")).Port).ToList();
        Port = ports[0];
        PanelPort = ports[1];
        Http = new HttpClient { BaseAddress = new Uri($"http://localhost:{Port}/datasnap/rest/TZapMethods/") };

        // The administration is on the panel port, behind its login.
        Admin = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() }) { BaseAddress = new Uri($"http://localhost:{PanelPort}/") };
        var login = await Admin.PostAsync("api/login", System.Net.Http.Json.JsonContent.Create(new { user = "admin", password = "admin" }));
        login.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        Admin.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        File.Delete(_definitions);
    }
}
