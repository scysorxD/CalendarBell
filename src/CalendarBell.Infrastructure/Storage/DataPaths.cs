namespace CalendarBell.Infrastructure.Storage;

/// <summary>
/// Per-user data locations (spec §5.4): %LOCALAPPDATA%\CalendarBell\ by default. CALENDARBELL_DATA_DIR overrides
/// the root for tests and side-by-side development instances.
/// </summary>
public sealed class DataPaths
{
    public const string DataDirVariable = "CALENDARBELL_DATA_DIR";

    public DataPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, "calendarbell.db");

    public string SettingsPath => Path.Combine(Root, "settings.json");

    public string CalendarBackupPath => Path.Combine(Root, "calendars.backup.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>True when the root was overridden (tests / development instance).</summary>
    public bool IsOverridden { get; private init; }

    public static DataPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? new DataPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalendarBell"))
            : new DataPaths(overridden) { IsOverridden = true };
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
