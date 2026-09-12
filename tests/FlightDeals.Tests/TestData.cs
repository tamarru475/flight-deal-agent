using FlightDeals;

namespace FlightDeals.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
    public static SearchProfile Profile => new() { OutboundDate = new(2027, 5, 1), ReturnDate = new(2027, 5, 15) };
    public static AccountQuota Account => new("Active", "Free Plan", 250, 0, 250, new(2026, 10, 1));
    public static Journey Journey(bool outbound, Cabin cabin = Cabin.PremiumEconomy) => new(660,
        [new(outbound ? "AKL" : "NRT", outbound ? "NRT" : "AKL",
            outbound ? "2027-05-01 10:15" : "2027-05-15 20:00",
            outbound ? "2027-05-01 18:15" : "2027-05-16 09:25",
            660, "Example Air", outbound ? "EX 99" : "EX 98", cabin, cabin.ToString(), null)], [], null, []);
    public static ManualBaseline Baseline => new()
    {
        Id = "test-baseline", Origin = "AKL", Destinations = ["NRT", "HND"], MinTripDays = 10, MaxTripDays = 30,
        RequestedCabin = Cabin.PremiumEconomy, Applicability = CabinApplicability.AllSegmentsInRequestedCabin,
        Thresholds = new(2000, 2500, 4000)
    };
    public static AppSettings Settings => new() { LiveSearchEnabled = true, Profile = Profile };
}

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => TestData.Now;
}
