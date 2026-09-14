using FlightDeals;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace FlightDeals.Tests;

[Collection("PostgreSQL")]
public class ScheduledPostgresTests
{
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
        var settings = configuration.GetSection("FlightDeals").Get<AppSettings>()!;
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
            var europe = await service.Run("europe-economy", default);
            var observation = await store.Observation(europe.ObservationId!.Value, default);
            Assert.Equal(Classification.Deal, observation!.Assessment.Classification);
            Assert.Equal(AssessmentSource.ManualBaseline, observation.Assessment.Source);
            Assert.Equal("akl-europe-economy", observation.Assessment.BaselineId);
            Assert.Equal(3500, observation.TotalPartyPrice);

            ScheduleState schedule;
            await using (var session = await store.OpenSession(default)) schedule = await session.ReadSchedule(default);
            Assert.Equal(clock.Now, schedule.LastScans["europe-economy"]);
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
            return Task.FromResult<FlightOption[]>([new(3500, journey, outbound ? "synthetic-selection" : null)]);
        }
    }
}
