using System.Globalization;
using System.Text.Json;

namespace ZapMQ.Server.Transport;

/// <summary>
/// The rules of this environment for what changes it: whether a new version may come in by hand,
/// or only in a package from another environment; whether whoever made a package may also approve
/// it; and at what hours packages are applied. Only the master changes them; they hold at once.
/// </summary>
public sealed class TransportPolicy
{
    /// <summary>
    /// A version brought here by hand: the zip of a folder, what was left in the inbox, a script
    /// written or imported, an application created from a zip. Off: only what arrives in a package.
    /// </summary>
    public bool ManualVersions { get; set; }

    /// <summary>
    /// Whoever made a package does not approve it here.
    /// </summary>
    public bool SeparateApprover { get; set; }

    /// <summary>
    /// The hours, in the time of this machine, packages are applied in ("22:00" to "06:00" goes
    /// past midnight). Both empty: at any time.
    /// </summary>
    public string? WindowStart { get; set; }
    public string? WindowEnd { get; set; }

    public DateTimeOffset? ChangedAt { get; set; }
    public string? ChangedBy { get; set; }

    public static TimeSpan? Hour(string? text) =>
        TimeSpan.TryParseExact(text?.Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var hour) && hour < TimeSpan.FromDays(1) ? hour : null;

    /// <summary>
    /// Whether a moment falls in the window; always, without one.
    /// </summary>
    public bool InWindow(DateTimeOffset at)
    {
        if (Hour(WindowStart) is not { } start || Hour(WindowEnd) is not { } end)
            return true;
        var time = at.ToLocalTime().TimeOfDay;
        return start <= end ? time >= start && time < end : time >= start || time < end;
    }
}

public sealed class TransportPolicyStore
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _path;
    private TransportPolicy _policy;

    public TransportPolicyStore(TransportStore store, ILogger<TransportPolicyStore> logger)
    {
        _path = Path.Combine(store.Root, "policy.json");
        try
        {
            _policy = File.Exists(_path) ? JsonSerializer.Deserialize<TransportPolicy>(File.ReadAllText(_path), Format) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable, the strictest rules stand: nothing by hand.
            logger.LogError("The rules of the transport in {File} could not be read: {Error}", _path, error.Message);
            _policy = new();
        }
    }

    public TransportPolicy Current()
    {
        lock (_gate)
            return JsonSerializer.Deserialize<TransportPolicy>(JsonSerializer.Serialize(_policy, Format), Format)!;
    }

    public TransportPolicy Set(TransportPolicy wanted, string by, DateTimeOffset now)
    {
        var start = wanted.WindowStart?.Trim();
        var end = wanted.WindowEnd?.Trim();
        if (string.IsNullOrEmpty(start) != string.IsNullOrEmpty(end))
            throw new TransportRefused("Informe o começo e o fim da janela, ou nenhum dos dois");
        if (!string.IsNullOrEmpty(start) && (TransportPolicy.Hour(start) is null || TransportPolicy.Hour(end) is null))
            throw new TransportRefused("A janela é de horas no formato 22:00");
        if (!string.IsNullOrEmpty(start) && TransportPolicy.Hour(start) == TransportPolicy.Hour(end))
            throw new TransportRefused("A janela começa e termina na mesma hora");
        var policy = new TransportPolicy
        {
            ManualVersions = wanted.ManualVersions, SeparateApprover = wanted.SeparateApprover,
            WindowStart = string.IsNullOrEmpty(start) ? null : TransportPolicy.Hour(start)!.Value.ToString(@"hh\:mm"),
            WindowEnd = string.IsNullOrEmpty(end) ? null : TransportPolicy.Hour(end)!.Value.ToString(@"hh\:mm"),
            ChangedAt = now, ChangedBy = by
        };
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(policy, Format));
            File.Move(temporary, _path, overwrite: true);
            _policy = policy;
        }
        return Current();
    }

    /// <summary>
    /// Refuses what brings a version by hand where only packages may.
    /// </summary>
    public void RequireManual()
    {
        if (!Current().ManualVersions)
            throw new TransportRefused("Neste ambiente as versões novas chegam só por transporte. Quem pode mudar isso é o usuário master, em Configurações → Regras do ambiente.", StatusCodes.Status403Forbidden);
    }
}
