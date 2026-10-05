using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class CyprusProfileTests
{
    [Theory]
    [InlineData("bcn", 5, 11, 1, 480)]
    [InlineData("tlv", 6, 10, 0, 180)]
    public void PlannedTripProfilesHaveExactApprovedConstraints(string origin, int outbound, int inbound, int stops, int minutes)
    {
        var settings = HistoricalSamplingTests.Settings();
        var monitored = settings.Profiles.Single(p => p.Search.Id == $"{origin}-cyprus-economy-2027-05");
        var profile = monitored.Search;
        Assert.True(monitored.Active);
        Assert.Equal(168, monitored.TargetIntervalHours);
        Assert.Equal(80, monitored.Priority);
        Assert.Equal(origin.ToUpperInvariant(), profile.Origin);
        Assert.Equal(new[] { "LCA", "PFO" }, profile.Destinations);
        Assert.Equal(new DateOnly(2027, 5, outbound), profile.OutboundDate);
        Assert.Equal(new DateOnly(2027, 5, inbound), profile.ReturnDate);
        Assert.Equal(inbound - outbound, profile.MinTripDays);
        Assert.Equal(inbound - outbound, profile.MaxTripDays);
        Assert.Equal(2, profile.Adults);
        Assert.Equal("NZD", profile.Currency);
        Assert.Equal(Cabin.Economy, profile.RequestedCabin);
        Assert.Equal(stops, profile.MaxStops);
        Assert.Equal(minutes, profile.MaxDurationMinutes);
        Assert.True(profile.ExcludeKnownSeparateTickets);
        Assert.True(profile.ExcludeAirportChanges);
        Assert.Null(profile.RequiredDestinationNights);
        var assessment = AssessmentEvaluator.Evaluate(profile, HistoricalSamplingTests.Journey(profile, true),
            HistoricalSamplingTests.Journey(profile, false), 1000, settings.ManualBaselines, TestData.Now);
        Assert.Equal(Classification.InsufficientBaseline, assessment.Classification);
    }

    [Theory]
    [InlineData("bne")]
    [InlineData("syd")]
    [InlineData("bkk")]
    [InlineData("sgn")]
    public void RebalancedHistoricalProfilesRemainActiveAtFortnightlyCadence(string route)
    {
        var profile = HistoricalSamplingTests.Settings().Profiles.Single(p => p.Search.Id == $"{route}-economy-2027-08");
        Assert.True(profile.Active);
        Assert.Equal(336, profile.TargetIntervalHours);
        Assert.Equal(20, profile.Priority);
    }
}
