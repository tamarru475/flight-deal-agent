using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FlightDeals;

public enum DigestStatus { Scheduled, Sending, Sent, Failed, Unknown, Skipped }
public sealed record DigestDelivery(DigestPeriod Period, DigestStatus Status, string MessageId,
    NotificationEmail? Email = null, DateTimeOffset? GeneratedAt = null,
    DateTimeOffset? AttemptedAt = null, DateTimeOffset? SentAt = null, string? Reason = null);

public sealed class WeeklyDigestStore(NpgsqlDataSource database)
{
    public async Task<WeeklyDigestSession?> Open(CancellationToken ct)
    {
        var connection = await database.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(741529105)", connection);
            if (await command.ExecuteScalarAsync(ct) is true) return new(connection);
            await connection.DisposeAsync();
            return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<DigestDelivery[]> Recent(CancellationToken ct)
    {
        await using var command = database.CreateCommand("SELECT payload::text FROM weekly_digests ORDER BY period_start DESC LIMIT 20");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var deliveries = new List<DigestDelivery>();
        while (await reader.ReadAsync(ct)) deliveries.Add(JsonSerializer.Deserialize<DigestDelivery>(reader.GetString(0), JsonDefaults.Options)!);
        return deliveries.ToArray();
    }
}

// A separate lock prevents duplicate digest sends without blocking scans or immediate alerts.
public sealed class WeeklyDigestSession(NpgsqlConnection connection) : IAsyncDisposable
{
    public async Task<DigestDelivery> Initialize(DateTimeOffset now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT payload::text FROM weekly_digests ORDER BY period_start LIMIT 1", connection);
        var existing = await command.ExecuteScalarAsync(ct);
        if (existing is string payload) return JsonSerializer.Deserialize<DigestDelivery>(payload, JsonDefaults.Options)!;
        // The first scheduled row is also the durable activation boundary. Never recreate it on restart.
        return await GetOrCreate(WeeklyDigestSchedule.Next(now), ct);
    }

    public async Task<DigestDelivery> GetOrCreate(DigestPeriod period, CancellationToken ct)
    {
        var delivery = new DigestDelivery(period, DigestStatus.Scheduled,
            $"<weekly-{period.Start:yyyyMMddTHHmmssZ}@flight-deal-agent.local>");
        await using var insert = new NpgsqlCommand("""
            INSERT INTO weekly_digests VALUES($1,$2,$3) ON CONFLICT DO NOTHING
            """, connection);
        insert.Parameters.AddWithValue(period.Start);
        insert.Parameters.AddWithValue(period.End);
        insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(delivery, JsonDefaults.Options));
        await insert.ExecuteNonQueryAsync(ct);
        await using var read = new NpgsqlCommand("SELECT payload::text FROM weekly_digests WHERE period_start=$1", connection);
        read.Parameters.AddWithValue(period.Start);
        return JsonSerializer.Deserialize<DigestDelivery>((string)(await read.ExecuteScalarAsync(ct))!, JsonDefaults.Options)!;
    }

    public async Task RecoverAndSkipOlder(DigestPeriod latest, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE weekly_digests SET payload=payload || jsonb_build_object(
                'status','Unknown','reason','Interrupted delivery; SMTP acceptance unknown. No automatic retry.')
            WHERE payload->>'status'='Sending'
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
        await using var skip = new NpgsqlCommand("""
            UPDATE weekly_digests SET payload=payload || jsonb_build_object(
                'status','Skipped','reason','Superseded by latest completed week; no backlog delivery.')
            WHERE period_start < $1 AND payload->>'status'='Scheduled'
            """, connection);
        skip.Parameters.AddWithValue(latest.Start);
        await skip.ExecuteNonQueryAsync(ct);
    }

    public async Task Save(DigestDelivery delivery, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("UPDATE weekly_digests SET payload=$2 WHERE period_start=$1", connection);
        command.Parameters.AddWithValue(delivery.Period.Start);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(delivery, JsonDefaults.Options));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Digest claim missing.");
    }

    public async Task<DigestData> ReadData(DigestPeriod period, CancellationToken ct)
    {
        // Freeze a consistent view, but release the transaction before the account or SMTP calls.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var observations = await Read<Observation>("""
            SELECT payload::text FROM observations WHERE observed_at >= $1 AND observed_at < $2
            ORDER BY observed_at,id
            """, period, ct);
        var runs = await Read<ScanRun>("""
            SELECT payload::text FROM scan_runs WHERE started_at < $2 AND
            (started_at >= $1 OR EXISTS (SELECT 1 FROM jsonb_array_elements(payload->'attempts') a
                WHERE (a->>'startedAt')::timestamptz >= $1 AND (a->>'startedAt')::timestamptz < $2))
            ORDER BY started_at,id
            """, period, ct);
        var notifications = await Read<NotificationRecord>("""
            SELECT payload::text FROM notifications WHERE
            ((payload->>'createdAt')::timestamptz >= $1 AND (payload->>'createdAt')::timestamptz < $2) OR
            ((payload->>'sentAt')::timestamptz >= $1 AND (payload->>'sentAt')::timestamptz < $2)
            """, period, ct);
        await using var quota = new NpgsqlCommand("""
            SELECT json_build_object('periodEnd',period_end,'accountedCredits',accounted_credits,
                'providerUsage',provider_usage)::text FROM quota_state
            """, connection, transaction);
        var value = await quota.ExecuteScalarAsync(ct);
        var budget = value is string json ? JsonSerializer.Deserialize<BudgetState>(json, JsonDefaults.Options) : null;
        await transaction.CommitAsync(ct);
        return new(observations, runs, notifications, budget);
    }

    private async Task<T[]> Read<T>(string sql, DigestPeriod period, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(period.Start);
        command.Parameters.AddWithValue(period.End);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<T>();
        while (await reader.ReadAsync(ct)) items.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonDefaults.Options)!);
        return items.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(741529105)", connection);
            await command.ExecuteNonQueryAsync();
        }
        finally { await connection.DisposeAsync(); }
    }
}
