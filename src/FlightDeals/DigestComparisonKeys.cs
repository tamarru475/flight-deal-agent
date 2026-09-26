namespace FlightDeals;

// Lists compare by ordered contents, not array identity. Notes are sorted before creating this key;
// destinations and flight segments retain their original order. Duplicate notes remain significant.
public sealed class DigestSequenceKey<T>(IEnumerable<T> values) : IEquatable<DigestSequenceKey<T>>
{
    private readonly T[] items = values.ToArray();
    public bool Equals(DigestSequenceKey<T>? other) => other is not null && items.SequenceEqual(other.items);
    public override bool Equals(object? other) => other is DigestSequenceKey<T> key && Equals(key);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in items) hash.Add(item);
        return hash.ToHashCode();
    }
}

public sealed record DigestSearchDefinitionKey(
    string Id, string Origin, DigestSequenceKey<string> Destinations,
    DateOnly OutboundDate, DateOnly ReturnDate, int MinTripDays, int MaxTripDays,
    int Adults, string Currency, Cabin RequestedCabin, int MaxStops, int MaxDurationMinutes,
    bool ExcludeKnownSeparateTickets, bool ExcludeAirportChanges, int? RequiredDestinationNights)
{
    public static DigestSearchDefinitionKey From(SearchProfile profile) => new(
        Id: profile.Id, Origin: profile.Origin, Destinations: new(profile.Destinations),
        OutboundDate: profile.OutboundDate, ReturnDate: profile.ReturnDate,
        MinTripDays: profile.MinTripDays, MaxTripDays: profile.MaxTripDays,
        Adults: profile.Adults, Currency: profile.Currency, RequestedCabin: profile.RequestedCabin,
        MaxStops: profile.MaxStops, MaxDurationMinutes: profile.MaxDurationMinutes,
        ExcludeKnownSeparateTickets: profile.ExcludeKnownSeparateTickets,
        ExcludeAirportChanges: profile.ExcludeAirportChanges,
        RequiredDestinationNights: profile.RequiredDestinationNights);
}

public sealed record DigestSegmentKey(string DepartureAirport, string ArrivalAirport, string? Airline, Cabin Cabin)
{
    public static DigestSegmentKey From(Segment segment) => new(segment.DepartureAirport,
        segment.ArrivalAirport, segment.Airline, segment.Cabin);
}

public sealed record DigestComparableProductKey(
    DigestSearchDefinitionKey SearchDefinition,
    DigestSequenceKey<DigestSegmentKey> OutboundSegments,
    DigestSequenceKey<DigestSegmentKey> ReturnSegments,
    DigestSequenceKey<string> OutboundFareNotes,
    DigestSequenceKey<string> ReturnFareNotes,
    string BaggageStatus, string ConnectionProtection)
{
    public static DigestComparableProductKey From(Observation quote) => new(
        SearchDefinition: DigestSearchDefinitionKey.From(quote.Profile),
        OutboundSegments: new(quote.Outbound.Segments.Select(DigestSegmentKey.From)),
        ReturnSegments: new(quote.Return.Segments.Select(DigestSegmentKey.From)),
        OutboundFareNotes: new(quote.Outbound.FareNotes.Order(StringComparer.Ordinal)),
        ReturnFareNotes: new(quote.Return.FareNotes.Order(StringComparer.Ordinal)),
        BaggageStatus: quote.BaggageStatus, ConnectionProtection: quote.ConnectionProtection);
}
