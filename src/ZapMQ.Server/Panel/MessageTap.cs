using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ZapMQ.Core;
using ZapMQ.Server.V1;

namespace ZapMQ.Server.Panel;

/// <summary>
/// One step in the life of a message, as the panel shows it. Only the fields that belong to
/// the step come filled; whoever follows a message puts the steps together by its id.
/// </summary>
public sealed record TapEvent(string Kind, string Queue, string Id, DateTimeOffset At, string? Body, bool? Truncated, bool? Rpc, string? Who, string? Response, string? Reason);

/// <summary>
/// The messages going through the queues, shown to whoever is watching. A queue is only
/// watched while somebody is looking at it, or when its definition asks for its last
/// messages to be kept. Watching takes nothing from the queue and changes nothing in it.
/// </summary>
public sealed class MessageTap : IDisposable
{
    /// <summary>
    /// The most of a body, or of an answer, that is shown. What goes beyond is cut, and said so.
    /// </summary>
    public const int LongestBody = 256 * 1024;

    private readonly Broker _broker;
    private readonly object _gate = new();
    private readonly Dictionary<string, Watched> _queues = new(StringComparer.Ordinal);

    private sealed class Watched(IDisposable watching)
    {
        public IDisposable Watching { get; } = watching;
        public List<Channel<TapEvent>> Watchers { get; } = [];

        /// <summary>The last steps kept, oldest first, when the queue asks for it.</summary>
        public Queue<TapEvent> Recent { get; } = new();
        public int Keep { get; set; }
    }

    public MessageTap(Broker broker)
    {
        _broker = broker;
        broker.Observed += Receive;
        Refresh();
    }

    /// <summary>
    /// Follows the definitions of the queues: the ones that ask for their last messages are
    /// watched all the time. Called whenever a definition changes.
    /// </summary>
    public void Refresh()
    {
        var keeping = _broker.GetAllQueueOptions().Where(pair => pair.Value.KeepRecent > 0).ToDictionary(pair => pair.Key, pair => pair.Value.KeepRecent, StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var (queue, keep) in keeping)
                Of(queue).Keep = keep;
            foreach (var (queue, watched) in _queues.ToList())
            {
                if (keeping.ContainsKey(queue))
                    continue;
                watched.Keep = 0;
                watched.Recent.Clear();
                LetGo(queue, watched);
            }
        }
    }

    /// <summary>
    /// Starts watching a queue. What was kept of it comes first. Dispose the result to stop.
    /// </summary>
    public TapSubscription Subscribe(string queue)
    {
        // Somebody slow loses the oldest of what was not read yet.
        var channel = Channel.CreateBounded<TapEvent>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate)
        {
            var watched = Of(queue);
            foreach (var step in watched.Recent)
                channel.Writer.TryWrite(step);
            watched.Watchers.Add(channel);
        }
        return new TapSubscription(channel.Reader, () =>
        {
            lock (_gate)
            {
                channel.Writer.TryComplete();
                if (_queues.TryGetValue(queue, out var watched) && watched.Watchers.Remove(channel))
                    LetGo(queue, watched);
            }
        });
    }

    /// <summary>
    /// The last steps kept of a queue, oldest first. Empty for a queue that keeps none.
    /// </summary>
    public IReadOnlyList<TapEvent> Recent(string queue)
    {
        lock (_gate)
            return _queues.TryGetValue(queue, out var watched) ? [.. watched.Recent] : [];
    }

    public void Dispose()
    {
        _broker.Observed -= Receive;
        lock (_gate)
        {
            foreach (var watched in _queues.Values)
            {
                watched.Watching.Dispose();
                foreach (var watcher in watched.Watchers)
                    watcher.Writer.TryComplete();
            }
            _queues.Clear();
        }
    }

    // Under the gate.
    private Watched Of(string queue)
    {
        if (!_queues.TryGetValue(queue, out var watched))
            _queues[queue] = watched = new Watched(_broker.Watch(queue));
        return watched;
    }

    // Under the gate.
    private void LetGo(string queue, Watched watched)
    {
        if (watched.Watchers.Count > 0 || watched.Keep > 0)
            return;
        watched.Watching.Dispose();
        _queues.Remove(queue);
    }

    /// <summary>
    /// Called by the broker, sometimes under one of its locks: nothing here waits.
    /// </summary>
    private void Receive(MessageEvent e)
    {
        var step = new TapEvent(e.Kind, e.Queue, e.Id, e.At,
            Cut(e.Body), e.Body is { Length: > LongestBody } || e.Response is { Length: > LongestBody } ? true : null,
            e.Kind == "published" ? e.Rpc : null, Who(e.Party), Cut(e.Response), e.Reason);

        lock (_gate)
        {
            if (!_queues.TryGetValue(e.Queue, out var watched))
                return;
            if (watched.Keep > 0)
            {
                watched.Recent.Enqueue(step);
                // A message is a handful of steps; this many steps hold at least that many messages.
                while (watched.Recent.Count > watched.Keep * 4)
                    watched.Recent.Dequeue();
            }
            foreach (var watcher in watched.Watchers)
                watcher.Writer.TryWrite(step);
        }
    }

    private static string? Cut(string? text) => text is { Length: > LongestBody } ? text[..LongestBody] : text;

    /// <summary>
    /// Who published or received, in words: the broker writes it down in ways of its own.
    /// </summary>
    public static string? Who(string? party)
    {
        if (string.IsNullOrEmpty(party))
            return null;
        if (party.StartsWith("v2:", StringComparison.Ordinal) && party[3..].Split('|') is [_, var name, _, var pid])
            return $"{name} (pid {pid})";
        if (party.StartsWith("v1:", StringComparison.Ordinal))
        {
            var (address, process, processId) = V1Callers.Read(party);
            return process is null ? $"cliente 1.x {address}" : $"{process} (pid {processId}, 1.x)";
        }
        // A v2 consumer describes itself for the logs, with its machine and its connection; here the process is enough.
        if (party.EndsWith(')') is false && party.LastIndexOf(") c-", StringComparison.Ordinal) is var end and > 0 && party.IndexOf(" (pid ", StringComparison.Ordinal) is var start and > 0
            && party.IndexOf(" @ ", start, StringComparison.Ordinal) is var at && at > start && at < end)
            return $"{party[..start]} (pid {party[(start + 6)..at]})";
        if (party.StartsWith("panel:", StringComparison.Ordinal))
            return $"painel ({party[6..]})";
        return party == "panel" ? "painel" : party;
    }

    public sealed class TapSubscription(ChannelReader<TapEvent> events, Action leave) : IDisposable
    {
        private int _left;

        public ChannelReader<TapEvent> Events => events;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
                leave();
        }
    }
}

