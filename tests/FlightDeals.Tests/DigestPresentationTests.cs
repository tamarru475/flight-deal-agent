using System.Text.Json;
using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class DigestPresentationTests
{
    private static NotificationEmail Render(params Observation[] quotes) => WeeklyDigestBuilder.Build(
        WeeklyDigestTests.Period, [new() { Search = TestData.Profile }], WeeklyDigestTests.Data(quotes), null, .05m, 200, 50);

    [Fact]
    public void SharedContextIsShownOnceAndRoutineZeroCountsAreHidden()
    {
        var email = Render(WeeklyDigestTests.Fare(1, 3500));
        Assert.Contains("All fares are return from Auckland for 2 adults. Prices shown per adult.", email.Body);
        Assert.Equal(1, email.Body.Split("2 adults").Length - 1);
        Assert.DoesNotContain("still running", email.Body);
        Assert.DoesNotContain("no suitable itinerary", email.Body);
    }

    [Fact]
    public void EstablishedRoutesPrecedeLearningRoutesAlphabetically()
    {
        var tokyo = WeeklyDigestTests.Fare(1, 3500);
        var paris = tokyo with { Profile = tokyo.Profile with { Id = "paris", Destinations = ["CDG"] } };
        var bangkok = WeeklyDigestTests.Fare(1, 1500, Classification.InsufficientBaseline) with
        { Profile = tokyo.Profile with { Id = "bangkok", Destinations = ["BKK"] } };
        var quotes = new[] { bangkok, tokyo, paris };
        var email = WeeklyDigestBuilder.Build(WeeklyDigestTests.Period,
            quotes.Select(o => new MonitoredProfile { Search = o.Profile }), WeeklyDigestTests.Data(quotes), null, .05m, 200, 50);
        Assert.True(email.Body.IndexOf("Paris —") < email.Body.IndexOf("Tokyo —"));
        Assert.True(email.Body.IndexOf("Tokyo —") < email.Body.IndexOf("Bangkok —"));
    }

    [Fact]
    public void NonzeroScanProblemsRemainVisible()
    {
        var period = WeeklyDigestTests.Period;
        var runs = new[] { RunStatus.NoSuitableReturn, RunStatus.Running }
            .Select(status => new ScanRun(Guid.NewGuid(), period.Start, TestData.Profile, status, 2, [])).ToArray();
        var email = WeeklyDigestBuilder.Build(period, [], WeeklyDigestTests.Data() with { Runs = runs }, null, .05m, 200, 50);
        Assert.Contains("1 scans found no suitable itinerary", email.Body);
        Assert.Contains("1 scans still running", email.Body);
    }

    [Fact]
    public void SingleQuoteHasOneItineraryAndNoInternalNames()
    {
        var email = Render(WeeklyDigestTests.Fare(1, 3000, Classification.InsufficientBaseline));
        Assert.Contains("Baseline still learning", email.Body);
        Assert.DoesNotContain("Lowest", email.Body);
        Assert.Equal(1, email.Body.Split("Example Air").Length - 1);
        foreach (var internalName in new[] { "InsufficientBaseline", "AllSegmentsInRequestedCabin", "IncompleteUnverified", "tokyo-premium", "ManualBaseline" })
            Assert.DoesNotContain(internalName, email.Body);
    }

    [Theory]
    [InlineData(990.01, false)]
    [InlineData(990, true)]
    public void WeeklyLowUsesInclusiveOnePercentDisplayThreshold(decimal lowest, bool show)
    {
        var email = Render(WeeklyDigestTests.Fare(1, lowest), WeeklyDigestTests.Fare(2, 1000));
        Assert.Equal(show, email.Body.Contains("Lowest this week"));
    }

    [Fact]
    public void HtmlEscapesProviderTextAndContainsNoActiveContent()
    {
        var quote = WeeklyDigestTests.Fare(1, 3500);
        var segment = quote.Outbound.Segments[0] with { Airline = "Air <script>alert('x')</script> & Co" };
        var email = Render(quote with { Outbound = quote.Outbound with { Segments = [segment] } });
        Assert.Contains("Air <script>", email.Body);
        Assert.Contains("&lt;script&gt;", email.HtmlBody);
        Assert.Contains("&amp; Co", email.HtmlBody);
        Assert.DoesNotContain("<script>", email.HtmlBody);
        Assert.DoesNotContain("<img", email.HtmlBody);
        Assert.DoesNotContain("<link", email.HtmlBody);
        Assert.Contains("NZ$3,500", email.Body);
        Assert.Contains("NZ$3,500", email.HtmlBody);
    }

    [Fact]
    public void MixedCabinsDescribeSegmentsWithoutInternalCompositionLabels()
    {
        var quote = WeeklyDigestTests.Fare(1, 3500) with { Return = TestData.Journey(false, Cabin.Economy) };
        var email = Render(quote);
        Assert.Contains("Premium Economy: AKL→NRT", email.Body);
        Assert.Contains("Economy: NRT→AKL", email.Body);
        Assert.Contains("premium-segment quality is not yet assessed", email.Body);
        Assert.DoesNotContain("MixedCabin", email.Body);
    }

    [Fact]
    public void ExistingPlainTextEmailPayloadRemainsCompatible()
    {
        var email = JsonSerializer.Deserialize<NotificationEmail>("{\"subject\":\"old\",\"body\":\"original\"}", JsonDefaults.Options)!;
        Assert.Null(email.HtmlBody);
        Assert.Equal("{\"subject\":\"old\",\"body\":\"original\"}", JsonSerializer.Serialize(email, JsonDefaults.Options));
    }

    [Fact]
    public void DeliveryProblemsAreVisibleButSuppressionMechanicsAreNot()
    {
        var period = WeeklyDigestTests.Period;
        var notifications = new[] { DeliveryStatus.Suppressed, DeliveryStatus.Unknown, DeliveryStatus.Failed }
            .Select(status => new NotificationRecord(Guid.NewGuid(), "scope", status, "internal reason", "<test@local>", period.Start)).ToArray();
        var email = WeeklyDigestBuilder.Build(period, [], WeeklyDigestTests.Data() with { Notifications = notifications }, null, .05m, 200, 50);
        Assert.Contains("No deal alerts sent this week", email.Body);
        Assert.Contains("2 alert deliveries failed or could not be confirmed", email.Body);
        Assert.DoesNotContain("Suppressed", email.Body);
        Assert.DoesNotContain("internal reason", email.Body);
    }
}
