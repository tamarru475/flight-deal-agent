using FlightDeals;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using Xunit;

namespace FlightDeals.Tests;

[Collection("PostgreSQL")]
public class WeeklyDigestPostgresTests
{
    [PostgresFact]
    public async Task FirstActivationWaitsUntilMondayThenSendsAQuietWeek()
    {
        await using var test = await DigestTestContext.Create();
        await test.Process();
        Assert.Equal(0, test.Sender.Calls);
        Assert.Equal(0, test.Account.Calls);
        var scheduled = Assert.Single(await test.Store.Recent(default));
        Assert.Equal(DigestStatus.Scheduled, scheduled.Status);
        Assert.Equal(WeeklyDigestSchedule.Next(test.Clock.Now), scheduled.Period);
        test.Clock.Now = scheduled.Period.DueAt;
        test.Sender.BeforeSend = async () =>
            Assert.Equal(DigestStatus.Sending, Assert.Single(await test.Store.Recent(default)).Status);
        await test.Process();
        Assert.Equal(1, test.Sender.Calls);
        Assert.Equal(1, test.Account.Calls);
        Assert.Contains("No observations", test.Sender.LastEmail!.Body);
        Assert.Equal(DigestStatus.Sent, Assert.Single(await test.Store.Recent(default)).Status);
        await test.AssertImmediateStateUntouched();
    }

    [PostgresFact]
    public async Task RestartDoesNotResendTheSameWeek()
    {
        await using var test = await DigestTestContext.Create();
        await test.ActivateAndAdvanceToDue();
        await test.Process();
        await test.Process(); // Each call creates a new processor.
        Assert.Equal(1, test.Sender.Calls);
        Assert.Equal(1, test.Account.Calls);
        Assert.Single(await test.Store.Recent(default));
    }

    [PostgresFact]
    public async Task AdvisoryLockPreventsConcurrentDigestProcessing()
    {
        await using var test = await DigestTestContext.Create();
        await test.ActivateAndAdvanceToDue();
        await using var held = await test.Store.Open(default);
        Assert.NotNull(held);
        Assert.Null(await test.Store.Open(default));
        await test.Process();
        Assert.Equal(0, test.Sender.Calls);
        Assert.Equal(0, test.Account.Calls);
    }

    [PostgresFact]
    public async Task DowntimeSendsOnlyLatestDueWeekAndSkipsOlderScheduledWeek()
    {
        await using var test = await DigestTestContext.Create();
        await test.ActivateAndAdvanceToDue();
        test.Clock.Now = test.Clock.Now.AddDays(22);
        await test.Process();
        await test.Process();
        var deliveries = await test.Store.Recent(default);
        Assert.Equal(2, deliveries.Length);
        Assert.Equal(WeeklyDigestSchedule.LatestDue(test.Clock.Now), deliveries[0].Period);
        Assert.Equal(DigestStatus.Sent, deliveries[0].Status);
        Assert.Equal(DigestStatus.Skipped, deliveries[1].Status);
        Assert.Equal(1, test.Sender.Calls);
    }

    [PostgresFact]
    public async Task UnknownDeliveryIsNotRetriedAndDoesNotBlockFollowingWeek()
    {
        await using var test = await DigestTestContext.Create();
        await test.ActivateAndAdvanceToDue();
        test.Sender.Outcome = DeliveryStatus.Unknown;
        await test.Process();
        await test.Process();
        Assert.Equal(1, test.Sender.Calls);
        Assert.Equal(DigestStatus.Unknown, Assert.Single(await test.Store.Recent(default)).Status);
        test.Clock.Now = test.Clock.Now.AddDays(7);
        test.Sender.Outcome = DeliveryStatus.Sent;
        await test.Process();
        Assert.Equal(2, test.Sender.Calls);
        Assert.Equal(DigestStatus.Sent, (await test.Store.Recent(default))[0].Status);
    }

    [PostgresFact]
    public async Task InterruptedSendingRecoversAsUnknownWithoutAttemptingDelivery()
    {
        await using var test = await DigestTestContext.Create();
        await test.ActivateAndAdvanceToDue();
        await using (var session = await test.Store.Open(default))
        {
            var interrupted = await session!.GetOrCreate(WeeklyDigestSchedule.LatestDue(test.Clock.Now), default);
            await session.Save(interrupted with { Status = DigestStatus.Sending }, default);
        }
        await test.Process();
        await test.Process();
        Assert.Equal(0, test.Sender.Calls);
        Assert.Equal(0, test.Account.Calls);
        Assert.Equal(DigestStatus.Unknown, Assert.Single(await test.Store.Recent(default)).Status);
    }

    [PostgresFact]
    public async Task FailedDeliveryIsNotRetriedAndDoesNotBlockFollowingWeek()
    {
        await using var test = await DigestTestContext.Create();
        await test.ActivateAndAdvanceToDue();
        test.Sender.Outcome = DeliveryStatus.Failed;
        await test.Process();
        await test.Process();
        Assert.Equal(1, test.Sender.Calls);
        Assert.Equal(DigestStatus.Failed, Assert.Single(await test.Store.Recent(default)).Status);
        test.Clock.Now = test.Clock.Now.AddDays(7);
        test.Sender.Outcome = DeliveryStatus.Sent;
        await test.Process();
        Assert.Equal(2, test.Sender.Calls);
        Assert.Equal(DigestStatus.Sent, (await test.Store.Recent(default))[0].Status);
    }

