namespace FlightDeals;

public interface IScanSession : IAsyncDisposable
{
    Task<BudgetState?> ReadBudget(CancellationToken ct);
    Task<ScheduleState> ReadSchedule(CancellationToken ct);
    Task Reserve(BudgetState state, ScanRun run, CancellationToken ct, DateTimeOffset? nextDispatch = null);
    Task SaveRun(ScanRun run, CancellationToken ct);
    Task Complete(ScanRun run, Observation observation, CancellationToken ct);
}

public interface IScanStore
{
    Task<IScanSession> OpenSession(CancellationToken ct);
}

public sealed class ScanService(IFlightProvider provider, IScanStore store, AppSettings settings, TimeProvider clock)
{
    public Task<ScanRun> Run(CancellationToken ct) => Run(settings.DefaultProfile.Id, ct);

    public async Task<ScanRun> Run(string profileId, CancellationToken ct)
    {
        EnsureLiveSearch();
        var profile = settings.MonitoredProfiles.SingleOrDefault(p => p.Search.Id == profileId)
            ?? throw new ScanException("Unknown search profile.");
        EnsureFutureDate(profile.Search);
        await using var session = await store.OpenSession(ct);
        return await ReserveAndScan(profile, session, ct);
    }

    public async Task<ScanRun?> RunDue(CancellationToken ct)
    {
        if (!settings.SchedulerEnabled || !settings.LiveSearchEnabled) return null;
        await using var session = await store.OpenSession(ct);
        var schedule = await session.ReadSchedule(ct);
        var profile = SchedulePlanner.SelectDue(settings.MonitoredProfiles, schedule, clock.GetUtcNow());
        if (profile is null) return null;
        return await ReserveAndScan(profile, session, ct);
    }

    private void EnsureLiveSearch()
    {
        if (!settings.LiveSearchEnabled) throw new ScanException("Live search is disabled in configuration.");
    }

