using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class ScheduleTests
{
    private static MonitoredProfile Profile(string id, int priority = 100, double interval = 56) => new()
    {
        Search = TestData.Profile with { Id = id }, Priority = priority, TargetIntervalHours = interval
    };

    private static AppSettings Settings(params MonitoredProfile[] profiles) => new()
    {
        LiveSearchEnabled = true, SchedulerEnabled = true, Profiles = profiles
    };

    private static ScheduleState Empty => new(new(), DateTimeOffset.MinValue);

    [Fact]
    public void ChoosesPriorityThenOldestDueThenOrdinalId()
    {
        var high = Profile("high", 200);
        Assert.Same(high, SchedulePlanner.SelectDue([Profile("low"), high], Empty, TestData.Now));
        var state = new ScheduleState(new() { ["a"] = TestData.Now.AddDays(-4), ["b"] = TestData.Now.AddDays(-5) }, DateTimeOffset.MinValue);
        Assert.Equal("b", SchedulePlanner.SelectDue([Profile("a"), Profile("b")], state, TestData.Now)!.Search.Id);
        Assert.Equal("a", SchedulePlanner.SelectDue([Profile("b"), Profile("a")], Empty, TestData.Now)!.Search.Id);
    }

    [Fact]
    public void HonorsDifferentIntervalsAndExactDueBoundary()
    {
        var state = new ScheduleState(new() { ["fast"] = TestData.Now.AddHours(-24), ["slow"] = TestData.Now.AddHours(-24) }, DateTimeOffset.MinValue);
        Assert.Equal("fast", SchedulePlanner.SelectDue([Profile("slow", interval: 56), Profile("fast", interval: 24)], state, TestData.Now)!.Search.Id);
        Assert.Null(SchedulePlanner.SelectDue([Profile("fast", interval: 24)], state, TestData.Now.AddTicks(-1)));
    }

    [Fact]
    public void SkipsInactiveExpiredAndGloballyDeferredProfiles()
    {
        var inactive = Profile("inactive") with { Active = false };
        var expired = Profile("expired") with { Search = TestData.Profile with { OutboundDate = new(2026, 9, 11) } };
        Assert.Null(SchedulePlanner.SelectDue([inactive, expired], Empty, TestData.Now));
        Assert.Null(SchedulePlanner.SelectDue([Profile("a")], Empty with { NextDispatch = TestData.Now.AddMinutes(1) }, TestData.Now));
    }

    [Fact]
    public void SpreadsRemainingSlotsOverProviderPeriod()
    {
        var abundant = SchedulePlanner.NextDispatch(new(new(2026, 10, 1), 2, 0), TestData.Settings, TestData.Now);
        var scarce = SchedulePlanner.NextDispatch(new(new(2026, 10, 1), 198, 0), TestData.Settings, TestData.Now);
        Assert.Equal(TestData.Now.AddMinutes(288), abundant);
        Assert.Equal(TestData.Now.AddDays(10), scarce);
    }

    [Fact]
    public async Task RestartAndMissedIntervalsDoNotCreateCatchUpScans()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider(store);
        var settings = Settings(Profile("a"), Profile("b"));
        var first = await new ScanService(provider, store, settings, new FixedClock()).RunDue(default);
        Assert.Equal("a", first!.Profile.Id);
        Assert.Null(await new ScanService(provider, store, settings, new FixedClock()).RunDue(default));
        Assert.Equal(2, provider.Calls);
        Assert.Equal(TestData.Now, store.Schedule.LastScans["a"]);
    }

    [Fact]
    public async Task FailedSearchKeepsScheduleAndReservation()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider(store) { FailReturn = true };
        var service = new ScanService(provider, store, Settings(Profile("a")), new FixedClock());
        Assert.Equal(RunStatus.Failed, (await service.RunDue(default))!.Status);
        Assert.Null(await service.RunDue(default));
        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, store.Budget!.AccountedCredits);
    }

    [Fact]
    public async Task ManualAndScheduledProfilesShareTheSameCeiling()
    {
        var store = new MemoryStore { Budget = new(new(2026, 10, 1), 198, 0) };
        var provider = new FakeProvider(store);
        var service = new ScanService(provider, store, Settings(Profile("a"), Profile("b")), new FixedClock());
        Assert.NotNull(await service.RunDue(default));
        await Assert.ThrowsAsync<ScanException>(() => service.Run("b", default));
        Assert.Equal(200, store.Budget!.AccountedCredits);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ManualScanDefersAutomaticScanOfTheSameProfile()
    {
        var store = new MemoryStore();
        var settings = Settings(Profile("a"));
        var service = new ScanService(new FakeProvider(store), store, settings, new FixedClock());
        await service.Run("a", default);
        var afterPacing = store.Schedule.NextDispatch;
        Assert.Null(SchedulePlanner.SelectDue(settings.MonitoredProfiles, store.Schedule, afterPacing));
    }

    [Fact]
    public async Task DisabledSchedulerMakesNoRequests()
    {
        var provider = new FakeProvider();
        Assert.Null(await new ScanService(provider, new MemoryStore(), TestData.Settings, new FixedClock()).RunDue(default));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void ValidatesMultipleProfilesAndRetainsLegacyConfiguration()
    {
        TestData.Settings.Validate();
        Settings(Profile("a"), Profile("b")).Validate();
        Assert.Throws<ScanException>(() => Settings(Profile("a"), Profile("a")).Validate());
        Assert.Throws<ScanException>(() => Settings(Profile("a", interval: 0)).Validate());
        Assert.Throws<ScanException>(() => Settings(Profile("a", priority: -1)).Validate());
    }
}
