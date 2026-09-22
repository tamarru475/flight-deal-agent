using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class WeeklyDigestTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);

    [Fact]
    public void FirstActivationWaitsForNextMondayAndCalendarWeeksRespectDaylightSaving()
    {
        var next = WeeklyDigestSchedule.Next(At("2026-09-22T00:00:00Z"));
        Assert.Equal(At("2026-09-27T20:00:00Z"), next.DueAt); // Monday 09:00 NZDT.
        Assert.Equal(At("2026-09-20T12:00:00Z"), next.Start);
        Assert.Equal(At("2026-09-27T11:00:00Z"), next.End);
        Assert.Equal(167, (next.End - next.Start).TotalHours);
        Assert.Equal(next, WeeklyDigestSchedule.LatestDue(next.DueAt));
        Assert.NotEqual(next, WeeklyDigestSchedule.LatestDue(next.DueAt.AddTicks(-1)));
        Assert.Equal(next.DueAt.AddDays(7), WeeklyDigestSchedule.Next(next.DueAt).DueAt);
    }

    [Fact]
    public void AutumnCalendarWeekHas169HoursAndDowntimeSelectsOnlyLatestDueWeek()
    {
        var period = WeeklyDigestSchedule.LatestDue(At("2027-04-05T00:00:00Z"));
        Assert.Equal(169, (period.End - period.Start).TotalHours);
        Assert.Equal(At("2027-04-04T21:00:00Z"), period.DueAt);
        Assert.Equal(period, WeeklyDigestSchedule.LatestDue(period.DueAt.AddDays(3)));
    }

    internal static DigestPeriod Period => WeeklyDigestSchedule.LatestDue(At("2026-09-28T01:00:00Z"));

    internal static Observation Fare(int day, decimal price, Classification classification = Classification.Normal) =>
        NotificationTests.Fare(classification, price) with { ObservedAt = Period.Start.AddDays(day) };

    internal static DigestData Data(params Observation[] observations) => new(observations, [], [], null);

    [Fact]
    public void QuietWeekStillIncludesEveryActiveProfileAndFooter()
    {
        var email = WeeklyDigestBuilder.Build(Period, [new() { Search = TestData.Profile }], Data(), null, .05m, 200, 50);
        Assert.Contains("Weekly flight summary", email.Subject);
        Assert.Contains("tokyo-premium", email.Body);
        Assert.Contains("No observations", email.Body);
        Assert.Contains("Observation count: 0", email.Body);
        Assert.Contains("Successful: 0", email.Body);
        Assert.Contains("Provider usage: unavailable", email.Body);
        Assert.Contains("Immediate alerts accepted by SMTP: 0", email.Body);
    }

    [Fact]
    public void QuotesClassificationsCabinsAndMovementAreSummarizedWithoutMarketLabels()
    {
        var data = Data(Fare(1, 3500), Fare(2, 3400), Fare(3, 3200));
        var email = WeeklyDigestBuilder.Build(Period, [new() { Search = TestData.Profile }], data, null, .05m, 200, 50);
        foreach (var text in new[] { "3 Normal observations", "NZD 6400.00", "NZD 3200.00", "ended lower",
                     "PremiumEconomy", "2027-05-01", "2027-05-15", "Connection protection", "not a market trend" })
            Assert.Contains(text, email.Body);
    }

    [Theory]
    [InlineData(3150, "ended lower")]
    [InlineData(3150.01, "roughly unchanged")]
    [InlineData(3850, "ended higher")]
    public void MovementUsesConfigurableInclusiveThreshold(decimal last, string expected)
    {
        var observations = new[] { Fare(1, 3500), Fare(2, 3500), Fare(3, last) };
        Assert.Contains(expected, WeeklyDigestBuilder.Movement(observations, .10m));
    }

    [Fact]
    public void IncomparableQuotesNeverCreateAPriceMovementClaim()
    {
        var first = Fare(1, 3500);
        var mixed = Fare(2, 3000) with { Return = TestData.Journey(false, Cabin.Economy) };
        Assert.Equal("Too little comparable data to assess movement", WeeklyDigestBuilder.Movement([first, mixed, Fare(3, 2500)], .05m));
        var changedDate = Fare(2, 3000) with { Profile = TestData.Profile with { ReturnDate = new(2027, 5, 20) } };
        Assert.Equal("Too little comparable data to assess movement", WeeklyDigestBuilder.Movement([first, changedDate, Fare(3, 2500)], .05m));
    }

    [Fact]
    public void ChangedDefinitionsAreSeparateAndPeriodEndIsExclusive()
    {
        var changed = Fare(2, 1000, Classification.Deal) with { Profile = TestData.Profile with { Adults = 1 } };
        var outside = Fare(1, 1, Classification.Deal) with { ObservedAt = Period.End };
        var email = WeeklyDigestBuilder.Build(Period, [new() { Search = TestData.Profile }],
            Data(Fare(1, 3500), changed, outside), null, .05m, 200, 50);
        Assert.Contains("Search definition", email.Body);
        Assert.Contains("Deal observed", email.Body);
        Assert.DoesNotContain("NZD 1.00", email.Body);
        Assert.Contains("1 Normal observations", email.Body);
    }

    [Fact]
    public void FooterSeparatesReservationsRequestsAndProviderUsage()
    {
        var start = Period.Start.AddDays(1);
        var run = new ScanRun(Guid.NewGuid(), start, TestData.Profile, RunStatus.Failed, 2,
            [new(1, "Outbound", start, "StartedPotentiallyCharged")]);
        var data = Data() with { Runs = [run], Budget = new(new(2026, 10, 11), 30, 28) };
        var account = TestData.Account with { Usage = 32, Remaining = 218, RenewalDate = new(2026, 10, 11) };
        var email = WeeklyDigestBuilder.Build(Period, [], data, account, .05m, 200, 50);
        Assert.Contains("Failed: 1", email.Body);
        Assert.Contains("Reserved credits: 2", email.Body);
        Assert.Contains("Potentially charged requests: 1", email.Body);
        Assert.Contains("Operating headroom: 168", email.Body);
    }

    [Fact]
    public void CheapSummaryAndChangedBaggageAreExplicit()
    {
        var first = Fare(1, 3000, Classification.Normal);
        var second = Fare(2, 2800, Classification.Cheap);
        var last = Fare(3, 2600, Classification.Cheap) with
        {
            Return = TestData.Journey(false) with { FareNotes = ["Checked baggage for a fee"] }
        };
        var email = WeeklyDigestBuilder.Build(Period, [new() { Search = TestData.Profile }],
            Data(first, second, last), null, .05m, 200, 50);
        Assert.Contains("Cheap observed", email.Body);
        Assert.Contains("Too little comparable data to assess movement", email.Body);
        Assert.Contains("Checked baggage for a fee", email.Body);
    }

    [Fact]
    public void FooterCountsAttemptsAndAcceptedAlertsAtTheirOwnPeriodBoundaries()
    {
        var run = new ScanRun(Guid.NewGuid(), Period.Start.AddMinutes(-1), TestData.Profile, RunStatus.Completed, 2,
            [new(1, "Outbound", Period.Start.AddMinutes(-1), "ResponseReceived"),
             new(2, "ReturnOptions", Period.Start, "ResponseReceived")]);
        var alert = new NotificationRecord(Guid.NewGuid(), "scope", DeliveryStatus.Sent, "test", "<test@local>",
            Period.Start.AddMinutes(-1), SentAt: Period.Start);
        var email = WeeklyDigestBuilder.Build(Period, [], Data() with { Runs = [run], Notifications = [alert] }, null, .05m, 200, 50);
        Assert.Contains("Reserved credits: 0", email.Body);
        Assert.Contains("Potentially charged requests: 1", email.Body);
        Assert.Contains("Immediate alerts accepted by SMTP: 1", email.Body);
    }

    [Fact]
    public async Task DigestDefaultsToDisabledRegardlessOfImmediateAlertSwitch()
    {
        Assert.False(new WeeklyDigestSettings().Enabled);
        Assert.Throws<InvalidOperationException>(() => new WeeklyDigestSettings { MovementThresholdFraction = 0 }.Validate());
        var sender = new SmtpNotificationSender(new NotificationSettings { Enabled = true }, new WeeklyDigestSettings());
        var record = new DigestDelivery(Period, DigestStatus.Sending, "<test@local>", new("test", "test"));
        Assert.Equal(DeliveryStatus.Failed, await sender.SendDigest(record, default));
    }
}
