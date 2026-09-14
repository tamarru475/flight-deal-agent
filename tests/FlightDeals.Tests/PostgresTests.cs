using FlightDeals;
using Npgsql;
using Xunit;

namespace FlightDeals.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_POSTGRES")))
            Skip = "Set TEST_POSTGRES to an isolated test database to run PostgreSQL integration tests.";
    }
}

[Collection("PostgreSQL")]
public class PostgresTests
{
    [PostgresFact]
    public async Task SlicePersistsAcrossConnectionsAndSerializesReservations()
    {
        // Use a unique schema; never drop or truncate the user's database.
        var cs = Environment.GetEnvironmentVariable("TEST_POSTGRES")!;
        var schema = "test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(cs);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema };
            await using var db = NpgsqlDataSource.Create(builder.ConnectionString);
            var store = new PostgresStore(db);
            await store.Initialize(default);
            await store.Initialize(default);
            await using (var session = await store.OpenSession(default))
                await Assert.ThrowsAsync<ScanException>(() => store.OpenSession(default));

            var run = await new ScanService(new FakeProvider(), store, TestData.Settings, new FixedClock()).Run(default);
            Assert.Equal(RunStatus.Completed, run.Status);
            Assert.Equal(2, run.Attempts.Length);
            var restarted = new PostgresStore(db);
            var read = await restarted.Observation(run.ObservationId!.Value, default);
            Assert.NotNull(read);
            Assert.Equal(4200, read.TotalPartyPrice);
            Assert.Equal(Cabin.PremiumEconomy, read.Return.Segments[0].Cabin);
            Assert.Equal(Classification.InsufficientBaseline, read.Assessment.Classification);
            Assert.Equal(RunStatus.Completed, (await restarted.Run(run.Id, default))!.Status);
            Assert.Equal(2, (await restarted.Quota(default))!.AccountedCredits);

            // Simulate a process interruption after reservation, before dispatch.
            Guid interruptedId;
            await using (var session = await restarted.OpenSession(default))
            {
                var interrupted = new ScanRun(Guid.NewGuid(), TestData.Now, TestData.Profile, RunStatus.Running, 2, []);
                interruptedId = interrupted.Id;
                await session.Reserve(new(new(2026, 10, 1), 200, 0), interrupted, default);
            }
            var provider = new FakeProvider();
            await Assert.ThrowsAsync<ScanException>(() => new ScanService(provider, restarted, TestData.Settings, new FixedClock()).Run(default));
            Assert.Equal(0, provider.Calls);
            Assert.Equal(RunStatus.Failed, (await restarted.Run(interruptedId, default))!.Status);
            Assert.Equal(200, (await restarted.Quota(default))!.AccountedCredits);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
