using System.Text.Json;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using Microsoft.Data.Sqlite;

namespace CalendarBell.Infrastructure.Storage;

/// <summary>Calendar configuration. URLs are stored only as DPAPI ciphertext; an encrypted backup file mirrors the list.</summary>
public sealed class SqliteCalendarRepository : ICalendarRepository
{
    private readonly CalendarBellDatabase _db;
    private readonly DataPaths _paths;

    public SqliteCalendarRepository(CalendarBellDatabase db, DataPaths paths)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public Task<IReadOnlyList<CalendarSource>> GetAllAsync(CancellationToken ct)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT calendar_id, name, encrypted_url, enabled FROM Calendars ORDER BY name COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var list = new List<CalendarSource>();
        while (reader.Read())
        {
            list.Add(new CalendarSource(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
        }

        return Task.FromResult<IReadOnlyList<CalendarSource>>(list);
    }

    public async Task<CalendarSource?> GetAsync(Guid id, CancellationToken ct) =>
        (await GetAllAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.CalendarId == id);

    public async Task SaveAsync(CalendarSource calendar, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        using (var connection = _db.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Calendars (calendar_id, name, encrypted_url, enabled, created_utc, updated_utc)
                VALUES ($id, $name, $url, $enabled, $now, $now)
                ON CONFLICT(calendar_id) DO UPDATE SET
                    name = excluded.name, encrypted_url = excluded.encrypted_url,
                    enabled = excluded.enabled, updated_utc = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$id", calendar.CalendarId.ToString());
            command.Parameters.AddWithValue("$name", calendar.Name);
            command.Parameters.AddWithValue("$url", calendar.EncryptedUrl);
            command.Parameters.AddWithValue("$enabled", calendar.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
            command.ExecuteNonQuery();
        }

        await WriteBackupAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        using (var connection = _db.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Calendars WHERE calendar_id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        }

        await WriteBackupAsync(ct).ConfigureAwait(false);
    }

    /// <summary>After a database reset: re-create calendars (encrypted URLs included) from the backup file.</summary>
    public async Task<int> RestoreFromBackupIfEmptyAsync(CancellationToken ct)
    {
        if ((await GetAllAsync(ct).ConfigureAwait(false)).Count > 0)
        {
            return 0;
        }

        var backup = AtomicFile.ReadWithBackup(_paths.CalendarBackupPath, json => JsonSerializer.Deserialize<List<CalendarSource>>(json));
        if (backup is null)
        {
            return 0;
        }

        foreach (var calendar in backup)
        {
            await SaveAsync(calendar, ct).ConfigureAwait(false);
        }

        return backup.Count;
    }

    private async Task WriteBackupAsync(CancellationToken ct)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);
        AtomicFile.WriteAllText(_paths.CalendarBackupPath, JsonSerializer.Serialize(all));
    }
}

public sealed class SqliteSyncStateStore : ISyncStateStore
{
    private readonly CalendarBellDatabase _db;

    public SqliteSyncStateStore(CalendarBellDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<IReadOnlyList<SyncRecord>> GetAllAsync(CancellationToken ct)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT calendar_id, etag, last_modified, last_attempt_utc, last_success_utc, consecutive_failures,
                   next_refresh_utc, last_state, last_error, last_http_status, upcoming_count
            FROM SyncState
            """;
        using var reader = command.ExecuteReader();
        var list = new List<SyncRecord>();
        while (reader.Read())
        {
            list.Add(new SyncRecord(Guid.Parse(reader.GetString(0)))
            {
                ETag = reader.IsDBNull(1) ? null : reader.GetString(1),
                LastModified = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastAttemptUtc = Db.Time(reader, 3),
                LastSuccessUtc = Db.Time(reader, 4),
                ConsecutiveFailures = reader.GetInt32(5),
                NextRefreshUtc = Db.Time(reader, 6),
                LastState = reader.IsDBNull(7) ? null : reader.GetString(7),
                LastError = reader.IsDBNull(8) ? null : reader.GetString(8),
                LastHttpStatus = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                UpcomingCount = reader.GetInt32(10),
            });
        }

        return Task.FromResult<IReadOnlyList<SyncRecord>>(list);
    }

    public Task SaveAsync(SyncRecord state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncState (calendar_id, etag, last_modified, last_attempt_utc, last_success_utc, consecutive_failures,
                                   next_refresh_utc, last_state, last_error, last_http_status, upcoming_count)
            VALUES ($id, $etag, $lm, $attempt, $success, $failures, $next, $state, $error, $http, $upcoming)
            ON CONFLICT(calendar_id) DO UPDATE SET
                etag = excluded.etag, last_modified = excluded.last_modified, last_attempt_utc = excluded.last_attempt_utc,
                last_success_utc = excluded.last_success_utc, consecutive_failures = excluded.consecutive_failures,
                next_refresh_utc = excluded.next_refresh_utc, last_state = excluded.last_state, last_error = excluded.last_error,
                last_http_status = excluded.last_http_status, upcoming_count = excluded.upcoming_count
            """;
        command.Parameters.AddWithValue("$id", state.CalendarId.ToString());
        command.Parameters.AddWithValue("$etag", (object?)state.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$lm", (object?)state.LastModified ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempt", Db.Value(state.LastAttemptUtc));
        command.Parameters.AddWithValue("$success", Db.Value(state.LastSuccessUtc));
        command.Parameters.AddWithValue("$failures", state.ConsecutiveFailures);
        command.Parameters.AddWithValue("$next", Db.Value(state.NextRefreshUtc));
        command.Parameters.AddWithValue("$state", (object?)state.LastState ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)state.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$http", (object?)state.LastHttpStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$upcoming", state.UpcomingCount);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid calendarId, CancellationToken ct)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SyncState WHERE calendar_id = $id";
        command.Parameters.AddWithValue("$id", calendarId.ToString());
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}

internal static class Db
{
    public static object Value(DateTimeOffset? value) => value is { } v ? v.UtcTicks : DBNull.Value;

    public static DateTimeOffset? Time(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new DateTimeOffset(reader.GetInt64(ordinal), TimeSpan.Zero);

    public static DateTimeOffset RequiredTime(SqliteDataReader reader, int ordinal) => new(reader.GetInt64(ordinal), TimeSpan.Zero);
}