    private void EnsureFutureDate(SearchProfile profile)
    {
        if (profile.OutboundDate <= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            throw new ScanException("Configure a future outbound date before scanning.");
    }

    private async Task<ScanRun> ReserveAndScan(MonitoredProfile monitored, IScanSession session, CancellationToken ct)
    {
        var profile = monitored.Search;
        var account = await provider.GetAccount(ct);
        var now = clock.GetUtcNow();
        var state = Budget.Reserve(account, await session.ReadBudget(ct), settings, now);
        var nextDispatch = SchedulePlanner.NextDispatch(state, settings, now);
        var run = new ScanRun(Id: Guid.NewGuid(), StartedAt: now, Profile: profile,
            Status: RunStatus.Running, ReservedCredits: Budget.CreditsPerScan, Attempts: []);
        await session.Reserve(state, run, ct, nextDispatch);
        return await ExecuteScan(run, session, ct);
    }

    private async Task<ScanRun> ExecuteScan(ScanRun run, IScanSession session, CancellationToken ct)
    {
        var profile = run.Profile;
        try
        {
            run = StartAttempt(run, "Outbound");
            await session.SaveRun(run, ct); // committed BEFORE the external call
            var outbound = Select(await provider.Search(profile, null, ct), profile, true);
            run = EndAttempt(run);
            await session.SaveRun(run, ct);
            if (outbound is null) return await Finish(RunStatus.NoSuitableOutbound, "No priced outbound meets the basic rules.");

            run = StartAttempt(run, "ReturnOptions");
            await session.SaveRun(run, ct);
            var returnOptions = await provider.Search(profile, outbound.DepartureToken, ct);
            var suitableStays = returnOptions.Where(option => DestinationStay.MeetsRequirement(
                profile, outbound.Journey, option.Journey));
            var inbound = Select(suitableStays, profile, false);
            run = EndAttempt(run);
            if (inbound is null) return await Finish(RunStatus.NoSuitableReturn,
                "No priced return meets the basic rules for the selected outbound; no additional search was made.");
            var now = clock.GetUtcNow();
            var observation = CreateObservation(run, outbound, inbound, now);
            run = run with { Status = RunStatus.Completed, ObservationId = observation.Id, FinishedAt = now,
                Message = "Paired quote collected; baggage and connection protection remain unverified." };
            await session.Complete(run, observation, ct);
            return run;
        }
        catch (Exception)
        {
            // Persist failure using an independent token even if the caller disconnected.
            // Never persist raw HTTP exceptions or provider response bodies.
            run = run with { Status = RunStatus.Failed, FinishedAt = clock.GetUtcNow(),
                Message = "Scan failed; reserved credits remain accounted. Inspect attempt stages; no automatic retry." };
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await session.SaveRun(run, cleanup.Token);
            return run;
        }

        async Task<ScanRun> Finish(RunStatus status, string message)
        {
            run = run with { Status = status, Message = message, FinishedAt = clock.GetUtcNow() };
            await session.SaveRun(run, ct);
            return run;
        }
    }

    private Observation CreateObservation(ScanRun run, FlightOption outbound, FlightOption inbound,
        DateTimeOffset now)
    {
        var profile = run.Profile;
        var partyPrice = inbound.Price!.Value; // the selected pair's total, not outbound + return
        var assessment = AssessmentEvaluator.Evaluate(profile, outbound.Journey, inbound.Journey,
            partyPrice, settings.ManualBaselines, now);
        return new Observation(
            Id: Guid.NewGuid(), RunId: run.Id, ObservedAt: now, Profile: profile,
            TotalPartyPrice: partyPrice, PricePerAdult: partyPrice / profile.Adults,
            Outbound: outbound.Journey, Return: inbound.Journey, Assessment: assessment);
    }

    private ScanRun StartAttempt(ScanRun run, string stage)
    {
        var attempt = new SearchAttempt(Number: run.Attempts.Length + 1, Stage: stage,
            StartedAt: clock.GetUtcNow(), Status: "StartedPotentiallyCharged");
        return run with { Attempts = [.. run.Attempts, attempt] };
    }

    private static ScanRun EndAttempt(ScanRun run)
    {
        var completedAttempt = run.Attempts[^1] with { Status = "ResponseReceived" };
        return run with { Attempts = [.. run.Attempts[..^1], completedAttempt] };
    }

    public static FlightOption? Select(IEnumerable<FlightOption> options, SearchProfile profile, bool outbound) => options
        .Where(o => o.Price > 0 && (!outbound || !string.IsNullOrEmpty(o.DepartureToken)))
        .Where(o => Suitable(o.Journey, profile, outbound))
        .OrderBy(o => o.Price).ThenBy(o => o.Journey.DurationMinutes)
        .ThenBy(o => string.Join('|', o.Journey.Segments.Select(s => s.FlightNumber)), StringComparer.Ordinal)
        .FirstOrDefault();

    private static bool Suitable(Journey journey, SearchProfile profile, bool outbound)
    {
        var hasValidSegmentCount = journey.Segments.Length > 0 && journey.Segments.Length <= profile.MaxStops + 1;
        var hasValidDuration = journey.DurationMinutes > 0 && journey.DurationMinutes <= profile.MaxDurationMinutes;
        if (!hasValidSegmentCount || !hasValidDuration) return false;
        if (journey.Segments.Any(segment => !HasRequiredSegmentFacts(segment))) return false;
        if (profile.ExcludeKnownSeparateTickets && journey.SeparateTickets == true) return false;
        if (profile.ExcludeAirportChanges && journey.Connections.Any(connection => connection.AirportChange)) return false;

        var first = journey.Segments[0];
        var last = journey.Segments[^1];
        var expectedDate = outbound ? profile.OutboundDate : profile.ReturnDate;
        if (first.DepartureLocal is null || !first.DepartureLocal.StartsWith(expectedDate.ToString("yyyy-MM-dd"), StringComparison.Ordinal)) return false;
        return outbound ? first.DepartureAirport == profile.Origin && profile.Destinations.Contains(last.ArrivalAirport)
            : profile.Destinations.Contains(first.DepartureAirport) && last.ArrivalAirport == profile.Origin;
    }

    private static bool HasRequiredSegmentFacts(Segment segment) =>
        segment.DurationMinutes > 0 && segment.DepartureAirport.Length == 3 && segment.ArrivalAirport.Length == 3;

}
