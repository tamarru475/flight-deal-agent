using System.Text.RegularExpressions;

namespace FlightDeals;

public sealed class AppSettings
{
    public bool LiveSearchEnabled { get; init; }
    public int MonthlyCreditLimit { get; init; } = 200;
    public int ReserveCredits { get; init; } = 50;
    public SearchProfile Profile { get; init; } = new();
    public ManualBaseline[] ManualBaselines { get; init; } = [];

    public void Validate()
    {
        if (MonthlyCreditLimit is < 2 or > 200 || ReserveCredits < 50 || MonthlyCreditLimit + ReserveCredits > 250)
            throw new ScanException("Budget must leave at least 50 credits and allow no more than 200.");
        ValidateProfile(Profile);
        ValidateBaselines();
    }

    private static bool Airport(string code) => Regex.IsMatch(code, "^[A-Z]{3}$");

    private static void ValidateProfile(SearchProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || !Airport(profile.Origin) || profile.Destinations.Length == 0
            || profile.Destinations.Any(d => !Airport(d) || d == profile.Origin) || profile.Adults is < 1 or > 9
            || !Regex.IsMatch(profile.Currency, "^[A-Z]{3}$") || profile.RequestedCabin == Cabin.Unknown
            || !Enum.IsDefined(profile.RequestedCabin) || profile.MaxStops is < 0 or > 2 || profile.MaxDurationMinutes <= 0)
            throw new ScanException("Invalid search profile.");
        var days = profile.ReturnDate.DayNumber - profile.OutboundDate.DayNumber;
        if (profile.MinTripDays < 1 || profile.MaxTripDays < profile.MinTripDays || days < profile.MinTripDays || days > profile.MaxTripDays)
            throw new ScanException("Travel dates must satisfy the profile's trip-duration bounds.");
    }

    private void ValidateBaselines()
    {
        if (ManualBaselines.Select(baseline => baseline.Id).Distinct().Count() != ManualBaselines.Length)
            throw new ScanException("Baseline IDs must be unique.");
        foreach (var baseline in ManualBaselines)
        {
            if (string.IsNullOrWhiteSpace(baseline.Id) || baseline.Version < 1 || !Airport(baseline.Origin)
                || baseline.Destinations.Length == 0 || baseline.Destinations.Any(d => !Airport(d))
                || baseline.MinTripDays < 1 || baseline.MaxTripDays < baseline.MinTripDays || baseline.TripType != "Return"
                || baseline.PassengerType != "Adult" || baseline.PriceBasis != "PerAdult" || baseline.BaggageBasis != "AsQuoted"
                || !Regex.IsMatch(baseline.Currency, "^[A-Z]{3}$") || baseline.RequestedCabin == Cabin.Unknown
                || !Enum.IsDefined(baseline.RequestedCabin) || !Enum.IsDefined(baseline.Applicability))
                throw new ScanException("Invalid manual baseline scope.");
            ValidateThresholds(baseline.Thresholds);
        }
    }

    private static void ValidateThresholds(Thresholds thresholds)
    {
        var allUnset = thresholds.DealBelow is null && thresholds.CheapBelow is null
            && thresholds.ExpensiveAtOrAbove is null;
        if (allUnset) return;

        var invalidOrder = thresholds.DealBelow <= 0 || thresholds.DealBelow >= thresholds.CheapBelow
            || thresholds.CheapBelow >= thresholds.ExpensiveAtOrAbove;
        if (!thresholds.Complete || invalidOrder)
            throw new ScanException("Supply all three ascending positive thresholds, or leave all unset.");
    }

}
