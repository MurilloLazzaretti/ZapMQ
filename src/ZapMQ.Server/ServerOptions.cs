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

    public V2Options V2 { get; set; } = new();

    public DeadLetterOptions DeadLetters { get; set; } = new();

    /// <summary>
    /// Settings of individual queues, by name.
    /// </summary>
    public Dictionary<string, QueueSettingsOptions> Queues { get; set; } = new(StringComparer.Ordinal);
}

public sealed class V2Options
{
    public int MaxFrameBytes { get; set; } = 4 * 1024 * 1024;

    public int PingSeconds { get; set; } = 15;

    public int PingTimeoutSeconds { get; set; } = 30;
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

    public QueueDeadLetterOptions? DeadLetters { get; set; }
}

public sealed class QueueDeadLetterOptions
{
    public int? MaxMessagesPerQueue { get; set; }

    public int? MaxAgeHours { get; set; }
}
