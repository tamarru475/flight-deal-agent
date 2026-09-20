using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class NotificationTests
{
    internal static Observation Fare(Classification classification = Classification.Cheap, decimal price = 2400)
    {
        var outbound = TestData.Journey(true);
        var inbound = TestData.Journey(false);
        var assessment = AssessmentEvaluator.Evaluate(TestData.Profile, outbound, inbound, price * 2,
            [TestData.Baseline], TestData.Now) with { Classification = classification };
        return new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, TestData.Profile,
            price * 2, price, outbound, inbound, assessment);
    }

    private static NotificationState Previous(Classification classification, decimal price = 2400) => new()
    {
        LastClassification = classification, LastNotifiedObservationId = Guid.NewGuid(),
        LastNotifiedPricePerAdult = price, LastNotifiedClassification = classification
    };

    [Theory]
    [InlineData(Classification.Deal, true)]
    [InlineData(Classification.Cheap, true)]
    [InlineData(Classification.Normal, false)]
    [InlineData(Classification.Expensive, false)]
    [InlineData(Classification.InsufficientBaseline, false)]
    public void FirstFareMustBeInteresting(Classification classification, bool send) =>
        Assert.Equal(send, NotificationPolicy.Evaluate(Fare(classification), new(), .10m, false).Send);

    [Theory]
    [InlineData(Classification.Normal, Classification.Cheap, true)]
    [InlineData(Classification.Normal, Classification.Deal, true)]
    [InlineData(Classification.Cheap, Classification.Deal, true)]
    [InlineData(Classification.Deal, Classification.Cheap, false)]
    [InlineData(Classification.Deal, Classification.Deal, false)]
    [InlineData(Classification.Cheap, Classification.Cheap, false)]
    public void ClassificationTransitions(Classification previous, Classification current, bool send) =>
        Assert.Equal(send, NotificationPolicy.Evaluate(Fare(current), Previous(previous), .10m, false).Send);

    [Theory]
    [InlineData(2160, true)]
    [InlineData(2160.01, false)]
    [InlineData(2100, true)]
    public void ImprovementUsesLastNotifiedPrice(decimal price, bool send) =>
        Assert.Equal(send, NotificationPolicy.Evaluate(Fare(price: price), Previous(Classification.Cheap), .10m, false).Send);

    [Fact]
    public void ConfigurableImprovementAndUnresolvedDelivery()
    {
        Assert.False(NotificationPolicy.Evaluate(Fare(price: 2160), Previous(Classification.Cheap), .20m, false).Send);
        Assert.False(NotificationPolicy.Evaluate(Fare(Classification.Deal), new(), .10m, true).Send);
    }

    [Theory]
    [InlineData(Cabin.Economy)]
    [InlineData(Cabin.Unknown)]
    public void ActualSegmentsBlockUnverifiedPremiumEvenIfAssessmentClaimsOtherwise(Cabin cabin)
    {
        var fare = Fare() with { Return = TestData.Journey(false, cabin) };
        Assert.False(NotificationPolicy.Evaluate(fare, new(), .10m, false).Send);
        var economy = fare with { Profile = fare.Profile with { RequestedCabin = Cabin.Economy } };
        Assert.True(NotificationPolicy.Evaluate(economy, new(), .10m, false).Send);
    }

    [Fact]
    public void SearchDefinitionChangesResetComparisonScope()
    {
        var profile = TestData.Profile;
        Assert.NotEqual(NotificationPolicy.Scope(profile), NotificationPolicy.Scope(profile with { Adults = 1 }));
        Assert.NotEqual(NotificationPolicy.Scope(profile), NotificationPolicy.Scope(profile with { Currency = "USD" }));
        Assert.NotEqual(NotificationPolicy.Scope(profile), NotificationPolicy.Scope(profile with { ReturnDate = profile.ReturnDate.AddDays(1) }));
    }

    [Fact]
    public void EmailExplainsQuoteAndItsLimitations()
    {
        var email = NotificationEmailBuilder.Build(Fare());
        Assert.Contains("Cheap", email.Subject);
        foreach (var required in new[] { "NZD 4800.00", "NZD 2400.00", "2027-05-01", "2027-05-15",
                     "PremiumEconomy", "660 minutes", "ManualBaseline", "IncompleteUnverified", "Unknown", "Return:" })
            Assert.Contains(required, email.Body);
    }

    [Fact]
    public async Task DisabledSenderNeverConnects()
    {
        var settings = new NotificationSettings();
        settings.Validate();
        var record = new NotificationRecord(Guid.NewGuid(), "scope", DeliveryStatus.Pending, "test", "<test@local>", TestData.Now);
        Assert.Equal(DeliveryStatus.Failed, await new SmtpNotificationSender(settings).Send(record, new("test", "test"), default));
        Assert.Throws<InvalidOperationException>(() => new NotificationSettings { Enabled = true }.Validate());
    }
}