/// <summary>
/// A message body somebody saved under a name, to publish again without typing it.
/// </summary>
public sealed record MessageModel(string Name, JsonElement Body, int TtlMs, bool Rpc);

/// <summary>
/// The message models of each queue, kept in a file next to the executable.
/// </summary>
public sealed class MessageModelStore(ILogger<MessageModelStore> logger)
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private Dictionary<string, List<MessageModel>>? _models;

    public required string Path { get; init; }

    public IReadOnlyList<MessageModel> Of(string queue)
    {
        lock (_gate)
            return Load().TryGetValue(queue, out var models) ? [.. models.OrderBy(model => model.Name, StringComparer.CurrentCultureIgnoreCase)] : [];
    }

    /// <summary>
    /// Saves a model, replacing the one of the same name.
    /// </summary>
    public void Save(string queue, MessageModel model)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.TryGetValue(queue, out var models))
                all[queue] = models = [];
            models.RemoveAll(existing => string.Equals(existing.Name, model.Name, StringComparison.CurrentCultureIgnoreCase));
            models.Add(model);
            Write(all);
        }
    }

    public bool Remove(string queue, string name)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.TryGetValue(queue, out var models) || models.RemoveAll(existing => string.Equals(existing.Name, name, StringComparison.CurrentCultureIgnoreCase)) == 0)
                return false;
            if (models.Count == 0)
                all.Remove(queue);
            Write(all);
            return true;
        }
    }

    private Dictionary<string, List<MessageModel>> Load()
    {
        if (_models is not null)
            return _models;
        try
        {
            _models = File.Exists(Path) ? JsonSerializer.Deserialize<Dictionary<string, List<MessageModel>>>(File.ReadAllText(Path), Format) : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogError("{File} could not be read and is being ignored: {Error}", System.IO.Path.GetFileName(Path), error.Message);
        }
        return _models ??= new Dictionary<string, List<MessageModel>>(StringComparer.Ordinal);
    }

    private void Write(Dictionary<string, List<MessageModel>> all)
    {
        try
        {
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Format));
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogError("{File} could not be written; the model is kept until the service restarts: {Error}", System.IO.Path.GetFileName(Path), error.Message);
        }
    }
}
