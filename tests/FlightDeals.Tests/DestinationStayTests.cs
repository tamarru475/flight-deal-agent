using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class DestinationStayTests
{
    [Theory]
    [InlineData("2027-08-09 18:00", true)]
    [InlineData("2027-08-10 00:30", false)]
    [InlineData("2027-08-08 23:00", false)]
    [InlineData("2027-08-17 10:00", false)]
    [InlineData("2027-08-09 99:00", false)]
    [InlineData("not-a-date", false)]
    [InlineData(null, false)]
    public void SevenNightsUsesDestinationArrivalInsteadOfOriginDeparture(string? arrival, bool expected)
    {
        foreach (var id in new[] { "rar", "ppt" })
        {
            var profile = HistoricalSamplingTests.Settings().Profiles.Single(p => p.Search.Id == $"{id}-economy-2027-08").Search;
            Assert.Equal(expected, DestinationStay.MeetsRequirement(profile,
                HistoricalSamplingTests.Journey(profile, true, arrival), HistoricalSamplingTests.Journey(profile, false)));
        }
    }

    [Fact]
    public void MissingReturnTimeAndDifferentDestinationFailButUnsetRulePreservesLegacyBehavior()
    {
        var profile = TestData.Profile with { RequiredDestinationNights = 7 };
        var outbound = TestData.Journey(true);
        var inbound = TestData.Journey(false);
        Assert.False(DestinationStay.MeetsRequirement(profile, outbound, inbound with { Segments = [inbound.Segments[0] with { DepartureLocal = null }] }));
        Assert.False(DestinationStay.MeetsRequirement(profile, outbound, inbound with { Segments = [inbound.Segments[0] with { DepartureAirport = "HND" }] }));
        Assert.True(DestinationStay.MeetsRequirement(profile with { RequiredDestinationNights = null }, outbound, inbound));
        Assert.Throws<ScanException>(() => new AppSettings { Profile = TestData.Profile with { RequiredDestinationNights = 0 } }.Validate());
        Assert.NotEqual(DigestSearchDefinitionKey.From(profile), DigestSearchDefinitionKey.From(profile with { RequiredDestinationNights = null }));
    }

    [Theory]
    [InlineData(true, RunStatus.Completed)]
    [InlineData(false, RunStatus.NoSuitableReturn)]
    public async Task InvalidCheaperReturnIsFilteredWithoutExtraRequests(bool includeValid, RunStatus expected)
    {
        var profile = HistoricalSamplingTests.Settings().Profiles.Single(p => p.Search.Id == "rar-economy-2027-08").Search;
        var provider = new StayProvider(includeValid);
        var store = new MemoryStore();
        var service = new ScanService(provider, store, new AppSettings { LiveSearchEnabled = true, Profile = profile }, new FixedClock());
        var run = await service.Run(default);
        Assert.Equal(expected, run.Status);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, store.Budget!.AccountedCredits);
        if (includeValid)
        {
            Assert.Equal(2000, store.Observation!.TotalPartyPrice);
            Assert.Equal(7, store.Observation.Profile.RequiredDestinationNights);
            Assert.Equal(Classification.InsufficientBaseline, store.Observation.Assessment.Classification);
        }
        else Assert.Null(store.Observation);
    }

    private sealed class StayProvider(bool includeValid) : IFlightProvider
    {
        public int Calls;
        public Task<AccountQuota> GetAccount(CancellationToken ct) => Task.FromResult(TestData.Account);
        public Task<FlightOption[]> Search(SearchProfile profile, string? departureToken, CancellationToken ct)
        {
            Calls++;
            if (departureToken is null) return Task.FromResult<FlightOption[]>(
                [new(1500, HistoricalSamplingTests.Journey(profile, true, "2027-08-09 18:00"), "fake-token")]);
            var valid = HistoricalSamplingTests.Journey(profile, false);
            var invalid = valid with { Segments = [valid.Segments[0] with { DepartureLocal = null }] };
            return Task.FromResult<FlightOption[]>(includeValid ? [new(1000, invalid, null), new(2000, valid, null)] : [new(1000, invalid, null)]);
        }
    }
}
