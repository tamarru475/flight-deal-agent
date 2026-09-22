namespace FlightDeals;

public interface IWeeklyDigestSender
{
    Task<DeliveryStatus> SendDigest(DigestDelivery delivery, CancellationToken ct);
}

public sealed class WeeklyDigestProcessor(WeeklyDigestStore store, WeeklyDigestSettings settings,
    AppSettings profiles, IFlightProvider provider, IWeeklyDigestSender sender, TimeProvider clock)
{
    public async Task Process(CancellationToken ct)
    {
        if (!settings.Enabled) return;
        await using var session = await store.Open(ct);
        if (session is null) return;
        var now = clock.GetUtcNow();
        var first = await session.Initialize(now, ct);
        if (now < first.Period.DueAt) return;
        var period = WeeklyDigestSchedule.LatestDue(now);
        if (period.Start < first.Period.Start) return;
        await session.RecoverAndSkipOlder(period, ct);
        var delivery = await session.GetOrCreate(period, ct);
        if (delivery.Status != DigestStatus.Scheduled) return;
        await PrepareAndSend(session, delivery, ct);
    }

    private async Task PrepareAndSend(WeeklyDigestSession session, DigestDelivery delivery, CancellationToken ct)
    {
        try
        {
            var data = await session.ReadData(delivery.Period, ct);
            var account = await TryReadAccount(ct);
            var generated = clock.GetUtcNow();
            var email = WeeklyDigestBuilder.Build(delivery.Period, profiles.MonitoredProfiles, data, account,
                settings.MovementThresholdFraction, profiles.MonthlyCreditLimit, profiles.ReserveCredits, generated);
            delivery = delivery with { Email = email, GeneratedAt = generated };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            await session.Save(delivery with { Status = DigestStatus.Failed, Reason = "Could not prepare summary; no email attempted. No automatic retry." }, ct);
            return;
        }
        // An SMTP attempt is allowed only after its claim and frozen email are durably committed.
        delivery = delivery with { Status = DigestStatus.Sending, AttemptedAt = clock.GetUtcNow() };
        await session.Save(delivery, ct);
        DeliveryStatus outcome;
        try { outcome = await sender.SendDigest(delivery, ct); }
        catch { outcome = DeliveryStatus.Unknown; }
        var status = outcome switch
        {
            DeliveryStatus.Sent => DigestStatus.Sent,
            DeliveryStatus.Failed => DigestStatus.Failed,
            _ => DigestStatus.Unknown
        };
        await session.Save(delivery with { Status = status,
            SentAt = status == DigestStatus.Sent ? clock.GetUtcNow() : null,
            Reason = status == DigestStatus.Sent ? "Accepted by SMTP; inbox delivery not guaranteed." : "Delivery not confirmed; no automatic retry." }, ct);
    }

    private async Task<AccountQuota?> TryReadAccount(CancellationToken ct)
    {
        try { return await provider.GetAccount(ct); } // Free account endpoint only; never Search.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}

public sealed class WeeklyDigestWorker(WeeklyDigestProcessor processor, WeeklyDigestSettings settings,
    ILogger<WeeklyDigestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled) return;
        logger.LogInformation("Weekly digest enabled: Monday 09:00 Pacific/Auckland; no automatic delivery retries.");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try { await processor.Process(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch { logger.LogWarning("Weekly digest processing interrupted; inspect /digests. SMTP errors are withheld; no delivery is retried."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
