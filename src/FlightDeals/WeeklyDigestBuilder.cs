using System.Text;
using System.Text.Json;

namespace FlightDeals;

public sealed record DigestData(Observation[] Observations, ScanRun[] Runs,
    NotificationRecord[] Notifications, BudgetState? Budget);

public static class WeeklyDigestBuilder
{
    public static NotificationEmail Build(DigestPeriod period, IEnumerable<MonitoredProfile> profiles,
        DigestData data, AccountQuota? account, decimal movementThreshold, int ceiling, int reserve,
        DateTimeOffset? generatedAt = null)
    {
        var start = TimeZoneInfo.ConvertTime(period.Start, WeeklyDigestSchedule.Zone);
        var end = TimeZoneInfo.ConvertTime(period.End, WeeklyDigestSchedule.Zone).AddDays(-1);
        var body = new StringBuilder();
        body.AppendLine($"Weekly flight summary: {start:yyyy-MM-dd} to {end:yyyy-MM-dd} (Pacific/Auckland)");
        foreach (var profile in profiles.Where(p => p.Active).OrderBy(p => p.Search.Id, StringComparer.Ordinal))
            AppendProfile(body, profile.Search, data.Observations.Where(o => InPeriod(o.ObservedAt, period)), movementThreshold);
        AppendFooter(body, period, data, account, ceiling, reserve, generatedAt ?? period.DueAt);
        return new($"Weekly flight summary — {start:yyyy-MM-dd} to {end:yyyy-MM-dd}", body.ToString());
    }

    private static void AppendProfile(StringBuilder body, SearchProfile current,
        IEnumerable<Observation> observations, decimal movementThreshold)
    {
        body.AppendLine();
        body.AppendLine($"Profile: {current.Id} ({current.Origin} → {string.Join('/', current.Destinations)})");
        var groups = observations.Where(o => o.Profile.Id == current.Id)
            .GroupBy(o => DigestSearchDefinitionKey.From(o.Profile))
            // Retain the existing section display order; serialization is not used for equality.
            .OrderBy(g => JsonSerializer.Serialize(g.First().Profile, JsonDefaults.Options), StringComparer.Ordinal).ToArray();
        if (!groups.Any(g => g.Key == DigestSearchDefinitionKey.From(current)))
        {
            AppendDefinition(body, current);
            body.AppendLine("Observation count: 0");
            body.AppendLine("No observations for the current search definition.");
            body.AppendLine("Movement: Too little comparable data to assess movement");
        }
        foreach (var group in groups)
        {
            var quotes = group.OrderBy(o => o.ObservedAt).ThenBy(o => o.Id).ToArray();
            AppendDefinition(body, quotes[0].Profile);
            body.AppendLine($"Observation count: {quotes.Length}");
            var counts = quotes.GroupBy(o => o.Assessment.Classification).OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {g.Key} observations");
            var classificationCounts = string.Join(", ", counts);
            var summary = quotes.Any(o => o.Assessment.Classification == Classification.Deal) ? "Deal observed"
                : quotes.Any(o => o.Assessment.Classification == Classification.Cheap) ? "Cheap observed" : classificationCounts;
            body.AppendLine($"Summary: {summary}");
            body.AppendLine($"Classifications: {classificationCounts}");
            AppendQuote(body, "Lowest", quotes.OrderBy(o => o.TotalPartyPrice).ThenBy(o => o.ObservedAt).ThenBy(o => o.Id).First());
            AppendQuote(body, "Latest", quotes[^1]);
            body.AppendLine($"Movement: {Movement(quotes, movementThreshold)}");
            body.AppendLine("Baselines: " + string.Join(", ", quotes.Select(o =>
                $"{o.Assessment.Source} ({o.Assessment.BaselineId ?? "none"}, version {o.Assessment.BaselineVersion})").Distinct()));
            foreach (var assumption in quotes.Select(o => o.Assessment.BaselineAssumption).Where(a => a is not null).Distinct())
                body.AppendLine(assumption);
            foreach (var limitation in quotes.SelectMany(o => o.Assessment.Limitations).Distinct()) body.AppendLine(limitation);
            body.AppendLine("Selected quotes may differ in airline, routing, baggage, and fare conditions; the lowest price is not necessarily the best itinerary.");
            body.AppendLine("Mixed-cabin premium price classifications do not verify premium placement on important long-haul segments.");
        }
    }

    private static void AppendDefinition(StringBuilder body, SearchProfile profile)
    {
        body.AppendLine($"Search definition: {profile.OutboundDate:yyyy-MM-dd} to {profile.ReturnDate:yyyy-MM-dd}; " +
            $"{profile.RequestedCabin}; {profile.Adults} adults; {profile.Currency}; {profile.Origin} → {string.Join('/', profile.Destinations)}");
    }

