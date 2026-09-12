using System.Globalization;
using System.Text.Json;

namespace FlightDeals;

public sealed record AccountQuota(string Status, string Plan, int Allowance, int Usage,
    int Remaining, DateOnly? RenewalDate);

public interface IFlightProvider
{
    Task<AccountQuota> GetAccount(CancellationToken ct);
    Task<FlightOption[]> Search(SearchProfile profile, string? departureToken, CancellationToken ct);
}

public sealed class SerpApiProvider(HttpClient client, string key) : IFlightProvider
{
    public async Task<AccountQuota> GetAccount(CancellationToken ct)
    {
        using var doc = await Get("account.json", [], ct);
        var account = doc.RootElement;
        var hasRenewalDate = DateOnly.TryParseExact(Text(account, "plan_renewal_date"), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var renewalDate);
        return new AccountQuota(
            Status: Text(account, "account_status") ?? "Unknown",
            Plan: Text(account, "plan_name") ?? "Unknown",
            Allowance: Number(account, "searches_per_month") ?? -1,
            Usage: Number(account, "this_month_usage") ?? -1,
            Remaining: Number(account, "plan_searches_left") ?? -1,
            RenewalDate: hasRenewalDate ? renewalDate : null);
    }

    public async Task<FlightOption[]> Search(SearchProfile profile, string? departureToken, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["engine"] = "google_flights", ["departure_id"] = profile.Origin,
            ["arrival_id"] = string.Join(',', profile.Destinations),
            ["outbound_date"] = profile.OutboundDate.ToString("yyyy-MM-dd"),
            ["return_date"] = profile.ReturnDate.ToString("yyyy-MM-dd"),
            ["type"] = "1", ["adults"] = profile.Adults.ToString(CultureInfo.InvariantCulture),
            ["currency"] = profile.Currency, ["hl"] = "en", ["gl"] = "nz",
            ["travel_class"] = ((int)profile.RequestedCabin).ToString(CultureInfo.InvariantCulture),
            ["stops"] = (profile.MaxStops + 1).ToString(CultureInfo.InvariantCulture),
            ["max_duration"] = profile.MaxDurationMinutes.ToString(CultureInfo.InvariantCulture),
            ["deep_search"] = "true", ["sort_by"] = "2"
        };
        if (departureToken is not null) query["departure_token"] = departureToken;
        using var doc = await Get("search.json", query, ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("search_metadata", out var meta) || Text(meta, "status") is not ("Success" or "Cached"))
            throw new ScanException("Provider did not return a completed search; no retry was made.");
        // Confirm the response belongs to our passenger/currency/cabin context before accepting prices.
        if (!root.TryGetProperty("search_parameters", out var echoed)
            || Number(echoed, "adults") != profile.Adults || Text(echoed, "currency") != profile.Currency
            || Number(echoed, "travel_class") != (int)profile.RequestedCabin)
            throw new ScanException("Provider search context is missing or mismatched.");
        return ParseOptions(root);
    }

    private async Task<JsonDocument> Get(string endpoint, Dictionary<string, string> query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ScanException("SERPAPI_API_KEY is not configured.");
        query["api_key"] = key;
        var uri = "https://serpapi.com/" + endpoint + "?" + string.Join('&',
            query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        try
        {
            using var response = await client.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode) throw new ScanException("SerpApi returned an unsuccessful HTTP status; no retry was made.");
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("error", out _))
            {
                doc.Dispose();
                throw new ScanException("SerpApi reported an error; no retry was made.");
            }
            return doc;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            // Do not propagate URI-bearing exceptions or raw response bodies (which can contain the key).
            throw new ScanException("Provider request failed or timed out; any search remains potentially charged.");
        }
    }

    public static FlightOption[] ParseOptions(JsonElement root)
    {
        var result = new List<FlightOption>();
        foreach (var name in new[] { "best_flights", "other_flights" })
        {
            if (!root.TryGetProperty(name, out var options) || options.ValueKind != JsonValueKind.Array) continue;
            foreach (var option in options.EnumerateArray())
            {
                if (Text(option, "type") != "Round trip" || !option.TryGetProperty("flights", out var flights)
                    || flights.ValueKind != JsonValueKind.Array) continue;
                var segments = flights.EnumerateArray().Select(ParseSegment).ToArray();
                var connections = ParseConnections(option, segments);
                var notes = option.TryGetProperty("extensions", out var ex) && ex.ValueKind == JsonValueKind.Array
                    ? ex.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
                var price = option.TryGetProperty("price", out var priceElement) && priceElement.ValueKind == JsonValueKind.Number
                    && priceElement.TryGetDecimal(out var n) ? (decimal?)n : null;
                var journey = new Journey(
                    DurationMinutes: Number(option, "total_duration") ?? 0,
                    Segments: segments,
                    Connections: connections,
                    SeparateTickets: Boolean(option, "separate_tickets"),
                    FareNotes: notes);
                result.Add(new FlightOption(Price: price, Journey: journey,
                    DepartureToken: Text(option, "departure_token")));
            }
        }
        return result.ToArray();
    }

    private static Segment ParseSegment(JsonElement flight)
    {
        var departure = flight.TryGetProperty("departure_airport", out var from) ? from : default;
        var arrival = flight.TryGetProperty("arrival_airport", out var to) ? to : default;
        var reportedCabin = Text(flight, "travel_class");
        return new Segment(
            DepartureAirport: Text(departure, "id") ?? "",
            ArrivalAirport: Text(arrival, "id") ?? "",
            DepartureLocal: Text(departure, "time"),
            ArrivalLocal: Text(arrival, "time"),
            DurationMinutes: Number(flight, "duration") ?? 0,
            Airline: Text(flight, "airline"),
            FlightNumber: Text(flight, "flight_number"),
            Cabin: ParseCabin(reportedCabin),
            ReportedCabin: reportedCabin,
            Overnight: Boolean(flight, "overnight"));
    }

    private static Cabin ParseCabin(string? reportedCabin) => reportedCabin switch
    {
        "Economy" => Cabin.Economy,
        "Premium Economy" => Cabin.PremiumEconomy,
        "Business Class" => Cabin.Business,
        "First Class" => Cabin.First,
        _ => Cabin.Unknown
    };

    private static Connection[] ParseConnections(JsonElement option, Segment[] segments)
    {
        var layovers = option.TryGetProperty("layovers", out var reported) && reported.ValueKind == JsonValueKind.Array
            ? reported.EnumerateArray().ToArray() : [];
        var connections = new List<Connection>();
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var arrivalAirport = segments[index].ArrivalAirport;
            var departureAirport = segments[index + 1].DepartureAirport;
            var layover = index < layovers.Length ? layovers[index] : default;
            connections.Add(new Connection(
                ArrivalAirport: arrivalAirport,
                DepartureAirport: departureAirport,
                AirportChange: arrivalAirport != departureAirport,
                ReportedDurationMinutes: Number(layover, "duration"),
                ReportedAirport: Text(layover, "id"),
                ReportedName: Text(layover, "name")));
        }
        return connections.ToArray();
    }

    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static bool? Boolean(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
