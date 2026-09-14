namespace FlightDeals;

public sealed record MonitoredProfile
{
    // Configuration binding appends array values to existing defaults. Start explicit profiles empty.
    public SearchProfile Search { get; init; } = new() { Destinations = [] };
    public bool Active { get; init; } = true;
    public double TargetIntervalHours { get; init; } = 56;
    public int Priority { get; init; } = 100;
}

public sealed record ScheduleState(Dictionary<string, DateTimeOffset> LastScans, DateTimeOffset NextDispatch);

public static class SchedulePlanner
{
    public static MonitoredProfile? SelectDue(IEnumerable<MonitoredProfile> profiles,
        ScheduleState state, DateTimeOffset now)
    {
        if (now < state.NextDispatch) return null;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return profiles
            .Where(profile => profile.Active && profile.Search.OutboundDate > today)
            .Select(profile => new { Profile = profile, Due = DueAt(profile, state) })
            .Where(candidate => candidate.Due <= now)
            .OrderByDescending(candidate => candidate.Profile.Priority)
            .ThenBy(candidate => candidate.Due)
            .ThenBy(candidate => candidate.Profile.Search.Id, StringComparer.Ordinal)
            .Select(candidate => candidate.Profile)
            .FirstOrDefault();
    }

    private static DateTimeOffset DueAt(MonitoredProfile profile, ScheduleState state) =>
        state.LastScans.TryGetValue(profile.Search.Id, out var lastScan)
            ? lastScan.AddHours(profile.TargetIntervalHours)
            : DateTimeOffset.MinValue;

    public static DateTimeOffset NextDispatch(BudgetState reservation, AppSettings settings, DateTimeOffset now)
    {
        var ceiling = Math.Min(settings.MonthlyCreditLimit, 250 - settings.ReserveCredits);
        // Include this dispatch in the slots spread over the rest of the verified provider period.
        var slots = (ceiling - reservation.AccountedCredits) / Budget.CreditsPerScan + 1;
        var renewal = new DateTimeOffset(reservation.PeriodEnd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var spacing = (renewal - now) / slots;
        return now + (spacing < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : spacing);
    }
}
