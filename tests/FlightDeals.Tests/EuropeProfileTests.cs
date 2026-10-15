using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class EuropeProfileTests
{
    [Theory]
    [InlineData("cdg-economy-2027-04-22", "CDG", 22)]
    [InlineData("cdg-economy-2027-04-24", "CDG", 24)]
    [InlineData("bcn-economy-2027-04-22", "BCN", 22)]
    public void EuropeWindowsMatchApprovedTripAndConstraints(string id, string airport, int day)
    {
        var settings = HistoricalSamplingTests.Settings();
        var monitored = Assert.Single(settings.Profiles, p => p.Search.Id == id);
        var profile = monitored.Search;
        Assert.True(monitored.Active);
        Assert.Equal(168, monitored.TargetIntervalHours);
        Assert.Equal(90, monitored.Priority);
        Assert.Equal("AKL", profile.Origin);
        Assert.Equal(new[] { airport }, profile.Destinations);
        Assert.Equal(new DateOnly(2027, 4, day), profile.OutboundDate);
        Assert.Equal(new DateOnly(2027, 6, 3), profile.ReturnDate);
        Assert.Equal(2, profile.Adults);
        Assert.Equal("NZD", profile.Currency);
        Assert.Equal(Cabin.Economy, profile.RequestedCabin);
        Assert.Equal(1, profile.MaxStops);
        Assert.Equal(1800, profile.MaxDurationMinutes);
        Assert.Equal(30, profile.MinTripDays);
        Assert.Equal(60, profile.MaxTripDays);
        Assert.True(profile.ExcludeKnownSeparateTickets);
        Assert.True(profile.ExcludeAirportChanges);
        Assert.Null(profile.RequiredDestinationNights);
        var assessment = AssessmentEvaluator.Evaluate(profile, HistoricalSamplingTests.Journey(profile, true),
            HistoricalSamplingTests.Journey(profile, false), 5000, settings.ManualBaselines, TestData.Now);
        Assert.Equal(Classification.Cheap, assessment.Classification);
        Assert.Equal("akl-europe-economy", assessment.BaselineId);
        Assert.Equal(2, assessment.BaselineVersion);
        Assert.Equal(new Thresholds(2200, 2700, 3500), assessment.AppliedThresholds);
    }

    [Fact]
    public void ReplacementUsesIndependentScopesAndTheExistingSharedGate()
    {
        var settings = HistoricalSamplingTests.Settings();
        Assert.DoesNotContain(settings.Profiles, p => p.Search.Id == "europe-economy");
        var europe = settings.Profiles.Where(p => p.Priority == 90).ToArray();
        Assert.Equal(3, europe.Length);
        Assert.Equal(3, europe.Select(p => NotificationPolicy.Scope(p.Search)).Distinct().Count());
        var state = new ScheduleState(new() { ["europe-economy"] = TestData.Now }, TestData.Now.AddHours(4));
        Assert.Null(SchedulePlanner.SelectDue(europe, state, TestData.Now));
        Assert.NotNull(SchedulePlanner.SelectDue(europe, state, state.NextDispatch));
        Assert.Equal(25.714286, europe.Sum(p => 2 * 30 * 24 / p.TargetIntervalHours), 6);
        Assert.Equal(26.571429, europe.Sum(p => 2 * 31 * 24 / p.TargetIntervalHours), 6);
        Assert.Equal(30, europe.Sum(p => 2 * Math.Ceiling(31 * 24 / p.TargetIntervalHours)));
    }
}
