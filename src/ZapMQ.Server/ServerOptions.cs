namespace ZapMQ.Server;

/// <summary>
/// Settings read from the <c>ZapMQ</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class ServerOptions
{
    public const string Section = "ZapMQ";

    public int Port { get; set; } = 5679;

    public int RetentionSeconds { get; set; } = 180;

    public int EmptyQueueLifetimeSeconds { get; set; } = 60;

    /// <summary>
    /// Folder of the daily log files, relative to the executable unless it is a full path.
    /// </summary>
    public string LogDirectory { get; set; } = "logs";

    /// <summary>
    /// How many daily log files are kept.
    /// </summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>
    /// Verbose, Debug, Information, Warning or Error.
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// The 1.x protocol carries the whole message in the URL, so the request line has to be
    /// allowed to grow far beyond the web server default.
    /// </summary>
    public int MaxRequestLineBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// File that keeps the queue settings made through the panel, relative to the executable
    /// unless it is a full path.
    /// </summary>
    /// <summary>
    /// Where the message models saved through the panel are kept. Relative to the executable
    /// unless it is a full path.
    /// </summary>
    public string MessageModelsFile { get; set; } = "models.json";

    public string QueueDefinitionsFile { get; set; } = "queues.json";

    public V2Options V2 { get; set; } = new();

    public DeadLetterOptions DeadLetters { get; set; } = new();

    public PanelOptions Panel { get; set; } = new();

    /// <summary>
    /// Settings of individual queues, by name.
    /// </summary>
    public Dictionary<string, QueueSettingsOptions> Queues { get; set; } = new(StringComparer.Ordinal);
}

public sealed class V2Options
{
    /// <summary>
    /// Off, the server answers the 1.x protocol only and v2 wrappers fall back to it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int MaxFrameBytes { get; set; } = 4 * 1024 * 1024;

    public int PingSeconds { get; set; } = 15;

    public int PingTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// The administration panel, served on a port of its own.
/// </summary>
public sealed class PanelOptions
{
    public const string DefaultUser = "admin";
    public const string DefaultPassword = "admin";

    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 5680;

    /// <summary>
    /// The path the panel is published under by a reverse proxy. The panel answers there and
    /// at the root of its port.
    /// </summary>
    public string BasePath { get; set; } = "/zapmq";

    public string User { get; set; } = DefaultUser;

    public string Password { get; set; } = DefaultPassword;

    public int SessionHours { get; set; } = 8;

    public bool HasDefaultPassword => User == DefaultUser && Password == DefaultPassword;
}

public sealed class DeadLetterOptions
{
    public int MaxMessagesPerQueue { get; set; } = 1000;

    public int MaxAgeHours { get; set; } = 168;
}

/// <summary>
/// One queue. Whatever is left out uses the general value.
/// </summary>
public sealed class QueueSettingsOptions
{
    public int? RetentionSeconds { get; set; }

    public bool RedeliverUnconfirmed { get; set; }

    /// <summary>
    /// A paused queue keeps receiving and hands nothing to anybody.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// How many of the last messages that went through the queue are kept to be looked at
    /// in the panel. Zero or absent keeps none.
    /// </summary>
    public int? KeepRecent { get; set; }

    public QueueDeadLetterOptions? DeadLetters { get; set; }
}

public sealed class QueueDeadLetterOptions
{
    public int? MaxMessagesPerQueue { get; set; }

    public int? MaxAgeHours { get; set; }
}
