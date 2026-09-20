using System.Text.Json;
using FlightDeals;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace FlightDeals.Tests;

[Collection("PostgreSQL")]
public class NotificationPostgresTests
{
    [PostgresFact]
    public async Task ActivationSuppressionDeliveryAndRestartRecoveryAreDurable()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_POSTGRES")!;
        var schema = "test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(cs);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema }.ConnectionString);
            await new PostgresStore(db).Initialize(default);
            await new PostgresStore(db).Initialize(default);
            var store = new NotificationStore(db);
            var sender = new FakeSender();
            var settings = new NotificationSettings { Enabled = true };
            NotificationProcessor Processor() => new(store, settings, TestData.Settings, sender, TimeProvider.System);

            await Insert(db, NotificationTests.Fare());
            await Processor().Process(default); // First enable skips the existing observation.
            Assert.Empty(await store.Recent(default));
            Assert.Equal(0, sender.Calls);
            await using (var held = await store.Open(default)) Assert.Null(await store.Open(default));

            var first = NotificationTests.Fare();
            await Insert(db, first);
            sender.BeforeSend = async record =>
                Assert.Equal(DeliveryStatus.Sending, (await store.Recent(default)).Single(r => r.ObservationId == record.ObservationId).Status);
            await Processor().Process(default);
            Assert.Equal(1, sender.Calls);
            Assert.Equal(DeliveryStatus.Sent, (await store.Recent(default)).Single().Status);
            await Processor().Process(default); // A new processor simulates a restart.
            Assert.Equal(1, sender.Calls);

            await Insert(db, NotificationTests.Fare(price: 2390));
            await Processor().Process(default);
            Assert.Equal(1, sender.Calls);
            Assert.Contains(await store.Recent(default), r => r.Status == DeliveryStatus.Suppressed);

            await Insert(db, NotificationTests.Fare(price: 2160));
            await Processor().Process(default);
            Assert.Equal(2, sender.Calls); // Exactly 10% below last sent, not below last observed.

            sender.Outcome = DeliveryStatus.Unknown;
            await Insert(db, NotificationTests.Fare(Classification.Deal, 1900));
            await Processor().Process(default);
            Assert.Equal(3, sender.Calls);
            await Insert(db, NotificationTests.Fare(Classification.Deal, 1500));
            await Processor().Process(default);
            Assert.Equal(3, sender.Calls); // Unknown blocks future alerts, including stronger fares.

            // Simulate process death after committing Sending but before committing the SMTP result.
            var interrupted = NotificationTests.Fare();
            await Insert(db, interrupted);
            var scope = NotificationPolicy.Scope(TestData.Profile);
            await using (var session = await store.Open(default))
            {
                var state = await session!.GetOrInitialize(scope, default);
                Assert.Equal(2160, state.LastNotifiedPricePerAdult);
                await session.Save(new(interrupted.Id, scope, DeliveryStatus.Sending, "simulated interruption",
                    "<interrupted@local>", DateTimeOffset.UtcNow), state, default);
            }
            await Processor().Process(default);
            Assert.Equal(DeliveryStatus.Unknown, (await store.Recent(default)).Single(r => r.ObservationId == interrupted.Id).Status);
            Assert.Equal(3, sender.Calls);

            // Disabled processing does not touch even an unavailable/uninitialized store or send mail.
            await new NotificationProcessor(store, new(), TestData.Settings, sender, TimeProvider.System).Process(default);
            Assert.Equal(3, sender.Calls);

            // A definite failure also holds subsequent alerts, without retrying the failed observation.
            var otherProfile = TestData.Profile with { Id = "definite-failure" };
            var otherSettings = new AppSettings { Profile = otherProfile };
            var failedSender = new FakeSender { Outcome = DeliveryStatus.Failed };
            var failedProcessor = new NotificationProcessor(store, settings, otherSettings, failedSender, TimeProvider.System);
            await failedProcessor.Process(default);
            await Insert(db, NotificationTests.Fare() with { Profile = otherProfile });
            await failedProcessor.Process(default);
            await Insert(db, NotificationTests.Fare(Classification.Deal, 1500) with { Profile = otherProfile });
            await failedProcessor.Process(default);
            Assert.Equal(1, failedSender.Calls);
            Assert.Contains(await store.Recent(default), r => r.Status == DeliveryStatus.Failed);

            // Restart before the first delivery attempt safely dispatches Pending exactly once.
            var pendingProfile = TestData.Profile with { Id = "pending-restart" };
            var pendingScope = NotificationPolicy.Scope(pendingProfile);
            var pendingFare = NotificationTests.Fare() with { Profile = pendingProfile };
            await Insert(db, pendingFare);
            await using (var session = await store.Open(default))
            {
                var state = await session!.GetOrInitialize(pendingScope, default);
                await session.Save(new(pendingFare.Id, pendingScope, DeliveryStatus.Pending, "eligible",
                    "<pending@local>", DateTimeOffset.UtcNow), state, default);
            }
            var pendingSender = new FakeSender();
            var pendingProcessor = new NotificationProcessor(store, settings,
                new AppSettings { Profile = pendingProfile }, pendingSender, TimeProvider.System);
            await pendingProcessor.Process(default);
            await pendingProcessor.Process(default);
            Assert.Equal(1, pendingSender.Calls);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task Insert(NpgsqlDataSource db, Observation observation)
    {
        await using var command = db.CreateCommand("""
            WITH inserted AS (INSERT INTO scan_runs VALUES($1,$2,'{}') RETURNING id)
            INSERT INTO observations SELECT $3,id,$2,$4 FROM inserted
            """);
        command.Parameters.AddWithValue(observation.RunId);
        command.Parameters.AddWithValue(observation.ObservedAt);
        command.Parameters.AddWithValue(observation.Id);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(observation, JsonDefaults.Options));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FakeSender : INotificationSender
    {
        public int Calls { get; private set; }
        public DeliveryStatus Outcome { get; set; } = DeliveryStatus.Sent;
        public Func<NotificationRecord, Task>? BeforeSend { get; set; }
        public async Task<DeliveryStatus> Send(NotificationRecord notification, NotificationEmail email, CancellationToken ct)
        {
            Calls++;
            if (BeforeSend is not null) await BeforeSend(notification);
            return Outcome;
        }
    }
}
