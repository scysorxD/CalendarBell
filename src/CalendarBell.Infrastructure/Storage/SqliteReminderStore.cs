using System.Text.Json;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;

namespace CalendarBell.Infrastructure.Storage;

/// <summary>
/// Reminder state, normalized occurrence cache and toast batches (spec §6.3). Only fields needed for reminders are
/// stored: no raw ICS, DESCRIPTION, ATTENDEE or meeting links; titles only when Privacy mode is off.
/// </summary>
public sealed class SqliteReminderStore : IReminderStore, IToastBatchStore
{
    private readonly CalendarBellDatabase _db;

    public SqliteReminderStore(CalendarBellDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<ReminderSnapshot> LoadAsync(CancellationToken ct)
    {
        using var connection = _db.OpenConnection();
        var records = new List<ReminderRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT calendar_id, occurrence_key, fingerprint, event_start_utc, initial_due_utc, initial_sent, snoozed_until_utc,
                       snooze_minutes, dismissed, cancelled, last_notified_utc, last_toast_tag, last_toast_group, state_version
                FROM Reminders
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                records.Add(new ReminderRecord(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    Db.RequiredTime(reader, 3),
                    Db.RequiredTime(reader, 4),
                    reader.GetInt64(5) != 0,
                    Db.Time(reader, 6),
                    reader.GetInt64(8) != 0,
                    reader.GetInt64(9) != 0,
                    Db.Time(reader, 10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.GetInt64(13))
                {
                    SnoozeMinutesBefore = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                });
            }
        }

        var occurrences = new List<EventOccurrence>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT calendar_id, occurrence_key, start_utc, end_utc, availability, is_cancelled, title, fingerprint FROM OccurrencesCache";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                occurrences.Add(new EventOccurrence(
                    Guid.Parse(reader.GetString(0)),
                    string.Empty, // the UID is not persisted; the key is an opaque hash of it
                    reader.GetString(1),
                    Db.RequiredTime(reader, 2),
                    Db.RequiredTime(reader, 3),
                    reader.GetString(4),
                    IsAllDay: false,
                    reader.GetInt64(5) != 0,
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7)));
            }
        }

        return Task.FromResult(new ReminderSnapshot(records, occurrences));
    }

    public Task SaveAsync(IReadOnlyList<ReminderRecord> records, IReadOnlyList<EventOccurrence> occurrences, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(occurrences);
        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.UtcTicks;
        foreach (var r in records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Reminders (calendar_id, occurrence_key, fingerprint, event_start_utc, initial_due_utc, initial_sent,
                    snoozed_until_utc, snooze_minutes, dismissed, cancelled, last_notified_utc, last_toast_tag, last_toast_group,
                    state_version, updated_utc)
                VALUES ($cal, $key, $fp, $start, $due, $sent, $snoozed, $minutes, $dismissed, $cancelled, $notified, $tag, $group, $version, $now)
                ON CONFLICT(calendar_id, occurrence_key) DO UPDATE SET
                    fingerprint = excluded.fingerprint, event_start_utc = excluded.event_start_utc, initial_due_utc = excluded.initial_due_utc,
                    initial_sent = excluded.initial_sent, snoozed_until_utc = excluded.snoozed_until_utc, snooze_minutes = excluded.snooze_minutes,
                    dismissed = excluded.dismissed, cancelled = excluded.cancelled, last_notified_utc = excluded.last_notified_utc,
                    last_toast_tag = excluded.last_toast_tag, last_toast_group = excluded.last_toast_group,
                    state_version = excluded.state_version, updated_utc = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$cal", r.CalendarId.ToString());
            command.Parameters.AddWithValue("$key", r.OccurrenceKey);
            command.Parameters.AddWithValue("$fp", r.RevisionFingerprint);
            command.Parameters.AddWithValue("$start", r.EventStartUtc.UtcTicks);
            command.Parameters.AddWithValue("$due", r.InitialDueUtc.UtcTicks);
            command.Parameters.AddWithValue("$sent", r.InitialNotificationSent ? 1 : 0);
            command.Parameters.AddWithValue("$snoozed", Db.Value(r.SnoozedUntilUtc));
            command.Parameters.AddWithValue("$minutes", (object?)r.SnoozeMinutesBefore ?? DBNull.Value);
            command.Parameters.AddWithValue("$dismissed", r.Dismissed ? 1 : 0);
            command.Parameters.AddWithValue("$cancelled", r.Cancelled ? 1 : 0);
            command.Parameters.AddWithValue("$notified", Db.Value(r.LastNotifiedUtc));
            command.Parameters.AddWithValue("$tag", (object?)r.LastToastTag ?? DBNull.Value);
            command.Parameters.AddWithValue("$group", (object?)r.LastToastGroup ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", r.StateVersion);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        foreach (var o in occurrences)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO OccurrencesCache (calendar_id, occurrence_key, start_utc, end_utc, availability, is_cancelled, title, fingerprint)
                VALUES ($cal, $key, $start, $end, $availability, $cancelled, $title, $fp)
                ON CONFLICT(calendar_id, occurrence_key) DO UPDATE SET
                    start_utc = excluded.start_utc, end_utc = excluded.end_utc, availability = excluded.availability,
                    is_cancelled = excluded.is_cancelled, title = excluded.title, fingerprint = excluded.fingerprint
                """;
            command.Parameters.AddWithValue("$cal", o.CalendarId.ToString());
            command.Parameters.AddWithValue("$key", o.OccurrenceKey);
            command.Parameters.AddWithValue("$start", o.StartUtc.UtcTicks);
            command.Parameters.AddWithValue("$end", o.EndUtc.UtcTicks);
            command.Parameters.AddWithValue("$availability", o.Availability);
            command.Parameters.AddWithValue("$cancelled", o.IsCancelled ? 1 : 0);
            command.Parameters.AddWithValue("$title", (object?)o.PrivacySafeTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$fp", o.RevisionFingerprint);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task DeleteCalendarDataAsync(Guid calendarId, CancellationToken ct)
    {
        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "Reminders", "OccurrencesCache" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE calendar_id = $id";
            command.Parameters.AddWithValue("$id", calendarId.ToString());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task PruneAsync(DateTimeOffset olderThanUtc, CancellationToken ct)
    {
        using var connection = _db.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var (table, column) in new[] { ("Reminders", "event_start_utc"), ("OccurrencesCache", "start_utc") })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE {column} < $cutoff";
            command.Parameters.AddWithValue("$cutoff", olderThanUtc.UtcTicks);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync(ToastBatchRecord batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO ToastBatches (batch_id, grp, occurrence_keys, created_utc, expires_utc)
            VALUES ($id, $group, $keys, $created, $expires)
            """;
        command.Parameters.AddWithValue("$id", batch.BatchId);
        command.Parameters.AddWithValue("$group", batch.Group);
        command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(batch.OccurrenceKeys));
        command.Parameters.AddWithValue("$created", batch.CreatedUtc.UtcTicks);
        command.Parameters.AddWithValue("$expires", batch.ExpiresUtc.UtcTicks);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<ToastBatchRecord?> GetBatchAsync(string batchId, CancellationToken ct)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT batch_id, grp, occurrence_keys, created_utc, expires_utc FROM ToastBatches WHERE batch_id = $id";
        command.Parameters.AddWithValue("$id", batchId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return Task.FromResult<ToastBatchRecord?>(null);
        }

        var keys = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [];
        return Task.FromResult<ToastBatchRecord?>(new ToastBatchRecord(reader.GetString(0), reader.GetString(1), keys, Db.RequiredTime(reader, 3), Db.RequiredTime(reader, 4)));
    }

    public Task PruneBatchesAsync(DateTimeOffset olderThanUtc, CancellationToken ct)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ToastBatches WHERE expires_utc < $cutoff";
        command.Parameters.AddWithValue("$cutoff", olderThanUtc.UtcTicks);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
