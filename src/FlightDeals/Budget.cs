namespace FlightDeals;

public sealed record BudgetState(DateOnly PeriodEnd, int AccountedCredits, int ProviderUsage);

public static class Budget
{
    public const int CreditsPerScan = 2;

    public static BudgetState Reserve(AccountQuota account, BudgetState? previous, AppSettings settings, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (account.Status != "Active" || account.Plan != "Free Plan" || account.Allowance != 250
            || account.Usage < 0 || account.Remaining < 0 || account.Usage + account.Remaining != 250
            || account.RenewalDate is not { } renewal || renewal <= today)
            throw new ScanException("Free account quota or renewal period cannot be verified; scan paused.");
        if (previous is not null && previous.PeriodEnd != renewal
            && (previous.PeriodEnd >= today || renewal < previous.PeriodEnd))
            throw new ScanException("Unexpected quota-period change; scan paused.");
        var accounted = previous?.PeriodEnd == renewal ? previous.AccountedCredits : 0;
        // Never reduce our durable upper bound because account usage lags or a search was cached.
        var current = Math.Max(accounted, account.Usage);
        var ceiling = Math.Min(settings.MonthlyCreditLimit, account.Allowance - settings.ReserveCredits);
        if (current + CreditsPerScan > ceiling || account.Remaining - CreditsPerScan < settings.ReserveCredits)
            throw new ScanException("Scan would exceed the operating budget or touch the reserve.");
        return new(renewal, current + CreditsPerScan, account.Usage);
    }
}