    [PostgresFact]
    public async Task UnavailableAccountDoesNotPreventDigestDelivery()
    {
        await using var test = await DigestTestContext.Create();
        await test.ActivateAndAdvanceToDue();
        test.Account.Fail = true;
        await test.Process();
        Assert.Equal(1, test.Sender.Calls);
        Assert.Contains("Provider usage: unavailable", test.Sender.LastEmail!.Body);
    }

    [PostgresFact]
    public async Task DisabledDigestNeitherInitializesNorProcessesExistingSchedule()
    {
        await using var test = await DigestTestContext.Create();
        await test.Process(enabled: false);
        Assert.Empty(await test.Store.Recent(default));
        await test.ActivateAndAdvanceToDue();
        await test.Process(enabled: false);
        Assert.Equal(DigestStatus.Scheduled, Assert.Single(await test.Store.Recent(default)).Status);
        Assert.Equal(0, test.Sender.Calls);
        Assert.Equal(0, test.Account.Calls);
        await test.AssertImmediateStateUntouched();
    }

    [PostgresFact]
    public async Task FullWeekReadExceedsRecent50WindowAndExcludesPeriodEnd()
    {
        await using var test = await DigestTestContext.Create();
        var period = WeeklyDigestTests.Period;
        for (var i = 0; i < 53; i++)
        {
            var observation = WeeklyDigestTests.Fare(1, 3500) with
            {
                ObservedAt = i == 52 ? period.End : period.Start.AddMinutes(i)
            };
            await test.InsertObservation(observation);
        }
        await using var session = await test.Store.Open(default);
        var data = await session!.ReadData(period, default);
        Assert.Equal(52, data.Observations.Length);
        Assert.Equal(52, data.Runs.Length);
        Assert.Empty(data.Notifications);
    }

    private sealed class DigestTestContext(NpgsqlDataSource admin, NpgsqlDataSource database, string schema) : IAsyncDisposable
    {
        public WeeklyDigestStore Store { get; } = new(database);
        public DigestSender Sender { get; } = new();
        public AccountOnlyProvider Account { get; } = new();
        public DigestClock Clock { get; } = new() { Now = DateTimeOffset.Parse("2026-09-22T00:00:00Z") };

        public static async Task<DigestTestContext> Create()
        {
            var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES")!;
            var schema = "test_" + Guid.NewGuid().ToString("N");
            var admin = NpgsqlDataSource.Create(connectionString);
            var database = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString);
            var context = new DigestTestContext(admin, database, schema);
            try
            {
                await using var create = admin.CreateCommand($"CREATE SCHEMA {schema}");
                await create.ExecuteNonQueryAsync();
                await new PostgresStore(database).Initialize(default);
                await new PostgresStore(database).Initialize(default);
                return context;
            }
            catch { await context.DisposeAsync(); throw; }
        }

        public Task Process(bool enabled = true) => new WeeklyDigestProcessor(Store,
            new WeeklyDigestSettings { Enabled = enabled }, TestData.Settings, Account, Sender, Clock).Process(default);

        public async Task ActivateAndAdvanceToDue()
        {
            await Process();
            Clock.Now = Assert.Single(await Store.Recent(default)).Period.DueAt;
        }

        public async Task AssertImmediateStateUntouched()
        {
            await using var count = database.CreateCommand("SELECT (SELECT count(*) FROM notifications)+(SELECT count(*) FROM notification_profile_state)+(SELECT count(*) FROM scan_runs)");
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }

        public async Task InsertObservation(Observation observation)
        {
            var run = new ScanRun(observation.RunId, observation.ObservedAt, observation.Profile, RunStatus.Completed, 2,
                [new(1, "Outbound", observation.ObservedAt, "ResponseReceived")], observation.Id, FinishedAt: observation.ObservedAt);
            await using var insert = database.CreateCommand("""
                WITH inserted AS (INSERT INTO scan_runs VALUES($1,$2,$3) RETURNING id)
                INSERT INTO observations SELECT $4,id,$2,$5 FROM inserted
                """);
            insert.Parameters.AddWithValue(run.Id);
            insert.Parameters.AddWithValue(run.StartedAt);
            insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(run, JsonDefaults.Options));
            insert.Parameters.AddWithValue(observation.Id);
            insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(observation, JsonDefaults.Options));
            await insert.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await database.DisposeAsync();
            try
            {
                await using var drop = admin.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
            finally { await admin.DisposeAsync(); }
        }
    }

    private sealed class DigestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DigestSender : IWeeklyDigestSender
    {
        public int Calls { get; private set; }
        public DeliveryStatus Outcome { get; set; } = DeliveryStatus.Sent;
        public NotificationEmail? LastEmail { get; private set; }
        public Func<Task>? BeforeSend { get; set; }
        public async Task<DeliveryStatus> SendDigest(DigestDelivery delivery, CancellationToken ct)
        {
            Calls++;
            LastEmail = delivery.Email;
            if (BeforeSend is not null) await BeforeSend();
            return Outcome;
        }
    }

    private sealed class AccountOnlyProvider : IFlightProvider
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<AccountQuota> GetAccount(CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("Fake account endpoint unavailable");
            return Task.FromResult(TestData.Account);
        }
        public Task<FlightOption[]> Search(SearchProfile profile, string? departureToken, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("Digest must never search for flights.");
    }
}