    private static void AppendQuote(StringBuilder body, string label, Observation quote)
    {
        body.AppendLine(FormattableString.Invariant($"{label}: {quote.Profile.Currency} {quote.TotalPartyPrice:F2} total; {quote.Profile.Currency} {quote.PricePerAdult:F2} per adult; observed {quote.ObservedAt:O}"));
        // Actual segments, not the persisted assessment's composition label, describe the selected product.
        body.AppendLine($"  Actual cabin composition: {AssessmentEvaluator.Describe(quote.Outbound.Segments.Concat(quote.Return.Segments), quote.Profile.RequestedCabin)}");
        foreach (var (labelDirection, journey) in new[] { ("Outbound", quote.Outbound), ("Return", quote.Return) })
        {
            body.AppendLine($"  {labelDirection}: " + string.Join("; ", journey.Segments.Select(s =>
                $"{s.DepartureAirport}–{s.ArrivalAirport} {s.Airline} {s.Cabin} ({s.DurationMinutes} min)")));
            foreach (var note in journey.FareNotes) body.AppendLine($"  Fare note (unverified): {note}");
        }
        body.AppendLine($"  Baggage: {quote.BaggageStatus}; Connection protection: {quote.ConnectionProtection}");
    }

    public static string Movement(Observation[] observations, decimal threshold)
    {
        if (observations.Length == 0) return "Too little comparable data to assess movement";
        var latest = observations.OrderBy(o => o.ObservedAt).ThenBy(o => o.Id).Last();
        var key = DigestComparableProductKey.From(latest);
        var comparable = observations.Where(o => DigestComparableProductKey.From(o) == key)
            .OrderBy(o => o.ObservedAt).ThenBy(o => o.Id).ToArray();
        if (comparable.Length < 3 || comparable[0].PricePerAdult <= 0 || comparable[0].ObservedAt == comparable[^1].ObservedAt)
            return "Too little comparable data to assess movement";
        var change = (comparable[^1].PricePerAdult - comparable[0].PricePerAdult) / comparable[0].PricePerAdult;
        var description = change >= threshold ? "ended higher" : change <= -threshold ? "ended lower" : "roughly unchanged";
        return FormattableString.Invariant($"{description} ({change:P1}, first to last of {comparable.Length} comparable observations); not a market trend.");
    }

    private static void AppendFooter(StringBuilder body, DigestPeriod period, DigestData data,
        AccountQuota? account, int ceiling, int reserve, DateTimeOffset now)
    {
        var runs = data.Runs.Where(r => InPeriod(r.StartedAt, period)).ToArray();
        body.AppendLine();
        body.AppendLine("Scan outcomes (runs started during this week; status at summary generation):");
        body.AppendLine($"Successful: {runs.Count(r => r.Status == RunStatus.Completed)}; Failed: {runs.Count(r => r.Status == RunStatus.Failed)}; " +
            $"No suitable itinerary: {runs.Count(r => r.Status is RunStatus.NoSuitableOutbound or RunStatus.NoSuitableReturn)}; " +
            $"Running: {runs.Count(r => r.Status == RunStatus.Running)}");
        body.AppendLine($"Reserved credits: {runs.Sum(r => r.ReservedCredits)}");
        body.AppendLine($"Potentially charged requests: {data.Runs.SelectMany(r => r.Attempts).Count(a => InPeriod(a.StartedAt, period))}");
        body.AppendLine("Reservations and recorded request attempts are not exact provider billing; interrupted or cached requests can differ.");
        var sent = data.Notifications.Count(n => n.Status == DeliveryStatus.Sent && n.SentAt is { } at && InPeriod(at, period));
        body.AppendLine($"Immediate alerts accepted by SMTP: {sent}");
        var decisions = data.Notifications.Where(n => InPeriod(n.CreatedAt, period)).GroupBy(n => n.Status).OrderBy(g => g.Key);
        body.AppendLine("Immediate alert decisions created this week (current status): " +
            string.Join(", ", decisions.Select(g => $"{g.Key}: {g.Count()}")));
        AppendQuota(body, account, data.Budget, ceiling, reserve, now);
    }

    private static void AppendQuota(StringBuilder body, AccountQuota? account, BudgetState? budget,
        int ceiling, int reserve, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var verified = account is { Status: "Active", Plan: "Free Plan", Allowance: 250, Usage: >= 0, Remaining: >= 0 }
            && account.Usage + account.Remaining == 250 && account.RenewalDate > today;
        if (!verified)
        {
            body.AppendLine("Provider usage: unavailable (no verified current free-account response).");
            body.AppendLine(budget is not null && budget.PeriodEnd > today
                ? $"Operating headroom: {Math.Max(0, Math.Min(ceiling, 250 - reserve) - budget.AccountedCredits)} credits (local accounting only; provider usage unverified)."
                : "Operating headroom: unavailable (current quota period unverified).");
            return;
        }
        var verifiedAccount = account!;
        var localUsed = budget is not null && budget.PeriodEnd == verifiedAccount.RenewalDate ? budget.AccountedCredits : 0;
        var used = Math.Max(localUsed, verifiedAccount.Usage);
        var headroom = Math.Max(0, Math.Min(ceiling, verifiedAccount.Allowance - reserve) - used);
        body.AppendLine($"Provider usage: {verifiedAccount.Usage}/{verifiedAccount.Allowance}; remaining allowance: {verifiedAccount.Remaining}; renewal: {verifiedAccount.RenewalDate:yyyy-MM-dd}");
        body.AppendLine($"Operating headroom: {headroom} credits under the {ceiling}-credit ceiling; {reserve}-credit reserve protected. Snapshot: {now:O}");
    }

    private static bool InPeriod(DateTimeOffset value, DigestPeriod period) => value >= period.Start && value < period.End;
}
