using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FlightDeals;

public sealed class NotificationStore(NpgsqlDataSource database)
{
    public async Task<NotificationSession?> Open(CancellationToken ct)
    {
        var connection = await database.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(741529104)", connection);
            if (await command.ExecuteScalarAsync(ct) is true) return new(connection);
            await connection.DisposeAsync();
            return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<NotificationRecord[]> Recent(CancellationToken ct)
    {
        await using var command = database.CreateCommand(
            "SELECT payload::text FROM notifications ORDER BY payload->>'createdAt' DESC LIMIT 50");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var records = new List<NotificationRecord>();
        while (await reader.ReadAsync(ct)) records.Add(JsonSerializer.Deserialize<NotificationRecord>(reader.GetString(0), JsonDefaults.Options)!);
        return records.ToArray();
    }
}

// This independent advisory lock serializes notification decisions and delivery, never flight searches.
public sealed class NotificationSession(NpgsqlConnection connection) : IAsyncDisposable
{
    public async Task RecoverInterrupted(CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE notifications SET payload = payload || jsonb_build_object(
                'status','Unknown','reason','Interrupted delivery; acceptance is unknown. Operator review required.')
            WHERE payload->>'status'='Sending'
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<NotificationState> GetOrInitialize(string scope, CancellationToken ct)
    {
        // Database time is the activation boundary. Existing observations are never backfilled.
        await using var insert = new NpgsqlCommand("""
            INSERT INTO notification_profile_state(scope,payload)
            VALUES($1,jsonb_build_object('enabledAt',clock_timestamp())) ON CONFLICT DO NOTHING
            """, connection);
        insert.Parameters.AddWithValue(scope);
        await insert.ExecuteNonQueryAsync(ct);
        return (await Read<NotificationState>(
            "SELECT payload::text FROM notification_profile_state WHERE scope=$1", scope, ct)).Single();
    }

    public Task<NotificationRecord[]> Records(string scope, CancellationToken ct) => Read<NotificationRecord>(
        "SELECT payload::text FROM notifications WHERE scope=$1", scope, ct);

    public async Task<Observation[]> Unprocessed(SearchProfile profile, NotificationState state, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT o.payload::text FROM observations o
            WHERE observed_at > $1 AND payload->'profile'->>'id'=$2
            AND NOT EXISTS (SELECT 1 FROM notifications n WHERE n.observation_id=o.id)
            ORDER BY observed_at,id
            """, connection);
        command.Parameters.AddWithValue(state.EnabledAt);
        command.Parameters.AddWithValue(profile.Id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<Observation>();
        var scope = NotificationPolicy.Scope(profile);
        while (await reader.ReadAsync(ct))
        {
            var observation = JsonSerializer.Deserialize<Observation>(reader.GetString(0), JsonDefaults.Options)!;
            if (NotificationPolicy.Scope(observation.Profile) == scope) result.Add(observation);
        }
        return result.ToArray();
    }

    public async Task<Observation> Observation(Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT payload::text FROM observations WHERE id=$1", connection);
        command.Parameters.AddWithValue(id);
        return JsonSerializer.Deserialize<Observation>((string)(await command.ExecuteScalarAsync(ct))!, JsonDefaults.Options)!;
    }

    public async Task Save(NotificationRecord record, NotificationState state, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var notification = new NpgsqlCommand("""
            INSERT INTO notifications VALUES($1,$2,$3)
            ON CONFLICT(observation_id) DO UPDATE SET payload=$3
            """, connection, transaction);
        notification.Parameters.AddWithValue(record.ObservationId);
        notification.Parameters.AddWithValue(record.Scope);
        notification.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(record, JsonDefaults.Options));
        await notification.ExecuteNonQueryAsync(ct);
        await using var update = new NpgsqlCommand(
            "UPDATE notification_profile_state SET payload=$2 WHERE scope=$1", connection, transaction);
        update.Parameters.AddWithValue(record.Scope);
        update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(state, JsonDefaults.Options));
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<T[]> Read<T>(string sql, string scope, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(scope);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<T>();
        while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonDefaults.Options)!);
        return result.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(741529104)", connection);
            await command.ExecuteNonQueryAsync();
        }
        finally { await connection.DisposeAsync(); }
    }
}
