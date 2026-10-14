using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace ZapMQ.Server.Panel;

/// <summary>
/// What of the ZapMQ itself can be set from the panel. It is kept in a file of its own, read
/// after <c>appsettings.json</c> and before the environment variables, so the file that comes
/// with the program is never written; it is read when the service starts.
/// </summary>
public sealed class ZapMQSettings(string path, ServerOptions running)
{
    public const string DefaultFile = "zapmq.settings.json";

    public string Path { get; } = path;

    /// <summary>
    /// One setting: where it is under <c>ZapMQ</c>, what it takes, and what the service runs with now.
    /// </summary>
    public sealed record Setting(string Key, string Kind, Func<ServerOptions, object?> Current, int Minimum = 0, int Maximum = int.MaxValue, string[]? Choices = null);

    public static readonly Setting[] All =
    [
        new("Port", "number", options => options.Port, 1, 65535),
        new("RetentionSeconds", "number", options => options.RetentionSeconds, 1),
        new("EmptyQueueLifetimeSeconds", "number", options => options.EmptyQueueLifetimeSeconds),
        new("LogDirectory", "text", options => options.LogDirectory),
        new("LogRetentionDays", "number", options => options.LogRetentionDays, 1),
        new("LogLevel", "choice", options => options.LogLevel, Choices: ["Verbose", "Debug", "Information", "Warning", "Error"]),
        new("MaxRequestLineBytes", "number", options => options.MaxRequestLineBytes, 1024),
        new("Panel.Port", "number", options => options.Panel.Port, 1, 65535),
        new("Panel.BasePath", "text", options => options.Panel.BasePath),
        new("Panel.SessionHours", "number", options => options.Panel.SessionHours, 1, 24 * 30),
        new("V2.Enabled", "bool", options => options.V2.Enabled),
        new("V2.MaxFrameBytes", "number", options => options.V2.MaxFrameBytes, 1024),
        new("V2.PingSeconds", "number", options => options.V2.PingSeconds, 1),
        new("V2.PingTimeoutSeconds", "number", options => options.V2.PingTimeoutSeconds, 1),
        new("DeadLetters.MaxMessagesPerQueue", "number", options => options.DeadLetters.MaxMessagesPerQueue),
        new("DeadLetters.MaxAgeHours", "number", options => options.DeadLetters.MaxAgeHours, 1),
        new("Transport.Environment", "text", options => options.Transport.Environment),
        new("Transport.Directory", "text", options => options.Transport.Directory),
    ];

    /// <summary>
    /// Puts the file among the sources of the configuration: after the files that come with the
    /// program, before what the machine says in its environment.
    /// </summary>
    public static string AddTo(IConfigurationBuilder configuration, IConfiguration current)
    {
        var path = System.IO.Path.GetFullPath(current[ServerOptions.Section + ":SettingsFile"] is { Length: > 0 } said ? said : DefaultFile, AppContext.BaseDirectory);
        var sources = configuration.Sources;
        var after = sources.Select((source, index) => (source, index)).LastOrDefault(pair => pair.source is JsonConfigurationSource).index;
        var source = new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(System.IO.Path.GetDirectoryName(path)!),
            Path = System.IO.Path.GetFileName(path),
            Optional = true,
            ReloadOnChange = false
        };
        sources.Insert(sources.Any(candidate => candidate is JsonConfigurationSource) ? after + 1 : 0, source);
        return path;
    }

    private JsonObject Read()
    {
        try
        {
            return File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path))?[ServerOptions.Section] is JsonObject section ? section : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonNode? At(JsonObject root, string key)
    {
        JsonNode? at = root;
        foreach (var part in key.Split('.'))
            at = at is JsonObject parent ? parent[part] : null;
        return at;
    }

    /// <summary>
    /// For each setting: what the service runs with, and what is written to be taken at the next start.
    /// </summary>
    public object Describe()
    {
        var saved = Read();
        return new
        {
            file = Path,
            settings = All.Select(setting => new
            {
                key = setting.Key, kind = setting.Kind, minimum = setting.Kind == "number" ? setting.Minimum : (int?)null,
                maximum = setting.Kind == "number" && setting.Maximum != int.MaxValue ? setting.Maximum : (int?)null, choices = setting.Choices,
                current = setting.Current(running), saved = At(saved, setting.Key)?.DeepClone()
            })
        };
    }

    /// <summary>
    /// Writes what was asked, in full: a setting that is not given is taken out, and the service
    /// uses what the file of the program says. Gives what is wrong, or null.
    /// </summary>
    public string? Write(Dictionary<string, JsonElement> values)
    {
        var section = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (All.FirstOrDefault(candidate => candidate.Key == key) is not { } setting)
                return $"\"{key}\" não é uma configuração que o painel altera";
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            JsonNode node;
            switch (setting.Kind)
            {
                case "number":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                        return $"{key} precisa ser um número inteiro";
                    if (number < setting.Minimum || number > setting.Maximum)
                        return $"{key} vai de {setting.Minimum} a {(setting.Maximum == int.MaxValue ? "mais" : setting.Maximum)}";
                    node = number;
                    break;
                case "bool":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return $"{key} é sim ou não";
                    node = value.GetBoolean();
                    break;
                default:
                    if (value.ValueKind != JsonValueKind.String)
                        return $"{key} precisa ser um texto";
                    var text = value.GetString()!.Trim();
                    if (text.Length == 0)
                        continue;
                    if (setting.Choices is { } choices && !choices.Contains(text, StringComparer.OrdinalIgnoreCase))
                        return $"{key} é um destes: {string.Join(", ", choices)}";
                    if (key == "Panel.BasePath" && !text.StartsWith('/'))
                        return "O caminho do painel começa com /";
                    node = text;
                    break;
            }
            var parts = key.Split('.');
            var parent = section;
            foreach (var part in parts[..^1])
                parent = parent[part] as JsonObject ?? (JsonObject)(parent[part] = new JsonObject());
            parent[parts[^1]] = node;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, new JsonObject { [ServerOptions.Section] = section }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, Path, overwrite: true);
        return null;
    }
}
