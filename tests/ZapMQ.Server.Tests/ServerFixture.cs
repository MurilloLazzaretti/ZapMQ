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

    public HttpClient Http { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _app = ServerHost.Build([], builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ZapMQ:Port"] = "0" }));
        await _app.StartAsync();

        Port = new Uri(_app.Urls.First().Replace("[::]", "localhost").Replace("0.0.0.0", "localhost")).Port;
        Http = new HttpClient { BaseAddress = new Uri($"http://localhost:{Port}/datasnap/rest/TZapMethods/") };
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
