using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FlightDeals;

public sealed class PostgresStore(NpgsqlDataSource database) : IScanStore
{
    public async Task Initialize(CancellationToken ct)
    {
        await using var command = database.CreateCommand("""
            CREATE TABLE IF NOT EXISTS quota_state (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                period_end date NOT NULL, accounted_credits integer NOT NULL CHECK (accounted_credits >= 0),
                provider_usage integer NOT NULL CHECK (provider_usage >= 0));
            CREATE TABLE IF NOT EXISTS scan_runs (
                id uuid PRIMARY KEY, started_at timestamptz NOT NULL, payload jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS observations (
                id uuid PRIMARY KEY, run_id uuid NOT NULL UNIQUE REFERENCES scan_runs(id),
                observed_at timestamptz NOT NULL, payload jsonb NOT NULL);
            CREATE INDEX IF NOT EXISTS scan_runs_started ON scan_runs(started_at DESC);
            CREATE INDEX IF NOT EXISTS observations_observed ON observations(observed_at DESC);
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IScanSession> OpenSession(CancellationToken ct)
    {
        var connection = await database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(741529103)", connection);
            if (await cmd.ExecuteScalarAsync(ct) is not true)
                throw new ScanException("Another scan is running; this trigger did not spend credits.");
            // With the global lock held, any previous Running row is from an interrupted process.
            await using var recover = new NpgsqlCommand("""
                UPDATE scan_runs SET payload = payload || jsonb_build_object(
                    'status','Failed','message','Previous scan interrupted; reservation retained; no retry.',
                    'finishedAt',now()) WHERE payload->>'status'='Running'
                """, connection);
            await recover.ExecuteNonQueryAsync(ct);
            return new Session(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task<ScanRun[]> Runs(CancellationToken ct) => Read<ScanRun>(
        "SELECT payload::text FROM scan_runs ORDER BY started_at DESC LIMIT 50", null, ct);
    public Task<Observation[]> Observations(CancellationToken ct) => Read<Observation>(
        "SELECT payload::text FROM observations ORDER BY observed_at DESC LIMIT 50", null, ct);
    public async Task<ScanRun?> Run(Guid id, CancellationToken ct) => (await Read<ScanRun>(
        "SELECT payload::text FROM scan_runs WHERE id = $1", id, ct)).SingleOrDefault();
    public async Task<Observation?> Observation(Guid id, CancellationToken ct) => (await Read<Observation>(
        "SELECT payload::text FROM observations WHERE id = $1", id, ct)).SingleOrDefault();
    public async Task<BudgetState?> Quota(CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("SELECT period_end, accounted_credits, provider_usage FROM quota_state");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await ReadBudgetRow(reader, ct);
    }
    private static async Task<BudgetState?> ReadBudgetRow(NpgsqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct)) return null;
        return new BudgetState(
            PeriodEnd: reader.GetFieldValue<DateOnly>(0),
            AccountedCredits: reader.GetInt32(1),
            ProviderUsage: reader.GetInt32(2));
    }

    private async Task<T[]> Read<T>(string sql, Guid? id, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand(sql);
        if (id is not null) cmd.Parameters.AddWithValue(id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            var payload = reader.GetString(0);
            items.Add(JsonSerializer.Deserialize<T>(payload, JsonDefaults.Options)!);
        }
        return items.ToArray();
    }

    private sealed class Session(NpgsqlConnection connection) : IScanSession
    {
        public async Task<BudgetState?> ReadBudget(CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand("SELECT period_end, accounted_credits, provider_usage FROM quota_state", connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await ReadBudgetRow(reader, ct);
        }

        public async Task Reserve(BudgetState state, ScanRun run, CancellationToken ct)
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            await using var quota = new NpgsqlCommand("""
                INSERT INTO quota_state(singleton, period_end, accounted_credits, provider_usage) VALUES(true,$1,$2,$3)
                ON CONFLICT(singleton) DO UPDATE SET period_end=$1, accounted_credits=$2, provider_usage=$3
                """, connection, tx);
            quota.Parameters.AddWithValue(state.PeriodEnd);
            quota.Parameters.AddWithValue(state.AccountedCredits);
            quota.Parameters.AddWithValue(state.ProviderUsage);
            await quota.ExecuteNonQueryAsync(ct);
            await using var insert = new NpgsqlCommand("INSERT INTO scan_runs VALUES($1,$2,$3)", connection, tx);
            insert.Parameters.AddWithValue(run.Id);
            insert.Parameters.AddWithValue(run.StartedAt);
            insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(run, JsonDefaults.Options));
            await insert.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }

        public async Task SaveRun(ScanRun run, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand("UPDATE scan_runs SET payload=$2 WHERE id=$1", connection);
            cmd.Parameters.AddWithValue(run.Id);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(run, JsonDefaults.Options));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task Complete(ScanRun run, Observation observation, CancellationToken ct)
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            await using var cmd = new NpgsqlCommand("INSERT INTO observations VALUES($1,$2,$3,$4)", connection, tx);
            cmd.Parameters.AddWithValue(observation.Id);
            cmd.Parameters.AddWithValue(run.Id);
            cmd.Parameters.AddWithValue(observation.ObservedAt);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(observation, JsonDefaults.Options));
            await cmd.ExecuteNonQueryAsync(ct);
            await SaveRun(run, ct);
            await tx.CommitAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(741529103)", connection);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { await connection.DisposeAsync(); }
        }
    }
}
