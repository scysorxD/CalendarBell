using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CalendarBell.Infrastructure.Storage;

/// <summary>The database is unreadable. The original file is left untouched and a copy is preserved for diagnosis.</summary>
public sealed class StorageCorruptedException : Exception
{
    public StorageCorruptedException()
    {
    }

    public StorageCorruptedException(string message)
        : base(message)
    {
    }

    public StorageCorruptedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string? PreservedCopyPath { get; private init; }

    /// <summary>The database file could not be opened at all (locked, permissions, disk).</summary>
    public static StorageCorruptedException ForUnavailable(Exception innerException) =>
        new("The CalendarBell database could not be opened.", innerException);

    internal static StorageCorruptedException ForDamagedFile(string? preservedCopyPath, Exception innerException) =>
        new("The CalendarBell database is damaged.", innerException) { PreservedCopyPath = preservedCopyPath };
}

/// <summary>
/// One SQLite database per user (spec §6.3): versioned schema, WAL, short-lived pooled connections.
/// A damaged database is never deleted automatically; the user can explicitly reset it from Diagnostics.
/// </summary>
public sealed class CalendarBellDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 2;

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private static readonly string[] SchemaV1 =
    [
        """
        CREATE TABLE Calendars (
            calendar_id   TEXT PRIMARY KEY,
            name          TEXT NOT NULL,
            encrypted_url TEXT NOT NULL,
            enabled       INTEGER NOT NULL,
            created_utc   INTEGER NOT NULL,
            updated_utc   INTEGER NOT NULL)
        """,
        "CREATE UNIQUE INDEX ux_calendars_name ON Calendars(name COLLATE NOCASE)",
        """
        CREATE TABLE SyncState (
            calendar_id          TEXT PRIMARY KEY,
            etag                 TEXT NULL,
            last_modified        TEXT NULL,
            last_attempt_utc     INTEGER NULL,
            last_success_utc     INTEGER NULL,
            consecutive_failures INTEGER NOT NULL DEFAULT 0,
            next_refresh_utc     INTEGER NULL,
            last_state           TEXT NULL,
            last_error           TEXT NULL,
            last_http_status     INTEGER NULL,
            upcoming_count       INTEGER NOT NULL DEFAULT 0)
        """,
        """
        CREATE TABLE OccurrencesCache (
            calendar_id    TEXT NOT NULL,
            occurrence_key TEXT NOT NULL,
            start_utc      INTEGER NOT NULL,
            end_utc        INTEGER NOT NULL,
            availability   TEXT NOT NULL,
            is_cancelled   INTEGER NOT NULL,
            title          TEXT NULL,
            fingerprint    TEXT NOT NULL,
            PRIMARY KEY (calendar_id, occurrence_key))
        """,
        "CREATE INDEX ix_occurrences_start ON OccurrencesCache(start_utc)",
        """
        CREATE TABLE Reminders (
            calendar_id       TEXT NOT NULL,
            occurrence_key    TEXT NOT NULL,
            fingerprint       TEXT NOT NULL,
            event_start_utc   INTEGER NOT NULL,
            initial_due_utc   INTEGER NOT NULL,
            initial_sent      INTEGER NOT NULL,
            snoozed_until_utc INTEGER NULL,
            snooze_minutes    INTEGER NULL,
            dismissed         INTEGER NOT NULL,
            cancelled         INTEGER NOT NULL,
            last_notified_utc INTEGER NULL,
            last_toast_tag    TEXT NULL,
            last_toast_group  TEXT NULL,
            state_version     INTEGER NOT NULL,
            updated_utc       INTEGER NOT NULL,
            UNIQUE (calendar_id, occurrence_key))
        """,
        "CREATE INDEX ix_reminders_start ON Reminders(event_start_utc)",
        """
        CREATE TABLE ToastBatches (
            batch_id        TEXT PRIMARY KEY,
            grp             TEXT NOT NULL,
            occurrence_keys TEXT NOT NULL,
            created_utc     INTEGER NOT NULL,
            expires_utc     INTEGER NOT NULL)
        """,
        "CREATE TABLE AppState (key TEXT PRIMARY KEY, value TEXT NULL)",
    ];

    private readonly string _connectionString;

    private CalendarBellDatabase(string connectionString, int schemaVersion, string journalMode)
    {
        _connectionString = connectionString;
        SchemaVersion = schemaVersion;
        JournalMode = journalMode;
    }

    public int SchemaVersion { get; }

    public string JournalMode { get; }

    public static CalendarBellDatabase Open(DataPaths paths, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 5000");
            var journalMode = Convert.ToString(Scalar(connection, "PRAGMA journal_mode = WAL"), CultureInfo.InvariantCulture) ?? string.Empty;
            var check = Convert.ToString(Scalar(connection, "PRAGMA quick_check"), CultureInfo.InvariantCulture);
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqliteException("Integrity check failed.", SqliteCorrupt);
            }

            var version = Migrate(connection);
            return new CalendarBellDatabase(connectionString, version, journalMode);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            SqliteConnection.ClearAllPools();
            throw StorageCorruptedException.ForDamagedFile(PreserveCopy(paths, time ?? TimeProvider.System), ex);
        }
    }

    /// <summary>Explicit user action: move the damaged database aside (kept for diagnosis) and start a new one.</summary>
    public static CalendarBellDatabase ResetAfterCorruption(DataPaths paths, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        SqliteConnection.ClearAllPools();
        var aside = UniqueAsidePath(paths, time ?? TimeProvider.System);
        foreach (var (file, target) in new[] { (paths.DatabasePath, aside), (paths.DatabasePath + "-wal", aside + "-wal"), (paths.DatabasePath + "-shm", aside + "-shm") })
        {
            if (File.Exists(file))
            {
                File.Move(file, target);
            }
        }

        return Open(paths, time);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 5000");
        return connection;
    }

    public void Dispose() => SqliteConnection.ClearAllPools();

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static int Migrate(SqliteConnection connection)
    {
        Execute(connection, "CREATE TABLE IF NOT EXISTS SchemaMigrations (version INTEGER PRIMARY KEY, applied_utc INTEGER NOT NULL)");
        var current = Convert.ToInt32(Scalar(connection, "SELECT COALESCE(MAX(version), 0) FROM SchemaMigrations"), CultureInfo.InvariantCulture);
        if (current < 1)
        {
            ApplyMigration(connection, 1, SchemaV1);
            current = 1;
        }

        if (current < 2)
        {
            // v2: remember when the feed body was last expanded, so 304 responses cannot freeze the 30-day window.
            ApplyMigration(connection, 2, ["ALTER TABLE SyncState ADD COLUMN last_full_parse_utc INTEGER NULL"]);
            current = 2;
        }

        return current;
    }

    private static void ApplyMigration(SqliteConnection connection, int version, IEnumerable<string> statements)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var statement in statements)
        {
            Execute(connection, statement, transaction);
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO SchemaMigrations (version, applied_utc) VALUES ($version, $now)";
        insert.Parameters.AddWithValue("$version", version);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string? PreserveCopy(DataPaths paths, TimeProvider time)
    {
        try
        {
            var copy = UniqueAsidePath(paths, time);
            File.Copy(paths.DatabasePath, copy, overwrite: false);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>calendarbell.db.corrupt-&lt;timestamp&gt;[-n]: never overwrites an earlier preserved copy.</summary>
    private static string UniqueAsidePath(DataPaths paths, TimeProvider time)
    {
        var basePath = paths.DatabasePath + ".corrupt-" + time.GetUtcNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var candidate = basePath;
        for (var n = 1; File.Exists(candidate) || File.Exists(candidate + "-wal"); n++)
        {
            candidate = basePath + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }
}
