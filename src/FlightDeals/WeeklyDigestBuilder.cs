using System.Globalization;
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
        var title = $"Weekly flight watch · {DateRange(DateOnly.FromDateTime(start.Date), DateOnly.FromDateTime(end.Date))}";
        var email = new DigestEmailLayout(title);
        var now = generatedAt ?? period.DueAt;
        var verifiedAccount = VerifiedAccount(account, now);
        var observations = data.Observations.Where(o => InPeriod(o.ObservedAt, period)).ToArray();
        var activeProfiles = profiles.Where(p => p.Active).ToArray();
        var activeIds = activeProfiles.Select(p => p.Search.Id).ToHashSet();
        var displayedObservations = observations.Where(o => activeIds.Contains(o.Profile.Id)).ToArray();
        var contexts = displayedObservations.Select(o => (o.Profile.Origin, o.Profile.Adults)).Distinct().ToArray();
        var sharedContext = contexts.Length == 1;
        AppendOverview(email, period, data, observations, verifiedAccount);
        email.Heading("Routes with new fares");
        if (sharedContext)
            email.Line($"All fares are return from {Airport(contexts[0].Origin)} for {contexts[0].Adults} adults. Prices shown per adult.");
        else
            email.Line("All fares are return. Prices shown per adult.");
        var establishedIds = displayedObservations
            .Where(o => o.Assessment.Classification != Classification.InsufficientBaseline)
            .Select(o => o.Profile.Id).ToHashSet();
        var emptyRoutes = new List<string>();
        var insufficientMovement = false;
        foreach (var profile in activeProfiles.OrderByDescending(p => establishedIds.Contains(p.Search.Id))
                     .ThenBy(p => Destination(p.Search), StringComparer.Ordinal))
        {
            var groups = observations.Where(o => o.Profile.Id == profile.Search.Id)
                .GroupBy(o => DigestSearchDefinitionKey.From(o.Profile))
                .OrderBy(g => JsonSerializer.Serialize(g.First().Profile, JsonDefaults.Options), StringComparer.Ordinal).ToArray();
            if (!groups.Any(g => g.Key == DigestSearchDefinitionKey.From(profile.Search)))
                emptyRoutes.Add($"{Destination(profile.Search)} — {CabinName(profile.Search.RequestedCabin)}");
            foreach (var group in groups)
                insufficientMovement |= AppendRoute(email, group.ToArray(), movementThreshold, sharedContext);
        }
        if (emptyRoutes.Count > 0)
        {
            email.Heading("No fares recorded this week");
            email.Line(string.Join(" · ", emptyRoutes));
        }
        email.Heading("Notes");
        if (insufficientMovement) email.Line("Not enough comparable history yet to assess price movement on some routes.");
        email.Line("Baseline still learning means no usable baseline is available yet.");
        email.Line("Prices are quoted search fares. Baggage, fare conditions and connection protection may be incomplete or unverified. Display prices are rounded.");
        AppendFooter(email, period, data, verifiedAccount, ceiling, reserve, now);
        return email.Build();
    }

    private static void AppendOverview(DigestEmailLayout email, DigestPeriod period, DigestData data,
        Observation[] observations, AccountQuota? account)
    {
        var runs = data.Runs.Where(r => InPeriod(r.StartedAt, period)).ToArray();
        var interesting = observations.Count(o => o.Assessment.Classification is Classification.Cheap or Classification.Deal);
        var fares = interesting == 0 ? "No Cheap/Deal fares" : $"{interesting} Cheap/Deal observations";
        email.Line($"{runs.Count(r => r.Status == RunStatus.Completed)} successful · {runs.Count(r => r.Status == RunStatus.Failed)} failed · {fares}");
        var requests = data.Runs.SelectMany(r => r.Attempts).Count(a => InPeriod(a.StartedAt, period));
        email.Line($"{requests} requests · " + (account is null ? "Provider usage unavailable" : $"{account.Usage}/{account.Allowance} credits used"));
    }

    private static bool AppendRoute(DigestEmailLayout email, Observation[] observations, decimal movementThreshold, bool sharedContext)
    {
        var quotes = observations.OrderBy(o => o.ObservedAt).ThenBy(o => o.Id).ToArray();
        var latest = quotes[^1];
        var lowest = quotes.OrderBy(o => o.TotalPartyPrice).ThenBy(o => o.ObservedAt).ThenBy(o => o.Id).First();
        var profile = latest.Profile;
        email.Route($"{Destination(profile)} — {CabinName(profile.RequestedCabin)}");
        email.Price($"{Money(latest.PricePerAdult, profile.Currency)} pp · {ClassificationName(latest.Assessment.Classification)}");
        var counts = string.Join(", ", quotes.GroupBy(o => o.Assessment.Classification).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {ClassificationName(g.Key)}"));
        var tripContext = sharedContext ? "" : $"from {Airport(profile.Origin)} · {profile.Adults} adults · ";
        email.Line($"{DateRange(profile.OutboundDate, profile.ReturnDate)} · {tripContext}{Money(latest.TotalPartyPrice, profile.Currency)} total");
        email.Line(quotes.Length == 1 ? "1 observation" : $"{quotes.Length} observations · {counts}");
        if (quotes.Any(o => o.Assessment.Classification == Classification.Deal)) email.Line("Deal observed");
        else if (quotes.Any(o => o.Assessment.Classification == Classification.Cheap)) email.Line("Cheap observed");
        email.Line(Itinerary(latest));
        AppendCabins(email, latest);
        if (latest.PricePerAdult > 0 && lowest.Id != latest.Id &&
            (latest.PricePerAdult - lowest.PricePerAdult) / latest.PricePerAdult >= .01m)
        {
            email.Line($"Lowest this week: {Money(lowest.PricePerAdult, profile.Currency)} pp · {Money(lowest.TotalPartyPrice, profile.Currency)} total");
            var differentProduct = DigestComparableProductKey.From(lowest) != DigestComparableProductKey.From(latest);
            if (differentProduct)
            {
                email.Line("Lowest itinerary: " + Itinerary(lowest));
                AppendCabins(email, lowest);
            }
        }
        var journeys = new[] { latest.Outbound, latest.Return };
        if (journeys.Any(j => j.SeparateTickets == true)) email.Line("Separate tickets reported; connections may require self-transfer.");
        if (journeys.Any(j => j.Connections.Any(c => c.AirportChange))) email.Line("Airport change required during a connection.");
        foreach (var note in journeys.SelectMany(j => j.FareNotes).Distinct()
                     .Where(note => note.Contains("baggage for a fee", StringComparison.OrdinalIgnoreCase)))
            email.Line("Fare note (unverified): " + note);
        var movement = Movement(quotes, movementThreshold);
        var insufficient = movement == "Too little comparable data to assess movement";
        if (!insufficient) email.Line(movement);
        return insufficient;
    }

    private static string Itinerary(Observation quote)
    {
        var airlines = quote.Outbound.Segments.Concat(quote.Return.Segments).Select(s => s.Airline)
            .Where(a => !string.IsNullOrWhiteSpace(a)).Distinct();
        var outbound = Routing(quote.Outbound);
        var inbound = Routing(quote.Return);
        var routing = outbound == inbound ? outbound + " both ways" : $"outbound: {outbound}; return: {inbound}";
        return $"{string.Join(" / ", airlines)} · {routing}";
    }

    private static string Routing(Journey journey)
    {
        if (journey.Segments.Length == 0) return "routing unavailable";
        var stops = journey.Segments.Length - 1;
        if (stops == 0) return "nonstop";
        var airports = journey.Segments.SkipLast(1).Select(s => Airport(s.ArrivalAirport));
        return $"via {string.Join(" and ", airports)} · {stops} stop{(stops == 1 ? "" : "s")}";
    }

    private static void AppendCabins(DigestEmailLayout email, Observation quote)
    {
        var segments = quote.Outbound.Segments.Concat(quote.Return.Segments).ToArray();
        if (segments.Length > 0 && segments.All(s => s.Cabin == quote.Profile.RequestedCabin)) return;
        foreach (var cabin in segments.GroupBy(s => s.Cabin))
            email.Line($"{CabinName(cabin.Key)}: " + string.Join(", ", cabin.Select(s => $"{s.DepartureAirport}→{s.ArrivalAirport}")));
        if (quote.Profile.RequestedCabin is Cabin.PremiumEconomy or Cabin.Business or Cabin.First)
            email.Line("Price classification only; premium-segment quality is not yet assessed.");
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

    private static void AppendFooter(DigestEmailLayout email, DigestPeriod period, DigestData data,
        AccountQuota? account, int ceiling, int reserve, DateTimeOffset now)
    {
        var runs = data.Runs.Where(r => InPeriod(r.StartedAt, period)).ToArray();
        var requests = data.Runs.SelectMany(r => r.Attempts).Count(a => InPeriod(a.StartedAt, period));
        email.Line($"{runs.Sum(r => r.ReservedCredits)} credits reserved for {requests} recorded requests this week; actual billing can differ.");
        var noItinerary = runs.Count(r => r.Status is RunStatus.NoSuitableOutbound or RunStatus.NoSuitableReturn);
        var running = runs.Count(r => r.Status == RunStatus.Running);
        if (noItinerary > 0) email.Line($"{noItinerary} scans found no suitable itinerary.");
        if (running > 0) email.Line($"{running} scans still running at summary generation.");
        var sent = data.Notifications.Count(n => n.Status == DeliveryStatus.Sent && n.SentAt is { } at && InPeriod(at, period));
        email.Line(sent == 0 ? "No deal alerts sent this week." : $"{sent} deal alert{(sent == 1 ? "" : "s")} accepted for delivery this week.");
        var problems = data.Notifications.Where(n => InPeriod(n.CreatedAt, period))
            .Count(n => n.Status is DeliveryStatus.Failed or DeliveryStatus.Unknown);
        if (problems > 0) email.Line($"{problems} alert deliveries failed or could not be confirmed; no automatic retry.");
        var budget = data.Budget;
        if (account is null)
        {
            email.Line(budget is not null && budget.PeriodEnd > DateOnly.FromDateTime(now.UtcDateTime)
                ? $"Operating headroom: {Math.Max(0, Math.Min(ceiling, 250 - reserve) - budget.AccountedCredits)} credits (local accounting only; provider usage unverified)."
                : "Operating headroom unavailable; current quota period unverified.");
            return;
        }
        var localUsed = budget is not null && budget.PeriodEnd == account.RenewalDate ? budget.AccountedCredits : 0;
        var used = Math.Max(localUsed, account.Usage);
        var headroom = Math.Max(0, Math.Min(ceiling, account.Allowance - reserve) - used);
        email.Line($"{account.Remaining} provider credits remaining · Operating headroom: {headroom} credits under the {ceiling}-credit ceiling; {reserve}-credit reserve protected. Renews {account.RenewalDate?.ToString("d MMM", CultureInfo.InvariantCulture)}.");
    }

    private static AccountQuota? VerifiedAccount(AccountQuota? account, DateTimeOffset now) =>
        account is { Status: "Active", Plan: "Free Plan", Allowance: 250, Usage: >= 0, Remaining: >= 0 }
        && account.Usage + account.Remaining == 250 && account.RenewalDate > DateOnly.FromDateTime(now.UtcDateTime)
            ? account : null;

    private static string Money(decimal value, string currency) =>
        (currency == "NZD" ? "NZ$" : currency + " ") + decimal.Round(value, 0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture);

    private static string DateRange(DateOnly start, DateOnly end) => start.Year == end.Year && start.Month == end.Month
        ? $"{start.Day}–{end.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
        : $"{start.ToString(start.Year == end.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture)}–{end.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

    private static string ClassificationName(Classification classification) => classification == Classification.InsufficientBaseline
        ? "Baseline still learning" : classification.ToString();
    private static string CabinName(Cabin cabin) => cabin switch
    {
        Cabin.PremiumEconomy => "Premium Economy", Cabin.Unknown => "Cabin unconfirmed", _ => cabin.ToString()
    };
    private static string Destination(SearchProfile profile) => string.Join(" / ", profile.Destinations.Select(Airport).Distinct());
    private static string Airport(string code) => code switch
    {
        "AKL" => "Auckland", "CDG" => "Paris", "MAD" => "Madrid", "BCN" => "Barcelona", "FCO" => "Rome",
        "NRT" or "HND" => "Tokyo", "BNE" => "Brisbane", "SYD" => "Sydney", "NAN" => "Fiji", "RAR" => "Rarotonga",
        "PER" => "Perth", "CNS" => "Cairns", "PPT" => "Tahiti", "BKK" => "Bangkok", "SGN" => "Ho Chi Minh City",
        "MNL" => "Manila", "SCL" => "Santiago", "LIM" => "Lima", "GIG" => "Rio", "CPT" => "Cape Town",
        "KUL" => "Kuala Lumpur", "SIN" => "Singapore", "DXB" => "Dubai", "HKG" => "Hong Kong", "JNB" => "Johannesburg",
        _ => code
    };
    private static bool InPeriod(DateTimeOffset value, DigestPeriod period) => value >= period.Start && value < period.End;
}
