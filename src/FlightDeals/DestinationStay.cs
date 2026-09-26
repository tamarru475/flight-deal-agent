using System.Globalization;

namespace FlightDeals;

public static class DestinationStay
{
    public static bool MeetsRequirement(SearchProfile profile, Journey outbound, Journey inbound)
    {
        if (profile.RequiredDestinationNights is not { } required) return true;
        var arrival = outbound.Segments.LastOrDefault();
        var departure = inbound.Segments.FirstOrDefault();
        if (arrival is null || departure is null || arrival.ArrivalAirport != departure.DepartureAirport
            || !profile.Destinations.Contains(arrival.ArrivalAirport)) return false;
        if (!TryLocalDate(arrival.ArrivalLocal, out var arrivalDate)
            || !TryLocalDate(departure.DepartureLocal, out var departureDate)) return false;
        // Both dates belong to the same destination airport. Count local calendar nights, not UTC days.
        return departureDate.DayNumber - arrivalDate.DayNumber == required;
    }

    private static bool TryLocalDate(string? timestamp, out DateOnly date)
    {
        date = default;
        if (!DateTime.TryParseExact(timestamp, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local)) return false;
        date = DateOnly.FromDateTime(local);
        return true;
    }
}
