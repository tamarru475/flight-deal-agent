namespace FlightDeals;

public sealed class NotificationProcessor(NotificationStore store, NotificationSettings settings,
    AppSettings profiles, INotificationSender sender, TimeProvider clock)
{
    public async Task Process(CancellationToken ct)
    {
        if (!settings.Enabled) return;
        await using var session = await store.Open(ct);
        if (session is null) return;
        await session.RecoverInterrupted(ct);
        foreach (var profile in profiles.MonitoredProfiles.Where(p => p.Active))
            await ProcessProfile(session, profile.Search, ct);
    }

    private async Task ProcessProfile(NotificationSession session, SearchProfile profile, CancellationToken ct)
    {
        var scope = NotificationPolicy.Scope(profile);
        var state = await session.GetOrInitialize(scope, ct);
        var records = await session.Records(scope, ct);
        var unresolved = records.Any(r => r.Status is DeliveryStatus.Failed or DeliveryStatus.Unknown);
        // Pending has never been attempted; a persisted Sending is recovered as Unknown, never retried.
        foreach (var pending in records.Where(r => r.Status == DeliveryStatus.Pending))
        {
            if (unresolved) break;
            var observation = await session.Observation(pending.ObservationId, ct);
            (state, unresolved) = await Deliver(session, pending, observation, state, ct);
        }
        foreach (var observation in await session.Unprocessed(profile, state, ct))
        {
            var decision = NotificationPolicy.Evaluate(observation, state, settings.MaterialImprovementFraction, unresolved);
            state = state with { LastProcessedObservationId = observation.Id, LastClassification = observation.Assessment.Classification };
            var record = new NotificationRecord(ObservationId: observation.Id, Scope: scope,
                Status: decision.Send ? DeliveryStatus.Pending : DeliveryStatus.Suppressed,
                Reason: decision.Reason, MessageId: $"<{observation.Id:N}@flight-deal-agent.local>", CreatedAt: clock.GetUtcNow());
            await session.Save(record, state, ct);
            if (decision.Send) (state, unresolved) = await Deliver(session, record, observation, state, ct);
        }
    }

    private async Task<(NotificationState State, bool Unresolved)> Deliver(NotificationSession session,
        NotificationRecord record, Observation observation, NotificationState state, CancellationToken ct)
    {
        NotificationEmail email;
        try { email = NotificationEmailBuilder.Build(observation); }
        catch
        {
            await session.Save(record with { Status = DeliveryStatus.Failed, Reason = "Could not prepare email; no delivery attempted." }, state, ct);
            return (state, true);
        }
        record = record with { Status = DeliveryStatus.Sending, AttemptedAt = clock.GetUtcNow() };
        await session.Save(record, state, ct); // Must commit before contacting SMTP.
        DeliveryStatus outcome;
        try { outcome = await sender.Send(record, email, ct); }
        catch { outcome = DeliveryStatus.Unknown; }
        if (outcome is not (DeliveryStatus.Sent or DeliveryStatus.Failed)) outcome = DeliveryStatus.Unknown;
        var sentAt = outcome == DeliveryStatus.Sent ? clock.GetUtcNow() : (DateTimeOffset?)null;
        if (sentAt is not null)
            state = state with { LastNotifiedObservationId = observation.Id,
                LastNotifiedClassification = observation.Assessment.Classification,
                LastNotifiedPricePerAdult = observation.PricePerAdult, LastNotifiedAt = sentAt };
        record = record with { Status = outcome, SentAt = sentAt,
            Reason = outcome == DeliveryStatus.Sent ? record.Reason : "Delivery not confirmed; no retry. Operator review required." };
        // On DB failure, Sending remains durable and will recover as Unknown.
        await session.Save(record, state, ct);
        return (state, outcome != DeliveryStatus.Sent);
    }
}

public sealed class NotificationWorker(NotificationProcessor processor, NotificationSettings settings,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled) return;
        logger.LogInformation("Notification worker enabled. Delivery has no automatic retries.");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await processor.Process(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Notification processing interrupted; inspect /notifications. No delivery is retried."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
