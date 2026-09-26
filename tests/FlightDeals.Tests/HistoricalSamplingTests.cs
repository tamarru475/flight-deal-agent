using FlightDeals;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FlightDeals.Tests;

public class HistoricalSamplingTests
{
    internal static AppSettings Settings() => new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json").Build().GetSection("FlightDeals").Get<AppSettings>()!;

    [Fact]
    public void ConfiguredCoverageAndCadencesStayWithinApprovedBudget()
    {
        var settings = Settings();
        settings.Validate();
        Assert.False(settings.LiveSearchEnabled);
        Assert.False(settings.SchedulerEnabled);
        Assert.Equal(200, settings.MonthlyCreditLimit);
        Assert.Equal(50, settings.ReserveCredits);
        Assert.Equal(21, settings.Profiles.Length);
        var active = settings.Profiles.Where(p => p.Active).ToArray();
        Assert.Equal(17, active.Length);
        Assert.Equal(2, active.Count(p => p.TargetIntervalHours == 56));
        Assert.Equal(9, active.Count(p => p.TargetIntervalHours == 168));
        Assert.Equal(6, active.Count(p => p.TargetIntervalHours == 336));
        var worstCase = active.Sum(p => 2 * Math.Ceiling(31 * 24 / p.TargetIntervalHours));
        Assert.Equal(182, worstCase);
        Assert.Equal(154.285714, active.Sum(p => 2 * 30 * 24 / p.TargetIntervalHours), 6);
        Assert.All(settings.Profiles.Skip(2), p =>
        {
            Assert.Equal(20, p.Priority);
            Assert.Equal("AKL", p.Search.Origin);
            Assert.Equal(2, p.Search.Adults);
            Assert.Equal("NZD", p.Search.Currency);
            Assert.Equal(Cabin.Economy, p.Search.RequestedCabin);
            Assert.True(p.Search.ExcludeAirportChanges);
            Assert.True(p.Search.ExcludeKnownSeparateTickets);
            Assert.True(p.Search.OutboundDate > new DateOnly(2027, 6, 5));
        });
        Assert.All(settings.Profiles.Where(p => !p.Active), p =>
        {
            Assert.EndsWith("2027-10", p.Search.Id);
            Assert.Equal(336, p.TargetIntervalHours);
        });
        Assert.All(settings.Profiles.Where(p => p.Search.Destinations.Contains("SCL")), p => Assert.Equal(0, p.Search.MaxStops));
        foreach (var name in new[] { "per", "cns" })
            Assert.Equal(336, settings.Profiles.Single(p => p.Search.Id == $"{name}-economy-2027-08").TargetIntervalHours);
        Assert.Equal(3, settings.ManualBaselines.Length);
    }

    [Fact]
    public void ExistingNotificationScopesAndSchedulesRemainUnchanged()
    {
        var settings = Settings();
        var tokyo = settings.Profiles[0];
        var europe = settings.Profiles[1];
        Assert.Equal(100, tokyo.Priority);
        Assert.Equal(90, europe.Priority);
        Assert.Equal(56, tokyo.TargetIntervalHours);
        Assert.Equal(56, europe.TargetIntervalHours);
        // Captured from the deployed profiles before adding the optional local-night rule.
        Assert.Equal("EB8E12AA6A0904D71E10D71E9C614DCBA4D3056F2FCB140826151C3ED3390515", NotificationPolicy.Scope(tokyo.Search));
        Assert.Equal("01ADE147066C654A5FF1950A95FC60C52D574FD18A166985420E607AA663D1CA", NotificationPolicy.Scope(europe.Search));
        Assert.Null(tokyo.Search.RequiredDestinationNights);
        Assert.Null(europe.Search.RequiredDestinationNights);
    }

    [Theory]
    [InlineData(2199.99, Classification.Deal)]
    [InlineData(2200, Classification.Cheap)]
    [InlineData(2699.99, Classification.Cheap)]
    [InlineData(2700, Classification.Normal)]
    [InlineData(3499.99, Classification.Normal)]
    [InlineData(3500, Classification.Expensive)]
    public void EuropeVersionTwoUsesApprovedPerAdultBoundaries(decimal price, Classification expected)
    {
        var settings = Settings();
        var profile = settings.Profiles[1].Search;
        var outbound = Journey(profile, true);
        var inbound = Journey(profile, false);
        var assessment = AssessmentEvaluator.Evaluate(profile, outbound, inbound, price * 2, settings.ManualBaselines, TestData.Now);
        Assert.Equal(expected, assessment.Classification);
        Assert.Equal(2, assessment.BaselineVersion);
        Assert.Equal("akl-europe-economy", assessment.BaselineId);
        Assert.Contains("newer real route/date evidence", assessment.BaselineAssumption);
    }

    [Fact]
    public void NewMarketsRemainUnclassifiedAndVisibleInDigest()
    {
        var settings = Settings();
        var observations = settings.Profiles.Skip(2).Where(p => p.Active).Select(p =>
        {
            var outbound = Journey(p.Search, true);
            var inbound = Journey(p.Search, false);
            var assessment = AssessmentEvaluator.Evaluate(p.Search, outbound, inbound, 1000, settings.ManualBaselines, TestData.Now);
            Assert.Equal(Classification.InsufficientBaseline, assessment.Classification);
            var observation = new Observation(Guid.NewGuid(), Guid.NewGuid(), WeeklyDigestTests.Period.Start.AddDays(1),
                p.Search, 1000, 500, outbound, inbound, assessment);
            Assert.False(NotificationPolicy.Evaluate(observation, new(), .10m, false).Send);
            return observation;
        }).ToArray();
        var email = WeeklyDigestBuilder.Build(WeeklyDigestTests.Period, settings.Profiles,
            new(observations, [], [], null), null, .05m, 200, 50);
        foreach (var observation in observations) Assert.Contains(observation.Profile.Id, email.Body);
        Assert.Contains("InsufficientBaseline", email.Body);
        Assert.DoesNotContain("2027-10", email.Body);
    }

    internal static Journey Journey(SearchProfile profile, bool outbound, string? arrivalLocal = null) => new(240,
        [new(outbound ? "AKL" : profile.Destinations[0], outbound ? profile.Destinations[0] : "AKL",
            $"{(outbound ? profile.OutboundDate : profile.ReturnDate):yyyy-MM-dd} 10:00", arrivalLocal, 240,
            "Synthetic Air", "TEST 1", profile.RequestedCabin, profile.RequestedCabin.ToString(), false)], [], null, []);
}
