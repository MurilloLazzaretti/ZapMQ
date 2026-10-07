using ZapMQ.Core;
using ZapMQ.Server.V1;

namespace ZapMQ.Server;

public static class ServerHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // A Windows service starts in System32, so appsettings.json is resolved from the executable folder.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        configure?.Invoke(builder);

        var options = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();

        builder.Services.AddWindowsService(service => service.ServiceName = "ZapMQ");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(services => new Broker(
            new BrokerOptions
            {
                Retention = TimeSpan.FromSeconds(options.RetentionSeconds),
                EmptyQueueLifetime = TimeSpan.FromSeconds(options.EmptyQueueLifetimeSeconds)
            },
            services.GetRequiredService<TimeProvider>()));
        builder.Services.AddHostedService<SweeperService>();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.ListenAnyIP(options.Port);
            kestrel.Limits.MaxRequestLineSize = options.MaxRequestLineBytes;
            kestrel.Limits.MaxRequestBufferSize = Math.Max(
                kestrel.Limits.MaxRequestBufferSize ?? 0, options.MaxRequestLineBytes + 64 * 1024L);
        });

        var app = builder.Build();

        app.MapDataSnap();
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapGet("/metrics", (Broker broker) => Results.Json(broker.GetQueues()));

        return app;
    }
}
