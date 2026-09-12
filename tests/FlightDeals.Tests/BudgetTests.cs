using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class BudgetTests
{
    [Fact]
    public void AllowsExactlyTwoCreditsBeforeTheReserve()
    {
        var result = Budget.Reserve(TestData.Account with { Usage = 198, Remaining = 52 }, null, TestData.Settings, TestData.Now);
        Assert.Equal(200, result.AccountedCredits);
        Assert.Throws<ScanException>(() => Budget.Reserve(TestData.Account with { Usage = 199, Remaining = 51 }, null, TestData.Settings, TestData.Now));
    }

    [Fact]
    public void LaggingUsageCannotEraseReservationsAcrossRestarts()
    {
        var previous = new BudgetState(new(2026, 10, 1), 198, 194);
        var next = Budget.Reserve(TestData.Account with { Usage = 194, Remaining = 56 }, previous, TestData.Settings, TestData.Now);
        Assert.Equal(200, next.AccountedCredits);
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
        var previous = new BudgetState(new(2026, 9, 10), 200, 200);
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
