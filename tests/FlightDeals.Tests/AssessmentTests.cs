using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class AssessmentTests
{
    [Theory]
    [InlineData(3999, Classification.Deal)]
    [InlineData(4000, Classification.Cheap)]
    [InlineData(4999, Classification.Cheap)]
    [InlineData(5000, Classification.Normal)]
    [InlineData(7999, Classification.Normal)]
    [InlineData(8000, Classification.Expensive)]
    public void PerAdultThresholdsHaveExplicitBoundaries(decimal total, Classification expected)
    {
        var a = AssessmentEvaluator.Evaluate(TestData.Profile, TestData.Journey(true), TestData.Journey(false),
            total, [TestData.Baseline], TestData.Now);
        Assert.Equal(expected, a.Classification);
        Assert.Equal(total / 2, a.PricePerAdult);
        Assert.Equal(AssessmentSource.ManualBaseline, a.Source);
        Assert.Equal(TestData.Baseline.Thresholds, a.AppliedThresholds);
        Assert.Equal(TestData.Baseline.Assumption, a.BaselineAssumption);
    }

    [Fact]
    public void EquivalentPerAdultPricesClassifyTheSameForDifferentPartySizes()
    {
        var one = AssessmentEvaluator.Evaluate(TestData.Profile with { Adults = 1 }, TestData.Journey(true),
            TestData.Journey(false), 1900, [TestData.Baseline], TestData.Now);
        var two = AssessmentEvaluator.Evaluate(TestData.Profile, TestData.Journey(true), TestData.Journey(false),
            3800, [TestData.Baseline], TestData.Now);
        Assert.Equal(one.Classification, two.Classification);
        Assert.Equal(one.PricePerAdult, two.PricePerAdult);
    }

    [Theory]
    [InlineData(CabinApplicability.AllSegmentsInRequestedCabin, Classification.InsufficientBaseline)]
    [InlineData(CabinApplicability.MainLongHaulSegmentsInRequestedCabin, Classification.InsufficientBaseline)]
    [InlineData(CabinApplicability.MixedCabin, Classification.Deal)]
    public void MixedCabinRequiresExplicitApplicabilityButHasNoPenalty(CabinApplicability policy, Classification expected)
    {
        var result = AssessmentEvaluator.Evaluate(TestData.Profile, TestData.Journey(true),
            TestData.Journey(false, Cabin.Economy), 3000, [TestData.Baseline with { Applicability = policy }], TestData.Now);
        Assert.Equal(expected, result.Classification);
        Assert.Equal(CabinComposition.MixedCabin, result.CabinComposition);
    }

    [Fact]
    public void UnsetOrWrongMarketBaselinesDoNotClassify()
    {
        foreach (var b in new[] { TestData.Baseline with { Thresholds = new(null, null, null) },
            TestData.Baseline with { Destinations = ["CDG"] }, TestData.Baseline with { Currency = "USD" },
            TestData.Baseline with { RequestedCabin = Cabin.Business } })
        {
            var a = AssessmentEvaluator.Evaluate(TestData.Profile, TestData.Journey(true), TestData.Journey(false),
                3000, [b], TestData.Now);
            Assert.Equal(Classification.InsufficientBaseline, a.Classification);
            Assert.Equal(AssessmentSource.None, a.Source);
        }
    }

    [Fact]
    public void UnknownCabinIsNotAssumedToMatch()
    {
        Assert.Equal(CabinComposition.Unknown, AssessmentEvaluator.Describe(TestData.Journey(true, Cabin.Unknown).Segments, Cabin.PremiumEconomy));
    }

    [Fact]
    public void PartialAndUnorderedThresholdsAreRejected()
    {
        foreach (var t in new[] { new Thresholds(1000, null, 4000), new Thresholds(3000, 2000, 4000) })
            Assert.Throws<ScanException>(() => new AppSettings
                { Profile = TestData.Profile, ManualBaselines = [TestData.Baseline with { Thresholds = t }] }.Validate());
    }
}
