using System.Net;
using System.Text.Json;
using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class ProviderTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "outbound.json"));

    [Fact]
    public void ParsesSegmentFactsWithoutTrustingLayoverLabelOrAssumingMissingValues()
    {
        using var doc = JsonDocument.Parse(Fixture);
        var options = SerpApiProvider.ParseOptions(doc.RootElement);
        Assert.Equal(2, options.Length);
        var j = options[0].Journey;
        Assert.Equal(Cabin.PremiumEconomy, j.Segments[0].Cabin);
        Assert.Equal(Cabin.Economy, j.Segments[1].Cabin);
        Assert.Equal(660, j.Segments[0].DurationMinutes);
        Assert.Null(j.Segments[0].Overnight);
        Assert.False(j.Segments[1].Overnight);
        Assert.True(j.Connections[0].AirportChange);
        Assert.Equal("HND", j.Connections[0].ArrivalAirport);
        Assert.Equal("NRT", j.Connections[0].DepartureAirport);
        Assert.Null(j.SeparateTickets);
        Assert.Empty(j.FareNotes);
        Assert.Null(options[1].Price);
        Assert.Equal(Cabin.Unknown, options[1].Journey.Segments[0].Cabin);
        Assert.DoesNotContain("synthetic-token", JsonSerializer.Serialize(options, JsonDefaults.Options));
    }

    [Fact]
    public async Task SendsTwoAdultsNzdCabinAndReturnTokenWithoutAdditionalRequests()
    {
        var handler = new Handler(Fixture);
        var provider = new SerpApiProvider(new HttpClient(handler), "synthetic-secret");
        await provider.Search(TestData.Profile, "selection", default);
        Assert.Equal(1, handler.Calls);
        var query = handler.Uri!.Query;
        Assert.Contains("adults=2", query);
        Assert.Contains("currency=NZD", query);
        Assert.Contains("travel_class=2", query);
        Assert.Contains("departure_token=selection", query);
        Assert.DoesNotContain("booking_token", query);
    }

    [Fact]
    public async Task RejectsWrongCurrencyBeforeAcceptingPrices()
    {
        var provider = new SerpApiProvider(new HttpClient(new Handler(Fixture.Replace("NZD", "USD"))), "synthetic-secret");
        await Assert.ThrowsAsync<ScanException>(() => provider.Search(TestData.Profile, null, default));
    }

    [Fact]
    public async Task ProviderErrorCannotLeakBodyOrSecretAndDoesNotRetry()
    {
        var handler = new Handler("{\"error\":\"synthetic-secret\"}");
        var provider = new SerpApiProvider(new HttpClient(handler), "synthetic-secret");
        var error = await Assert.ThrowsAsync<ScanException>(() => provider.Search(TestData.Profile, null, default));
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        public int Calls;
        public Uri? Uri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        }
    }
}
