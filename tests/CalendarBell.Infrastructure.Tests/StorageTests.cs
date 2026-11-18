using System.Text;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Services;
using CalendarBell.Infrastructure.Security;
using CalendarBell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace CalendarBell.Infrastructure.Tests;

public sealed class StorageTests : IDisposable
{
    private const string SecretUrl = "https://calendar-a.example/owa/calendar/abc/reachcalendar.ics?token=SECRET-TOKEN-42";
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CalendarB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 10, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "CalendarBellTests", Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;

    public StorageTests()
    {
        _paths = new DataPaths(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CalendarBellDatabase OpenDatabase() => CalendarBellDatabase.Open(_paths);

    private static EventOccurrence Occ(string key, Guid calendarId, string? title = null) =>
        new(calendarId, "uid-" + key, key, Start, Start.AddMinutes(30), Availability.Busy, false, false, title, "fp-" + key);

    [Fact]
    public void Database_is_created_with_schema_version_and_wal()
    {
        using var db = OpenDatabase();

        Assert.True(File.Exists(_paths.DatabasePath));
        Assert.Equal(CalendarBellDatabase.CurrentSchemaVersion, db.SchemaVersion);
        Assert.Equal("wal", db.JournalMode, ignoreCase: true);
    }

    [Fact]
    public async Task Calendars_round_trip_with_encrypted_url()
    {
        using var db = OpenDatabase();
        var repo = new SqliteCalendarRepository(db, _paths);
        var protector = new DpapiUrlProtector();

        await repo.SaveAsync(new CalendarSource(CalendarA, "Calendar A", protector.Protect(SecretUrl), true), Ct);
        await repo.SaveAsync(new CalendarSource(CalendarB, "Calendar B", protector.Protect("https://calendar-b.example/b.ics"), false), Ct);
        var all = await repo.GetAllAsync(Ct);

        Assert.Equal(["Calendar A", "Calendar B"], all.Select(c => c.Name));
        Assert.Equal(SecretUrl, protector.Unprotect(all[0].EncryptedUrl));
        Assert.False(all[1].Enabled);
    }

    [Fact]
    public async Task Renaming_keeps_the_calendar_id()
    {
        using var db = OpenDatabase();
        var repo = new SqliteCalendarRepository(db, _paths);
        await repo.SaveAsync(new CalendarSource(CalendarA, "Calendar A", "enc", true), Ct);

        await repo.SaveAsync(new CalendarSource(CalendarA, "Team Calendar", "enc", true), Ct);

        var only = Assert.Single(await repo.GetAllAsync(Ct));
        Assert.Equal(CalendarA, only.CalendarId);
        Assert.Equal("Team Calendar", only.Name);
    }

    [Fact]
    public async Task T26_no_plain_url_in_database_backup_or_settings_files()
    {
        using (var db = OpenDatabase())
        {
            var repo = new SqliteCalendarRepository(db, _paths);
            await repo.SaveAsync(new CalendarSource(CalendarA, "Calendar A", new DpapiUrlProtector().Protect(SecretUrl), true), Ct);
            new SettingsStore(_paths).Save(new AppSettings());
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.DoesNotContain("SECRET-TOKEN", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            Assert.DoesNotContain("calendar-a.example", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-TOKEN", Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Calendar_backup_restores_calendars_into_a_new_database()
    {
        using (var db = OpenDatabase())
        {
            await new SqliteCalendarRepository(db, _paths).SaveAsync(new CalendarSource(CalendarA, "Calendar A", "enc-a", true), Ct);
        }

        SqliteConnection.ClearAllPools();
        File.Delete(_paths.DatabasePath);
        File.Delete(_paths.DatabasePath + "-wal");
        File.Delete(_paths.DatabasePath + "-shm");

        using var fresh = OpenDatabase();
        var repo = new SqliteCalendarRepository(fresh, _paths);
        var restored = await repo.RestoreFromBackupIfEmptyAsync(Ct);

        Assert.Equal(1, restored);
        Assert.Equal("enc-a", Assert.Single(await repo.GetAllAsync(Ct)).EncryptedUrl);
    }

    [Fact]
    public async Task Sync_state_round_trips()
    {
        using var db = OpenDatabase();
        var store = new SqliteSyncStateStore(db);
        var record = new SyncRecord(CalendarA)
        {
            ETag = "\"v1\"",
            LastModified = "Wed, 18 Nov 2026 11:00:00 GMT",
            LastSuccessUtc = Start,
            LastAttemptUtc = Start,
            ConsecutiveFailures = 2,
            NextRefreshUtc = Start.AddMinutes(4),
            LastState = "NetworkError",
            LastError = "Server error",
            LastHttpStatus = 503,
            UpcomingCount = 7,
        };

        await store.SaveAsync(record, Ct);

        Assert.Equal(record, Assert.Single(await store.GetAllAsync(Ct)));
        await store.DeleteAsync(CalendarA, Ct);
        Assert.Empty(await store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task T07_reminder_state_and_occurrences_survive_reopen()
    {
        var options = ReminderOptions.Default;
        var occurrence = Occ("k1", CalendarA);
        var record = ReminderStateMachine.ApplyFired(ReminderRecord.CreateFor(occurrence, options), Start.AddMinutes(-15));
        (record, _) = ReminderStateMachine.ApplySnooze(record, 5, Start.AddMinutes(-14), options);
        record = record with { LastToastTag = "0123456789ab", LastToastGroup = "reminders" };
        using (var db = OpenDatabase())
        {
            await new SqliteReminderStore(db).SaveAsync([record], [occurrence], Ct);
        }

        SqliteConnection.ClearAllPools();
        using var reopened = OpenDatabase();
        var snapshot = await new SqliteReminderStore(reopened).LoadAsync(Ct);

        Assert.Equal(record, Assert.Single(snapshot.Records));
        Assert.Equal(occurrence with { Uid = string.Empty }, Assert.Single(snapshot.Occurrences)); // UID is not persisted (privacy)
    }

    [Fact]
    public async Task Upsert_updates_existing_rows()
    {
        using var db = OpenDatabase();
        var store = new SqliteReminderStore(db);
        var record = ReminderRecord.CreateFor(Occ("k1", CalendarA), ReminderOptions.Default);
        await store.SaveAsync([record], [Occ("k1", CalendarA)], Ct);

        var dismissed = ReminderStateMachine.ApplyDismiss(record);
        await store.SaveAsync([dismissed], [Occ("k1", CalendarA) with { IsCancelled = true }], Ct);

        var snapshot = await store.LoadAsync(Ct);
        Assert.True(Assert.Single(snapshot.Records).Dismissed);
        Assert.True(Assert.Single(snapshot.Occurrences).IsCancelled);
    }

    [Fact]
    public async Task Deleting_calendar_data_leaves_other_calendars()
    {
        using var db = OpenDatabase();
        var store = new SqliteReminderStore(db);
        await store.SaveAsync(
            [ReminderRecord.CreateFor(Occ("a", CalendarA), ReminderOptions.Default), ReminderRecord.CreateFor(Occ("b", CalendarB), ReminderOptions.Default)],
            [Occ("a", CalendarA), Occ("b", CalendarB)], Ct);

        await store.DeleteCalendarDataAsync(CalendarA, Ct);

        var snapshot = await store.LoadAsync(Ct);
        Assert.Equal(CalendarB, Assert.Single(snapshot.Records).CalendarId);
        Assert.Equal(CalendarB, Assert.Single(snapshot.Occurrences).CalendarId);
    }

    [Fact]
    public async Task Prune_removes_records_older_than_cutoff()
    {
        using var db = OpenDatabase();
        var store = new SqliteReminderStore(db);
        await store.SaveAsync([ReminderRecord.CreateFor(Occ("old", CalendarA), ReminderOptions.Default)], [Occ("old", CalendarA)], Ct);

        await store.PruneAsync(Start.AddDays(1), Ct);

        var snapshot = await store.LoadAsync(Ct);
        Assert.Empty(snapshot.Records);
        Assert.Empty(snapshot.Occurrences);
    }

    [Fact]
    public async Task Toast_batches_round_trip_and_prune()
    {
        using var db = OpenDatabase();
        IToastBatchStore store = new SqliteReminderStore(db);
        var batch = new ToastBatchRecord("0123456789ab", "reminders", ["k1", "k2"], Start, Start.AddHours(1));

        await store.SaveBatchAsync(batch, Ct);
        var loaded = await store.GetBatchAsync("0123456789ab", Ct);

        Assert.NotNull(loaded);
        Assert.Equal(["k1", "k2"], loaded.OccurrenceKeys);
        Assert.Equal(batch.ExpiresUtc, loaded.ExpiresUtc);
        await store.PruneBatchesAsync(Start.AddDays(2), Ct);
        Assert.Null(await store.GetBatchAsync("0123456789ab", Ct));
    }

    [Fact]
    public async Task Privacy_mode_title_is_not_persisted_when_absent()
    {
        using var db = OpenDatabase();
        var store = new SqliteReminderStore(db);
        await store.SaveAsync([], [Occ("k1", CalendarA, title: null)], Ct);

        Assert.Null(Assert.Single((await store.LoadAsync(Ct)).Occurrences).PrivacySafeTitle);
    }

    [Fact]
    public void Corrupt_database_is_preserved_and_reported_not_deleted()
    {
        Directory.CreateDirectory(_paths.Root);
        var garbage = Encoding.ASCII.GetBytes("this is definitely not a sqlite database file ......................................................................");
        File.WriteAllBytes(_paths.DatabasePath, garbage);

        var ex = Assert.Throws<StorageCorruptedException>(OpenDatabase);

        Assert.True(File.Exists(_paths.DatabasePath));
        Assert.Equal(garbage, File.ReadAllBytes(_paths.DatabasePath));
        Assert.NotNull(ex.PreservedCopyPath);
        Assert.True(File.Exists(ex.PreservedCopyPath));
    }

    [Fact]
    public void Reset_after_corruption_moves_file_aside_and_creates_a_new_database()
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllBytes(_paths.DatabasePath, Encoding.ASCII.GetBytes(new string('x', 200)));
        Assert.Throws<StorageCorruptedException>(OpenDatabase);

        using var db = CalendarBellDatabase.ResetAfterCorruption(_paths);

        Assert.Equal(CalendarBellDatabase.CurrentSchemaVersion, db.SchemaVersion);
        Assert.Contains(Directory.EnumerateFiles(_paths.Root), f => Path.GetFileName(f).StartsWith("calendarbell.db.corrupt-", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_default_when_missing_and_round_trip_atomically()
    {
        var store = new SettingsStore(_paths);

        var defaults = store.Load();
        Assert.True(defaults.StartWithWindows);
        Assert.True(defaults.PlaySound);
        Assert.True(defaults.PrivacyMode);
        Assert.False(defaults.FirstRunCompleted);
        Assert.Equal("en-US", defaults.Language);

        store.Save(defaults with { PlaySound = false, FirstRunCompleted = true, PausedUntilUtc = Start });
        store.Save(store.Load() with { PrivacyMode = false });

        var loaded = store.Load();
        Assert.False(loaded.PlaySound);
        Assert.False(loaded.PrivacyMode);
        Assert.Equal(Start, loaded.PausedUntilUtc);
        Assert.True(File.Exists(_paths.SettingsPath + ".bak"));
        Assert.Empty(Directory.EnumerateFiles(_paths.Root, "*.tmp"));
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_backup()
    {
        var store = new SettingsStore(_paths);
        store.Save(new AppSettings { PlaySound = false });
        store.Save(new AppSettings { PlaySound = false, FirstRunCompleted = true });
        File.WriteAllText(_paths.SettingsPath, "{ not json");

        var loaded = store.Load();

        Assert.False(loaded.PlaySound);
    }

    [Fact]
    public void Window_placement_is_persisted()
    {
        var store = new SettingsStore(_paths);
        var placement = new WindowPlacement(100, 120, 760, 560);

        store.Save(new AppSettings().WithPlacement("Settings", placement));

        Assert.Equal(placement, store.Load().GetPlacement("Settings"));
    }
}
