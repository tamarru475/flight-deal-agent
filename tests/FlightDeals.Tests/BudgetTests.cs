using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class BudgetTests
{
    [Theory]
    [InlineData(240, 10)]
    [InlineData(200, 50)]
    [InlineData(180, 20)]
    public void ValidPoliciesUseTheSameLimitForReservationAndPacing(int limit, int reserve)
    {
        var settings = new AppSettings { MonthlyCreditLimit = limit, ReserveCredits = reserve, Profile = TestData.Profile };
        settings.Validate();
        var effective = Math.Min(limit, 250 - reserve);
        Assert.Equal(effective, Budget.EffectiveLimit(settings));
        Assert.Equal(Math.Min(limit, 220 - reserve), Budget.EffectiveLimit(settings, 220));
        var previous = new BudgetState(new(2026, 10, 1), effective - 2, 0);
        var last = Budget.Reserve(TestData.Account, previous, settings, TestData.Now);
        Assert.Equal(effective, last.AccountedCredits);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            SchedulePlanner.NextDispatch(last, settings, TestData.Now));
        Assert.Throws<ScanException>(() => Budget.Reserve(TestData.Account, last, settings, TestData.Now));
    }

    [Theory]
    [InlineData(241, 10)]
    [InlineData(240, 9)]
    [InlineData(240, 11)]
    [InlineData(1, 10)]
    [InlineData(2, int.MaxValue)]
    [InlineData(200, -1)]
    public void InvalidPoliciesFailClearly(int limit, int reserve)
    {
        var settings = new AppSettings { MonthlyCreditLimit = limit, ReserveCredits = reserve };
        Assert.Contains("Budget must", Assert.Throws<ScanException>(settings.Validate).Message);
    }

    [Fact]
    public void CalendarChangeWithoutVerifiedRenewalDoesNotResetAccounting()
    {
        var previous = new BudgetState(new(2026, 10, 11), 240, 230);
        var account = TestData.Account with { RenewalDate = previous.PeriodEnd };
        var nextMonth = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        Assert.Throws<ScanException>(() => Budget.Reserve(account, previous, TestData.Settings, nextMonth));
        Assert.Throws<ScanException>(() => Budget.Reserve(account with { RenewalDate = null }, previous,
            TestData.Settings, nextMonth.AddDays(20)));
    }

    [Fact]
    public void AllowsExactlyTwoCreditsBeforeTheReserve()
    {
        var result = Budget.Reserve(TestData.Account with { Usage = 238, Remaining = 12 }, null, TestData.Settings, TestData.Now);
        Assert.Equal(240, result.AccountedCredits);
        Assert.Throws<ScanException>(() => Budget.Reserve(TestData.Account with { Usage = 239, Remaining = 11 }, null, TestData.Settings, TestData.Now));
    }

    [Fact]
    public void LaggingUsageCannotEraseReservationsAcrossRestarts()
    {
        var previous = new BudgetState(new(2026, 10, 1), 238, 194);
        var next = Budget.Reserve(TestData.Account with { Usage = 194, Remaining = 56 }, previous, TestData.Settings, TestData.Now);
        Assert.Equal(240, next.AccountedCredits);
        Assert.Throws<ScanException>(() => Budget.Reserve(TestData.Account with { Usage = 194, Remaining = 56 }, next, TestData.Settings, TestData.Now));
    }

    [Fact]
    public void ExternalUsageCountsAgainstOurCeiling()
    {
        var next = Budget.Reserve(TestData.Account with { Usage = 190, Remaining = 60 }, new(new(2026, 10, 1), 20, 18), TestData.Settings, TestData.Now);
        Assert.Equal(192, next.AccountedCredits);
    }

    [Fact]
    public void NewVerifiedPeriodCanResetOnlyAfterOldPeriodHasPassed()
    {
        var previous = new BudgetState(new(2026, 9, 10), 240, 240);
        Assert.Equal(2, Budget.Reserve(TestData.Account, previous, TestData.Settings, TestData.Now).AccountedCredits);
        Assert.Throws<ScanException>(() => Budget.Reserve(TestData.Account, previous with { PeriodEnd = new(2026, 9, 11) }, TestData.Settings, TestData.Now));
    }

    [Fact]
    public void MissingInconsistentOrPaidQuotaFailsClosed()
    {
        foreach (var account in new[] { TestData.Account with { RenewalDate = null }, TestData.Account with { Plan = "Paid" },
            TestData.Account with { Remaining = 249 }, TestData.Account with { RenewalDate = new(2026, 9, 11) } })
            Assert.Throws<ScanException>(() => Budget.Reserve(account, null, TestData.Settings, TestData.Now));
    }
}
