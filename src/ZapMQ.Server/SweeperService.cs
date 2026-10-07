using ZapMQ.Core;

namespace ZapMQ.Server;

/// <summary>
/// Runs the broker housekeeping once a second, the same cadence the 1.x cleaner threads used.
/// </summary>
public sealed class SweeperService(Broker broker, TimeProvider time, ILogger<SweeperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    broker.Sweep();
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Broker housekeeping failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
