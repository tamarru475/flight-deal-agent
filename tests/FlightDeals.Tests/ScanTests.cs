using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class ScanTests
{
    [Fact]
    public async Task PairedObservationUsesReturnQuoteAndPersistsBeforeSending()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider(store);
        var run = await new ScanService(provider, store, TestData.Settings, new FixedClock()).Run(default);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(4200, store.Observation!.TotalPartyPrice);
        Assert.Equal(2100, store.Observation.PricePerAdult);
        Assert.Equal(Classification.InsufficientBaseline, store.Observation.Assessment.Classification);
        Assert.Equal(Cabin.PremiumEconomy, store.Observation.Outbound.Segments[0].Cabin);
        Assert.Null(store.Observation.Return.SeparateTickets);
    }

    [Fact]
    public async Task TimeoutDoesNotRetryOrReleaseCredits()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider(store) { FailReturn = true };
        var run = await new ScanService(provider, store, TestData.Settings, new FixedClock()).Run(default);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, store.Budget!.AccountedCredits);
        Assert.Null(store.Observation);
        Assert.Equal("StartedPotentiallyCharged", run.Attempts[1].Status);
    }

    [Fact]
    public async Task NoOutboundStopsAfterOneRequest()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider(store) { EmptyOutbound = true };
        var run = await new ScanService(provider, store, TestData.Settings, new FixedClock()).Run(default);
        Assert.Equal(RunStatus.NoSuitableOutbound, run.Status);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(2, store.Budget!.AccountedCredits); // deliberately conservative, no automatic refund
    }

    [Fact]
    public async Task ReserveFailureMakesNoFlightRequests()
    {
        var store = new MemoryStore { Budget = new(new(2026, 10, 1), 200, 200) };
        var provider = new FakeProvider(store);
        await Assert.ThrowsAsync<ScanException>(() => new ScanService(provider, store, TestData.Settings, new FixedClock()).Run(default));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void AirportChangesAndUnpricedOptionsAreNotSelected()
    {
        var valid = new FlightOption(4200, TestData.Journey(true), "token");
        var changed = valid with { Price = 1000, Journey = valid.Journey with
            { Connections = [new("HND", "NRT", true, 245, "HND", "Narita")] } };
        Assert.Same(valid, ScanService.Select([changed, valid with { Price = null }, valid], TestData.Profile, true));
    }
}

internal sealed class MemoryStore : IScanStore, IScanSession
{
    public BudgetState? Budget;
    public ScanRun? SavedRun;
    public Observation? Observation;
    public Task<IScanSession> OpenSession(CancellationToken ct) => Task.FromResult<IScanSession>(this);
    public Task<BudgetState?> ReadBudget(CancellationToken ct) => Task.FromResult(Budget);
    public ScheduleState Schedule = new(new Dictionary<string, DateTimeOffset>(), DateTimeOffset.MinValue);
    public Task<ScheduleState> ReadSchedule(CancellationToken ct) => Task.FromResult(Schedule);
    public Task Reserve(BudgetState state, ScanRun run, CancellationToken ct, DateTimeOffset? nextDispatch = null)
    {
        Budget = state;
        SavedRun = run;
        Schedule.LastScans[run.Profile.Id] = run.StartedAt;
        Schedule = Schedule with { NextDispatch = nextDispatch ?? Schedule.NextDispatch };
        return Task.CompletedTask;
    }
    public Task SaveRun(ScanRun run, CancellationToken ct) { SavedRun = run; return Task.CompletedTask; }
    public Task Complete(ScanRun run, Observation observation, CancellationToken ct) { SavedRun = run; Observation = observation; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeProvider(MemoryStore? store = null) : IFlightProvider
{
    public int Calls;
    public bool FailReturn;
    public bool EmptyOutbound;
    public Task<AccountQuota> GetAccount(CancellationToken ct) => Task.FromResult(TestData.Account);
    public Task<FlightOption[]> Search(SearchProfile profile, string? token, CancellationToken ct)
    {
        Calls++;
        if (store is not null)
        {
            Assert.NotNull(store.Budget);
            Assert.Equal(Calls, store.SavedRun!.Attempts.Length);
            Assert.Equal("StartedPotentiallyCharged", store.SavedRun.Attempts[^1].Status);
        }
        if (token is not null && FailReturn) throw new ScanException("Test timeout");
        if (token is null && EmptyOutbound) return Task.FromResult<FlightOption[]>([]);
        return Task.FromResult<FlightOption[]>([new(token is null ? 3000 : 4200, TestData.Journey(token is null), token is null ? "outbound-token" : null)]);
    }
}
