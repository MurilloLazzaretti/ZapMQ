using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ZapMQ.Core;

namespace ZapMQ.Server.Panel;

/// <summary>
/// One line of trace as the panel shows it. <see cref="Dropped"/> above zero stands for that
/// many lines the process threw away before this point, and carries no text.
/// </summary>
public sealed record TraceLine(long N, DateTimeOffset At, string Text, long Dropped = 0);

/// <summary>
/// Where a trace is: <c>starting</c>, <c>on</c>, <c>unsupported</c> (the process does not
/// know this way of tracing), <c>unreachable</c> (nobody is listening for the request) or
/// <c>ended</c> (the process left).
/// </summary>
public sealed record TraceState(string State, string? Message = null);

/// <summary>
/// What a watcher receives: a change of state, or lines.
/// </summary>
public sealed record TraceEvent(TraceState? State, IReadOnlyList<TraceLine>? Lines);

/// <summary>
/// The trace of the supervised processes, through the broker. A process only sends its trace
/// while somebody is watching: the first watcher makes the hub ask for it, the request is
/// renewed while there is one, and the last to leave turns it off. Trace is disposable: what
/// a watcher cannot keep up with is dropped, and nothing of it becomes a dead letter.
///
/// A process that only knows the trace of 1.x (a socket on its own machine) is asked for
/// through the Worker Control, which opens that socket and publishes what arrives.
/// </summary>
public sealed class TraceHub(Broker broker, WorkerControlClient workerControl, TimeProvider time, ILogger<TraceHub> logger) : IDisposable
{
    /// <summary>
    /// For how long a process keeps tracing after the last request it got.
    /// </summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the request is repeated while somebody watches. Set by the tests.
    /// </summary>
    public TimeSpan RenewEvery { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the process has to answer the first request. Set by the tests.
    /// </summary>
    public TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Lines kept per process, shown to whoever starts watching.
    /// </summary>
    public const int History = 2000;

    /// <summary>
    /// How long the lines of a process are kept after its last watcher leaves.
    /// </summary>
    public static readonly TimeSpan Memory = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private ITimer? _forgetting;

    private Broker Broker => broker;

    private WorkerControlClient WorkerControl => workerControl;

    private TimeProvider Time => time;

    private ILogger Logger => logger;

    public static string RequestQueue(int processId) => processId + "TR";

    /// <summary>
    /// What the name of every queue of trace lines starts with. The broker treats them as
    /// disposable: no dead letters, and a short wait.
    /// </summary>
    public const string LinesPrefix = "zapmq.trace.";

    public static string LinesQueue(int processId) => LinesPrefix + processId;

    /// <summary>
    /// Starts watching a process. What is already known comes first: the state, and the lines
    /// kept from a while ago. Dispose the result to stop watching.
    /// </summary>
    public TraceSubscription Subscribe(int processId)
    {
        // From the first watcher on, what nobody watches any more is let go now and then.
        if (_forgetting is null)
        {
            var timer = time.CreateTimer(_ => Forget(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            if (Interlocked.CompareExchange(ref _forgetting, timer, null) is not null)
                timer.Dispose();
        }
        while (true)
        {
            var session = _sessions.GetOrAdd(processId, id => new Session(this, id));
            if (session.TryAdd(out var subscription))
                return subscription;
            // It was being let go at this very moment; the next one is new.
            _sessions.TryRemove(new KeyValuePair<int, Session>(processId, session));
        }
    }

    /// <summary>
    /// Lets go of what nobody has watched for a while.
    /// </summary>
    private void Forget()
    {
        var now = time.GetUtcNow();
        foreach (var (processId, session) in _sessions)
        {
            if (session.TryRetire(now))
                _sessions.TryRemove(new KeyValuePair<int, Session>(processId, session));
        }
    }

    public void Dispose()
    {
        _forgetting?.Dispose();
        foreach (var session in _sessions.Values)
            session.Shut();
        _sessions.Clear();
    }

    public sealed class TraceSubscription(ChannelReader<TraceEvent> events, Action leave) : IDisposable
    {
        private int _left;

        public ChannelReader<TraceEvent> Events => events;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
                leave();
        }
    }

    private sealed class Session(TraceHub hub, int processId)
    {
        private readonly object _gate = new();
        private readonly List<Channel<TraceEvent>> _watchers = [];
        private readonly Queue<TraceLine> _history = new();
        private readonly string _requests = RequestQueue(processId);
        private readonly string _lines = LinesQueue(processId);

        private TraceState _state = new("starting");
        private CancellationTokenSource? _running;
        private Sink? _sink;
        private DateTimeOffset _idleSince;
        private bool _retired;
        private bool _relayed;
        private long _serial;

        public bool TryAdd(out TraceSubscription subscription)
        {
            // A watcher that falls behind loses the oldest of what it has not read yet.
            var channel = Channel.CreateBounded<TraceEvent>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            lock (_gate)
            {
                if (_retired)
                {
                    subscription = null!;
                    return false;
                }

                var first = _watchers.Count == 0;
                if (first)
                    _state = new TraceState("starting");
                channel.Writer.TryWrite(new TraceEvent(_state, [.. _history]));
                _watchers.Add(channel);
                if (first)
                    Start();
            }

            subscription = new TraceSubscription(channel.Reader, () => Leave(channel));
            return true;
        }

        public bool TryRetire(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_watchers.Count > 0 || now - _idleSince < Memory)
                    return false;
                _retired = true;
            }
            return true;
        }

        public void Shut()
        {
            lock (_gate)
            {
                foreach (var watcher in _watchers)
                    watcher.Writer.TryComplete();
                _watchers.Clear();
                Stop();
                _retired = true;
            }
        }

        private void Leave(Channel<TraceEvent> channel)
        {
            lock (_gate)
            {
                channel.Writer.TryComplete();
                if (_watchers.Remove(channel) && _watchers.Count == 0)
                    Stop();
            }
        }

        // Under the gate.
        private void Start()
        {
            var incoming = Channel.CreateBounded<BrokerMessage>(new BoundedChannelOptions(64) { SingleReader = true });
            var sink = new Sink(processId, incoming.Writer);
            var running = new CancellationTokenSource();
            _sink = sink;
            _running = running;
            hub.Broker.Bind(sink, _lines);
            _ = Task.Run(() => PumpAsync(sink, incoming.Reader, running.Token));
            _ = Task.Run(() => AskAsync(running.Token));
        }

        // Under the gate.
        private void Stop()
        {
            _running?.Cancel();
            _running = null;
            if (_sink is { } sink)
            {
                hub.Broker.Disconnect(sink);
                _sink = null;
            }
            if (_relayed)
                _ = hub.WorkerControl.AskAsync("StopTrace", null, request => request["ProcessId"] = processId);
            else if (_state.State == "on")
                Request("stop zapmq trace", rpc: false);
            _relayed = false;
            _idleSince = hub.Time.GetUtcNow();
        }

        /// <summary>
        /// Asks the process to trace and keeps asking while somebody watches.
        /// </summary>
        private async Task AskAsync(CancellationToken stop)
        {
            try
            {
                var direct = await AskProcessAsync(stop);
                if (direct is null)
                {
                    Change(new TraceState("on"), stop);
                    while (true)
                    {
                        await Task.Delay(hub.RenewEvery, hub.Time, stop);
                        if (!Listening())
                        {
                            Change(new TraceState("ended", "O processo saiu."), stop);
                            return;
                        }
                        Request("start zapmq trace", rpc: false);
                    }
                }

                // The way of 1.x, which only somebody on the machine of the process can do.
                var relay = await AskWorkerControlAsync(stop);
                if (!relay.Ok)
                {
                    Change(direct with { Message = direct.Message + " " + Why(relay) }, stop);
                    return;
                }

                lock (_gate)
                {
                    if (stop.IsCancellationRequested)
                    {
                        _ = hub.WorkerControl.AskAsync("StopTrace", null, request => request["ProcessId"] = processId);
                        return;
                    }
                    _relayed = true;
                }
                Change(new TraceState("on", "Este processo usa o trace antigo, por socket; o Worker Control está repassando o que ele escreve."), stop);
                while (true)
                {
                    await Task.Delay(hub.RenewEvery, hub.Time, stop);
                    relay = await AskWorkerControlAsync(stop);
                    if (!relay.Ok)
                    {
                        Change(new TraceState("ended", relay.ErrorCode == "not-found" ? "O processo saiu." : "O Worker Control parou de repassar o trace. " + Why(relay)), stop);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Nobody is watching any more.
            }
            catch (Exception error)
            {
                hub.Logger.LogError(error, "Trace of process {ProcessId} stopped by an error", processId);
            }
        }

        /// <summary>
        /// Asks the process itself. Null when it took the request; otherwise, what to tell
        /// the watchers if nobody else can get its trace.
        /// </summary>
        private async Task<TraceState?> AskProcessAsync(CancellationToken stop)
        {
            if (!Listening())
                return new TraceState("unreachable", "O processo não está conectado ao ZapMQ pelo protocolo v2.");

            var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Request("start zapmq trace", rpc: true, new Receiver(answer));
            try
            {
                return Accepted(await answer.Task.WaitAsync(hub.Patience, hub.Time, stop))
                    ? null
                    : new TraceState("unsupported", "Este processo usa uma versão do wrapper que não envia o trace pelo ZapMQ.");
            }
            catch (TimeoutException)
            {
                return new TraceState("unreachable", "O processo não respondeu ao pedido de trace.");
            }
        }

        private Task<WorkerControlAnswer> AskWorkerControlAsync(CancellationToken stop) =>
            hub.WorkerControl.AskAsync("StartTrace", null, request =>
            {
                request["ProcessId"] = processId;
                request["Queue"] = _lines;
                request["LeaseSeconds"] = (int)Lease.TotalSeconds;
            }, stop);

        private static string Why(WorkerControlAnswer relay) => relay switch
        {
            { Reached: false } => "O Worker Control, que poderia repassá-lo, não está respondendo.",
            { ErrorCode: "unknown-command" } => "O Worker Control instalado é anterior ao repasse de trace.",
            { ErrorCode: "not-found" } => "Ele também não é um processo mantido pelo Worker Control.",
            _ => "O Worker Control não conseguiu repassá-lo: " + relay.ErrorMessage
        };

        private bool Listening() => hub.Broker.GetQueue(_requests) is { Consumers: > 0 };

        private static bool Accepted(string? response)
        {
            try
            {
                return response is not null && JsonNode.Parse(response)?["message"]?.GetValue<string>() == "on";
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        private void Request(string message, bool rpc, Consumer? replyTo = null)
        {
            var body = new JsonObject { ["message"] = message, ["port"] = 0, ["queue"] = _lines, ["lease"] = (int)Lease.TotalSeconds };
            hub.Broker.Publish(_requests, body.ToJsonString(), rpc, ttl: hub.Patience, replyTo: replyTo, publisher: "panel");
        }

        /// <summary>
        /// Takes what the process publishes, one batch at a time, and hands it to the watchers.
        /// </summary>
        private async Task PumpAsync(Sink sink, ChannelReader<BrokerMessage> incoming, CancellationToken stop)
        {
            try
            {
                await foreach (var message in incoming.ReadAllAsync(stop))
                {
                    try
                    {
                        Spread(Read(message.Body), stop);
                    }
                    catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
                    {
                        hub.Logger.LogDebug(error, "Trace batch of process {ProcessId} could not be read", processId);
                    }
                    hub.Broker.Confirm(sink, _lines, message.Id);
                }
            }
            catch (OperationCanceledException)
            {
                // Nobody is watching any more.
            }
        }

        private List<TraceLine> Read(string body)
        {
            var lines = new List<TraceLine>();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("Dropped", out var dropped) && dropped.GetInt64() > 0)
                lines.Add(new TraceLine(0, hub.Time.GetUtcNow(), "", dropped.GetInt64()));
            if (root.TryGetProperty("Lines", out var given) && given.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in given.EnumerateArray())
                {
                    var at = line.TryGetProperty("At", out var moment) && moment.TryGetDateTimeOffset(out var parsed) ? parsed : hub.Time.GetUtcNow();
                    lines.Add(new TraceLine(0, at, line.TryGetProperty("Text", out var text) ? text.GetString() ?? "" : ""));
                }
            }
            return lines;
        }

        private void Spread(List<TraceLine> lines, CancellationToken stop)
        {
            if (lines.Count == 0)
                return;
            lock (_gate)
            {
                if (stop.IsCancellationRequested)
                    return;
                for (var i = 0; i < lines.Count; i++)
                {
                    lines[i] = lines[i] with { N = ++_serial };
                    _history.Enqueue(lines[i]);
                }
                while (_history.Count > History)
                    _history.Dequeue();
                var batch = new TraceEvent(null, lines);
                foreach (var watcher in _watchers)
                    watcher.Writer.TryWrite(batch);
            }
        }

        private void Change(TraceState state, CancellationToken stop)
        {
            lock (_gate)
            {
                if (stop.IsCancellationRequested)
                    return;
                _state = state;
                var change = new TraceEvent(state, null);
                foreach (var watcher in _watchers)
                    watcher.Writer.TryWrite(change);
            }
        }
    }

    /// <summary>
    /// Where the broker pushes the lines of one process.
    /// </summary>
    private sealed class Sink(int processId, ChannelWriter<BrokerMessage> incoming) : Consumer
    {
        public override string Description => $"ZapMQ panel (trace of {processId})";

        protected override void Deliver(BrokerMessage message) => incoming.TryWrite(message);

        protected override void DeliverResponse(BrokerMessage message)
        {
        }
    }

    /// <summary>
    /// Where the broker pushes the answer of the first request.
    /// </summary>
    private sealed class Receiver(TaskCompletionSource<string?> answer) : Consumer
    {
        public override string Description => "ZapMQ panel";

        protected override void Deliver(BrokerMessage message)
        {
        }

        protected override void DeliverResponse(BrokerMessage message) => answer.TrySetResult(message.Response);
    }
}
