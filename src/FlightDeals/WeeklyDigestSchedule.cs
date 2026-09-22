namespace FlightDeals;

public sealed class WeeklyDigestSettings
{
    public bool Enabled { get; init; }
    public decimal MovementThresholdFraction { get; init; } = .05m;

    public void Validate()
    {
        if (MovementThresholdFraction is <= 0 or >= 1)
            throw new InvalidOperationException("Digest movement threshold must be between zero and one.");
    }
}

public sealed record DigestPeriod(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset DueAt);

public static class WeeklyDigestSchedule
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

    public static DigestPeriod Next(DateTimeOffset now)
    {
        var monday = MondayOf(now);
        if (Utc(monday.AddHours(9)) <= now) monday = monday.AddDays(7);
        return PeriodEnding(monday);
    }

    public static DigestPeriod LatestDue(DateTimeOffset now)
    {
        var monday = MondayOf(now);
        if (Utc(monday.AddHours(9)) > now) monday = monday.AddDays(-7);
        return PeriodEnding(monday);
    }

    private static DateTime MondayOf(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone).Date;
        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        return local.AddDays(-daysSinceMonday);
    }

    private static DigestPeriod PeriodEnding(DateTime monday) => new(
        Start: Utc(monday.AddDays(-7)), End: Utc(monday), DueAt: Utc(monday.AddHours(9)));

    private static DateTimeOffset Utc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone));
}
