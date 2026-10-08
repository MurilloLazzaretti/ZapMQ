using System.Reflection;
using Serilog;
using Serilog.Events;
using ZapMQ.Core;
using ZapMQ.Server.Admin;
using ZapMQ.Server.Panel;
using ZapMQ.Server.V1;
using ZapMQ.Server.V2;

namespace ZapMQ.Server;

public static class ServerHost
{
    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    public static object Metrics(Broker broker, V2Connections connections) => new
    {
        version = Version,
        queues = broker.GetQueues(),
        deadLetters = broker.GetDeadLetterSummary(),
        connections = connections.Describe()
    };

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
        builder.Services.AddSingleton(services =>
        {
            var brokerOptions = new BrokerOptions
            {
                Retention = TimeSpan.FromSeconds(options.RetentionSeconds),
                EmptyQueueLifetime = TimeSpan.FromSeconds(options.EmptyQueueLifetimeSeconds),
                DeadLetterLimit = options.DeadLetters.MaxMessagesPerQueue,
                DeadLetterMaxAge = TimeSpan.FromHours(options.DeadLetters.MaxAgeHours)
            };
            // The trace of the supervised processes is only worth having while it is watched.
            brokerOptions.DisposablePrefixes.Add(TraceHub.LinesPrefix);
            foreach (var (queue, settings) in options.Queues)
                brokerOptions.Queues[queue] = QueueSettingsMapping.ToCore(settings);

            return new Broker(brokerOptions, services.GetRequiredService<TimeProvider>());
        });
        builder.Services.AddSingleton<V2Connections>();
        // Which process is behind a 1.x client of this machine. A test may put its own first.
        if (!builder.Services.Any(service => service.ServiceType == typeof(V1.IPeerResolver)))
        {
            if (OperatingSystem.IsWindows())
                builder.Services.AddSingleton<V1.IPeerResolver, V1.WindowsPeerResolver>();
            else
                builder.Services.AddSingleton<V1.IPeerResolver, V1.NoPeerResolver>();
        }
        builder.Services.AddSingleton<V1.V1Callers>();
        builder.Services.AddHostedService<SweeperService>();
        builder.Services.AddSingleton(services => new PanelUserStore(Path.GetFullPath(options.PanelUsersFile, AppContext.BaseDirectory), options.Panel,
            services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<PanelUserStore>>()));
        builder.Services.AddSingleton(services => new PanelAuth(options.Panel, services.GetRequiredService<PanelUserStore>(), services.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(services => new QueueDefinitionStore(services.GetRequiredService<Broker>(), services.GetRequiredService<ILogger<QueueDefinitionStore>>())
        {
            Path = Path.GetFullPath(options.QueueDefinitionsFile, AppContext.BaseDirectory)
        });
        builder.Services.AddSingleton<WorkerControlClient>();
        builder.Services.AddSingleton<TraceHub>();
        builder.Services.AddSingleton<MessageTap>();
        builder.Services.AddSingleton(services => new MessageModelStore(services.GetRequiredService<ILogger<MessageModelStore>>())
        {
            Path = Path.GetFullPath(options.MessageModelsFile, AppContext.BaseDirectory)
        });
        builder.Services.AddSingleton<MetricsSampler>();
        builder.Services.AddHostedService(services => services.GetRequiredService<MetricsSampler>());

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.ListenAnyIP(options.Port);
            if (options.Panel.Enabled)
                kestrel.ListenPanel(options.Panel);
            kestrel.Limits.MaxRequestLineSize = options.MaxRequestLineBytes;
            kestrel.Limits.MaxRequestBufferSize = Math.Max(
                kestrel.Limits.MaxRequestBufferSize ?? 0, options.MaxRequestLineBytes + 64 * 1024L);
        });

        var app = builder.Build();

        var lifetimeLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ZapMQ");
        app.Lifetime.ApplicationStarted.Register(() => lifetimeLog.LogInformation(
            "ZapMQ {Version} started on port {Port} (retention {Retention} s)", Version, options.Port, options.RetentionSeconds));
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            lifetimeLog.LogInformation("ZapMQ stopping");
            // Runs before the connections are cut: clients are told and get a moment to confirm
            // what they are processing.
            app.Services.GetRequiredService<V2Connections>().DrainAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        });

        // What the panel changed in the queues goes on top of what the settings file says.
        app.Services.GetRequiredService<QueueDefinitionStore>().Load();
        // From now on the queues that ask for their last messages to be kept are watched.
        app.Services.GetRequiredService<MessageTap>();

        var startedAt = DateTimeOffset.UtcNow;
        if (options.Panel.Enabled)
        {
            if (app.Services.GetRequiredService<PanelUserStore>().List().Any(user => user is { Master: true, InitialPassword: true }))
                app.Lifetime.ApplicationStarted.Register(() => lifetimeLog.LogWarning(
                    "The master of the panel still has the initial password; change it in the panel, under the name of the user"));
            app.UsePanel(options, startedAt);
        }

        if (options.V2.Enabled)
            app.MapV2(options.V2);
        app.MapDataSnap();
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapGet("/metrics", (Broker broker, V2Connections connections) => Results.Json(Metrics(broker, connections)));

        return app;
    }
}
