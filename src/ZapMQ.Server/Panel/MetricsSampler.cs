using ZapMQ.Core;
using ZapMQ.Server.V2;

namespace ZapMQ.Server.Panel;

/// <summary>
/// One reading of the broker. The rates are per second, over the time since the previous one.
/// </summary>
public sealed record MetricsPoint(
    DateTimeOffset At,
    double PublishedPerSecond,
    double DeliveredPerSecond,
    double ConfirmedPerSecond,
    double DeadPerSecond,
    int Pending,
    int Processing,
    int Queues,
    int Connections);

/// <summary>
/// Keeps the recent history of the broker for the charts of the panel: a reading every five
/// seconds for the last hour and one per minute for the last day. In memory; a restart of the
/// service starts it over.
/// </summary>
public sealed class MetricsSampler(Broker broker, V2Connections connections, TimeProvider time, ILogger<MetricsSampler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private const int RecentCapacity = 720;
    private const int DayCapacity = 1440;

    private readonly object _gate = new();
    private readonly Queue<MetricsPoint> _recent = new();
    private readonly Queue<MetricsPoint> _day = new();
    private readonly List<MetricsPoint> _minute = [];
    private BrokerTotals _previous = new(0, 0, 0, 0);
    private DateTimeOffset _previousAt;

    public MetricsPoint? Latest
    {
        get
        {
            lock (_gate)
                return _recent.Count > 0 ? _recent.Last() : null;
        }
    }

    public IReadOnlyList<MetricsPoint> Recent()
    {
        lock (_gate)
            return [.. _recent];
    }

    public IReadOnlyList<MetricsPoint> Day()
    {
        lock (_gate)
            return [.. _day];
    }

    /// <summary>
    /// Takes one reading now. Public so that a test does not have to wait for the clock.
    /// </summary>
    public void Sample()
    {
        var now = time.GetUtcNow();
        var totals = broker.GetTotals();
        var queues = broker.GetQueues();

        lock (_gate)
        {
            var seconds = _previousAt == default ? 0 : (now - _previousAt).TotalSeconds;
            double Rate(long current, long before) => seconds <= 0 ? 0 : Math.Round(Math.Max(0, current - before) / seconds, 2);

            var point = new MetricsPoint(
                now,
                Rate(totals.Published, _previous.Published),
                Rate(totals.Delivered, _previous.Delivered),
                Rate(totals.Confirmed, _previous.Confirmed),
                Rate(totals.DeadLettered, _previous.DeadLettered),
                queues.Sum(queue => queue.Pending),
                queues.Sum(queue => queue.Processing),
                queues.Count,
                connections.Count);

            _previous = totals;
            _previousAt = now;

            _recent.Enqueue(point);
            while (_recent.Count > RecentCapacity)
                _recent.Dequeue();

            // One point per minute: the average of the rates and the peak of what was waiting.
            _minute.Add(point);
            if (_minute.Count >= 12)
            {
                _day.Enqueue(new MetricsPoint(
                    now,
                    Math.Round(_minute.Average(p => p.PublishedPerSecond), 2),
                    Math.Round(_minute.Average(p => p.DeliveredPerSecond), 2),
                    Math.Round(_minute.Average(p => p.ConfirmedPerSecond), 2),
                    Math.Round(_minute.Average(p => p.DeadPerSecond), 2),
                    _minute.Max(p => p.Pending),
                    _minute.Max(p => p.Processing),
                    point.Queues,
                    point.Connections));
                _minute.Clear();
                while (_day.Count > DayCapacity)
                    _day.Dequeue();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    Sample();
                }
                catch (Exception error)
                {
                    logger.LogError(error, "A reading of the broker for the panel failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
