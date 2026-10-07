namespace ZapMQ.Server;

/// <summary>
/// Settings read from the <c>ZapMQ</c> section of <c>ZapMQ.json</c>, or from the 1.x
/// <c>ZapMQ.ini</c> when only that file exists.
/// </summary>
public sealed class ServerOptions
{
    public const string Section = "ZapMQ";

    public int Port { get; set; } = 5679;

    public int RetentionSeconds { get; set; } = 180;

    public int EmptyQueueLifetimeSeconds { get; set; } = 60;

    /// <summary>
    /// The 1.x protocol carries the whole message in the URL, so the request line has to be
    /// allowed to grow far beyond the web server default.
    /// </summary>
    public int MaxRequestLineBytes { get; set; } = 4 * 1024 * 1024;
}
