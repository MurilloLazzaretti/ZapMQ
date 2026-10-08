using System.Text.Json;
using ZapMQ.Core;
using ZapMQ.Server.Admin;

namespace ZapMQ.Server.Panel;

/// <summary>
/// The queue settings made through the administration, kept in <c>queues.json</c> next to the
/// executable so that they outlive a restart. At start they go on top of whatever
/// <c>appsettings.json</c> says.
/// </summary>
public sealed class QueueDefinitionStore(Broker broker, ILogger<QueueDefinitionStore> logger)
{

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly object _gate = new();

    public required string Path { get; init; }

    private string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// Applies what the file holds. Called once, before the service starts answering.
    /// </summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(Path))
                return;

            var definitions = JsonSerializer.Deserialize<Dictionary<string, QueueSettingsOptions>>(File.ReadAllText(Path)) ?? [];
            foreach (var (queue, settings) in definitions)
                broker.ConfigureQueue(queue, QueueSettingsMapping.ToCore(settings));
            logger.LogInformation("{Count} queue definitions read from {File}", definitions.Count, FileName);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogError("{File} could not be read and is being ignored: {Error}", FileName, error.Message);
        }
    }

    /// <summary>
    /// Changes the settings of a queue, effective at once, and writes the file. Null goes back
    /// to the defaults.
    /// </summary>
    public void Set(string queue, QueueOptions? options)
    {
        lock (_gate)
        {
            broker.ConfigureQueue(queue, options);
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var definitions = broker.GetAllQueueOptions()
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => QueueSettingsMapping.ToOptions(pair.Value));
            // Written aside and moved into place, so a crash never leaves half a file.
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(definitions, Format));
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogError("{File} could not be written; the change is in force but will be lost on restart: {Error}", FileName, error.Message);
        }
    }
}
