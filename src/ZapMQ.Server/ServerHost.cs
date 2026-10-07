using System.Reflection;
using Serilog;
using Serilog.Events;
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

        // The file is the diagnostic log. The providers the host already registered stay on, so
        // warnings and errors still reach the Windows Event Log when running as a service.
        var logFile = Path.Combine(Path.GetFullPath(options.LogDirectory, AppContext.BaseDirectory), "zapmq-.log");
        var logLevel = Enum.TryParse<LogEventLevel>(options.LogLevel, ignoreCase: true, out var level) ? level : LogEventLevel.Information;
        builder.Services.AddSerilog(log => log
            .MinimumLevel.Is(logLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.LogRetentionDays,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"),
            writeToProviders: true);
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

        var lifetimeLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ZapMQ");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        app.Lifetime.ApplicationStarted.Register(() => lifetimeLog.LogInformation(
            "ZapMQ {Version} started on port {Port} (retention {Retention} s)", version, options.Port, options.RetentionSeconds));
        app.Lifetime.ApplicationStopping.Register(() => lifetimeLog.LogInformation("ZapMQ stopping"));

        app.MapDataSnap();
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapGet("/metrics", (Broker broker) => Results.Json(broker.GetQueues()));

        return app;
    }
}
