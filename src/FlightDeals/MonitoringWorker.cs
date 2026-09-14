namespace FlightDeals;

public sealed class MonitoringWorker(ScanService scans, AppSettings settings,
    TimeProvider clock, ILogger<MonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.SchedulerEnabled || !settings.LiveSearchEnabled) return;

        // Wait before the first check; restart never causes a catch-up loop.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var run = await scans.RunDue(stoppingToken);
                    if (run is not null)
                        logger.LogInformation("Scheduled scan {RunId} finished with {Status}.", run.Id, run.Status);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (ScanException)
                {
                    // Quota/account checks are free. Rechecking eligibility never retries a flight request.
                    logger.LogInformation("Scheduled scan deferred by account, quota, or concurrency checks.");
                }
                catch (Exception)
                {
                    // Never log raw provider/connection exceptions, which may contain secrets.
                    logger.LogWarning("Monitoring check failed; no immediate retry was made.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
