using System.Net;
using System.Text.Json;
using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class ScanFailureTests
{
    [Theory]
    [InlineData("http", ScanFailureCategory.ProviderHttpError)]
    [InlineData("network", ScanFailureCategory.ProviderHttpError)]
    [InlineData("invalid", ScanFailureCategory.ProviderResponseInvalid)]
    [InlineData("json", ScanFailureCategory.ProviderParseError)]
    [InlineData("shape", ScanFailureCategory.ProviderResponseInvalid)]
    [InlineData("timeout", ScanFailureCategory.Timeout)]
    [InlineData("unexpected", ScanFailureCategory.Unexpected)]
    public async Task OutboundFailuresArePersistedSafelyWithoutRetry(string failure, ScanFailureCategory expected)
    {
        var handler = new FailureHandler(failure);
        var provider = new SearchProvider(new SerpApiProvider(new HttpClient(handler), "private-key"));
        var store = new MemoryStore();
        var run = await new ScanService(provider, store, TestData.Settings, new FixedClock()).Run(default);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(expected, run.FailureCategory);
        Assert.Equal(ScanFailureStage.OutboundSearch, run.FailureStage);
        Assert.Single(run.Attempts);
        Assert.Equal("StartedPotentiallyCharged", run.Attempts[0].Status);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, run.ReservedCredits);
        Assert.Equal(2, store.Budget!.AccountedCredits);
        Assert.NotEqual(DateTimeOffset.MinValue, store.Schedule.NextDispatch);
        var json = JsonSerializer.Serialize(store.SavedRun, JsonDefaults.Options);
        Assert.DoesNotContain("private-key", json);
        Assert.DoesNotContain("secret-payload", json);
        Assert.Equal(expected, JsonSerializer.Deserialize<ScanRun>(json, JsonDefaults.Options)!.FailureCategory);
    }

    [Fact]
    public async Task ReturnFailureRetainsSuccessfulOutboundAndBothReservations()
    {
        var store = new MemoryStore();
        var run = await new ScanService(new ReturnFailureProvider(), store, TestData.Settings, new FixedClock()).Run(default);
        Assert.Equal(ScanFailureStage.ReturnSearch, run.FailureStage);
        Assert.Equal(ScanFailureCategory.Timeout, run.FailureCategory);
        Assert.Equal(2, run.Attempts.Length);
        Assert.Equal("ResponseReceived", run.Attempts[0].Status);
        Assert.Equal("StartedPotentiallyCharged", run.Attempts[1].Status);
        Assert.Equal(2, store.Budget!.AccountedCredits);
    }

    [Fact]
    public void HistoricalRunWithoutDiagnosticsStillDeserializes()
    {
        var run = new ScanRun(Guid.NewGuid(), TestData.Now, TestData.Profile, RunStatus.Failed, 2, []);
        var json = JsonSerializer.Serialize(run, JsonDefaults.Options);
        Assert.DoesNotContain("failureCategory", json);
        var restored = JsonSerializer.Deserialize<ScanRun>(json, JsonDefaults.Options)!;
        Assert.Null(restored.FailureCategory);
        Assert.Null(restored.FailureStage);
    }

    [Fact]
    public async Task AccountFailureDoesNotCreateRunOrReserveCredits()
    {
        var store = new MemoryStore();
        await Assert.ThrowsAsync<ScanException>(() => new ScanService(new AccountFailureProvider(), store,
            TestData.Settings, new FixedClock()).Run(default));
        Assert.Null(store.SavedRun);
        Assert.Null(store.Budget);
        Assert.Equal(DateTimeOffset.MinValue, store.Schedule.NextDispatch);
    }

    private sealed class SearchProvider(IFlightProvider search) : IFlightProvider
    {
        public Task<AccountQuota> GetAccount(CancellationToken ct) => Task.FromResult(TestData.Account);
        public Task<FlightOption[]> Search(SearchProfile profile, string? token, CancellationToken ct) => search.Search(profile, token, ct);
    }

    private sealed class ReturnFailureProvider : IFlightProvider
    {
        public Task<AccountQuota> GetAccount(CancellationToken ct) => Task.FromResult(TestData.Account);
        public Task<FlightOption[]> Search(SearchProfile profile, string? token, CancellationToken ct) => token is null
            ? Task.FromResult<FlightOption[]>([new(3000, TestData.Journey(true), "token")])
            : throw new TimeoutException("private-key secret-payload");
    }

    private sealed class AccountFailureProvider : IFlightProvider
    {
        public Task<AccountQuota> GetAccount(CancellationToken ct) => throw new ScanException("Account failed", ScanFailureCategory.ProviderHttpError);
        public Task<FlightOption[]> Search(SearchProfile profile, string? token, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Search must not run");
    }

    private sealed class FailureHandler(string failure) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (failure == "network") throw new HttpRequestException("private-key secret-payload");
            if (failure == "timeout") throw new TaskCanceledException("private-key secret-payload");
            if (failure == "unexpected") throw new Exception("private-key secret-payload");
            var body = failure switch
            {
                "invalid" => "{\"error\":\"private-key secret-payload\"}",
                "json" => "private-key secret-payload not json",
                "shape" => "{\"search_metadata\":42,\"secret\":\"private-key secret-payload\"}",
                _ => "private-key secret-payload"
            };
            return Task.FromResult(new HttpResponseMessage(failure == "http" ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            { Content = new StringContent(body) });
        }
    }
}
