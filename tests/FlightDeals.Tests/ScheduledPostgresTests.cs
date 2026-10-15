using FlightDeals;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace FlightDeals.Tests;

[Collection("PostgreSQL")]
public class ScheduledPostgresTests
{
    [PostgresFact]
    public async Task RetiredEuropeHistoryRemainsUnchangedAfterLoadingReplacementProfiles()
    {
        var legacyConfiguration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Fixtures/configuration-before-split.json").Build();
        var legacy = FlightDealsConfiguration.Load(legacyConfiguration, AppContext.BaseDirectory);
        var retired = legacy.Profiles.Single(p => p.Search.Id == "europe-economy").Search;
        var oldSettings = new AppSettings { LiveSearchEnabled = true, Profile = retired, ManualBaselines = legacy.ManualBaselines };
        var schema = "test_" + Guid.NewGuid().ToString("N");
        var cs = Environment.GetEnvironmentVariable("TEST_POSTGRES")!;
        await using var admin = NpgsqlDataSource.Create(cs);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema }.ConnectionString;
            await using var database = NpgsqlDataSource.Create(connection);
            var store = new PostgresStore(database);
            await store.Initialize(default);
            var provider = new ProfileProvider();
            var run = await new ScanService(provider, store, oldSettings, new FixedClock()).Run(default);
            var original = await store.Observation(run.ObservationId!.Value, default);
            var originalJson = System.Text.Json.JsonSerializer.Serialize(original, JsonDefaults.Options);
            var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
                { ["FlightDeals:LiveSearchEnabled"] = "true", ["FlightDeals:SchedulerEnabled"] = "true" }).Build();
            var replacement = FlightDealsConfiguration.Load(configuration, AppContext.BaseDirectory);
            await using var restartedDatabase = NpgsqlDataSource.Create(connection);
            var restartedStore = new PostgresStore(restartedDatabase);
            Assert.Null(await new ScanService(provider, restartedStore, replacement, new FixedClock()).RunDue(default));
            var persisted = await restartedStore.Observation(run.ObservationId.Value, default);
            Assert.Equal(originalJson, System.Text.Json.JsonSerializer.Serialize(persisted, JsonDefaults.Options));
            Assert.Equal("europe-economy", persisted!.Profile.Id);
            Assert.Equal(new DateOnly(2027, 5, 1), persisted.Profile.OutboundDate);
            Assert.Equal(new DateOnly(2027, 6, 15), persisted.Profile.ReturnDate);
            Assert.Equal(RunStatus.Completed, (await restartedStore.Run(run.Id, default))!.Status);
            Assert.Equal(2, provider.Calls);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task BaselineRevisionOnlyAffectsNewObservations()
    {
        var settings = HistoricalSamplingTests.Settings();
        var originalBaseline = settings.ManualBaselines[0] with
        {
            Version = 1, Thresholds = new(2000, 2500, 4000), Assumption = "Original manual seed"
        };
        var oldSettings = new AppSettings { LiveSearchEnabled = true, Profile = settings.Profiles[1].Search,
            ManualBaselines = [originalBaseline] };
        var revisedSettings = new AppSettings { LiveSearchEnabled = true, Profile = oldSettings.Profile,
            ManualBaselines = settings.ManualBaselines };
        var schema = "test_" + Guid.NewGuid().ToString("N");
        var cs = Environment.GetEnvironmentVariable("TEST_POSTGRES")!;
        await using var admin = NpgsqlDataSource.Create(cs);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var database = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema }.ConnectionString);
            var store = new PostgresStore(database);
            await store.Initialize(default);
            var provider = new ProfileProvider { Price = 5000 };
            var original = await new ScanService(provider, store, oldSettings, new FixedClock()).Run(default);
            await store.Initialize(default); // Deployment initialization must not rewrite stored assessments.
            var revised = await new ScanService(provider, store, revisedSettings, new FixedClock()).Run(default);
            var originalObservation = await store.Observation(original.ObservationId!.Value, default);
            var revisedObservation = await store.Observation(revised.ObservationId!.Value, default);
            Assert.Equal(1, originalObservation!.Assessment.BaselineVersion);
            Assert.Equal(Classification.Normal, originalObservation.Assessment.Classification);
            Assert.Equal(new Thresholds(2000, 2500, 4000), originalObservation.Assessment.AppliedThresholds);
            Assert.Equal(2, revisedObservation!.Assessment.BaselineVersion);
            Assert.Equal(Classification.Cheap, revisedObservation.Assessment.Classification);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task ConfiguredEuropeScanUsesBaselineAndPersistsPacingAcrossRestart()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FlightDeals:LiveSearchEnabled"] = "true",
                ["FlightDeals:SchedulerEnabled"] = "true"
            }).Build();
        var settings = FlightDealsConfiguration.Load(configuration, AppContext.BaseDirectory);
        settings.Validate();
        Assert.Equal(new[] { "CDG" }, settings.Profiles[1].Search.Destinations);
        var schema = "test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("TEST_POSTGRES")!);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_POSTGRES")!) { SearchPath = schema };
            await using var database = NpgsqlDataSource.Create(connection.ConnectionString);
            var store = new PostgresStore(database);
            await store.Initialize(default);
            var provider = new ProfileProvider();
            var clock = new AdvancingClock();
            var service = new ScanService(provider, store, settings, clock);

            // Explicit Europe manual selection exercises the shipped baseline and advances scheduling.
            var europe = await service.Run("cdg-economy-2027-04-22", default);
            var observation = await store.Observation(europe.ObservationId!.Value, default);
            Assert.Equal(Classification.Deal, observation!.Assessment.Classification);
            Assert.Equal(AssessmentSource.ManualBaseline, observation.Assessment.Source);
            Assert.Equal("akl-europe-economy", observation.Assessment.BaselineId);
            Assert.Equal(3500, observation.TotalPartyPrice);

            ScheduleState schedule;
            await using (var session = await store.OpenSession(default)) schedule = await session.ReadSchedule(default);
            Assert.Equal(clock.Now, schedule.LastScans["cdg-economy-2027-04-22"]);
            Assert.True(schedule.NextDispatch > clock.Now);

            // Use a fresh pool to exercise persisted state rather than an in-memory service instance.
            await using var restartedDatabase = NpgsqlDataSource.Create(connection.ConnectionString);
            var restarted = new ScanService(provider, new PostgresStore(restartedDatabase), settings, clock);
            Assert.Null(await restarted.RunDue(default));
            Assert.Equal(2, provider.Calls);

            clock.Now = schedule.NextDispatch;
            Assert.Equal("tokyo-premium", (await restarted.RunDue(default))!.Profile.Id);
            Assert.Null(await service.RunDue(default));
            Assert.Equal(4, provider.Calls);
            Assert.Equal(4, (await store.Quota(default))!.AccountedCredits);

            // A durable reservation left by a killed process still defers that profile and all dispatch.
            clock.Now = clock.Now.AddDays(3);
            await using (var session = await store.OpenSession(default))
            {
                var interrupted = new ScanRun(Guid.NewGuid(), clock.Now, settings.DefaultProfile, RunStatus.Running, 2, []);
                await session.Reserve(new(new(2026, 10, 1), 6, 0), interrupted, default, clock.Now.AddHours(8));
            }
            Assert.Null(await restarted.RunDue(default));
            Assert.Equal(4, provider.Calls);
            Assert.Equal(6, (await store.Quota(default))!.AccountedCredits);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        public DateTimeOffset Now = TestData.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ProfileProvider : IFlightProvider
    {
        public int Calls;
        public decimal Price = 3500;
        public Task<AccountQuota> GetAccount(CancellationToken ct) => Task.FromResult(TestData.Account);
        public Task<FlightOption[]> Search(SearchProfile profile, string? departureToken, CancellationToken ct)
        {
            Calls++;
            var outbound = departureToken is null;
            var date = outbound ? profile.OutboundDate : profile.ReturnDate;
            var segment = new Segment(
                DepartureAirport: outbound ? profile.Origin : profile.Destinations[0],
                ArrivalAirport: outbound ? profile.Destinations[0] : profile.Origin,
                DepartureLocal: $"{date:yyyy-MM-dd} 10:00", ArrivalLocal: null,
                DurationMinutes: 660, Airline: "Synthetic Air", FlightNumber: "TEST 1",
                Cabin: profile.RequestedCabin, ReportedCabin: profile.RequestedCabin.ToString(), Overnight: null);
            var journey = new Journey(660, [segment], [], null, []);
            return Task.FromResult<FlightOption[]>([new(Price, journey, outbound ? "synthetic-selection" : null)]);
        }
    }
}
