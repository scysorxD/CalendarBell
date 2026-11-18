using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarBell.Infrastructure.Storage;

public sealed record WindowPlacement(double Left, double Top, double Width, double Height);

/// <summary>Non-sensitive preferences only (settings.json). Calendar URLs live encrypted in the database.</summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 1;

    public bool StartWithWindows { get; init; } = true;

    public bool PlaySound { get; init; } = true;

    public bool PrivacyMode { get; init; } = true;

    public bool FirstRunCompleted { get; init; }

    /// <summary>UI language. English by default regardless of the Windows language (es-AR resources are available).</summary>
    public string Language { get; init; } = "en-US";

    /// <summary>Pause end; <see cref="DateTimeOffset.MaxValue"/> means "until resumed".</summary>
    public DateTimeOffset? PausedUntilUtc { get; init; }

    /// <summary>"TaskScheduler" or "RunKey": which auto-start mechanism is registered.</summary>
    public string? StartupMethod { get; init; }

    public Dictionary<string, WindowPlacement> Windows { get; init; } = [];

    public WindowPlacement? GetPlacement(string window) => Windows.TryGetValue(window, out var p) ? p : null;

    public AppSettings WithPlacement(string window, WindowPlacement placement) =>
        this with { Windows = new Dictionary<string, WindowPlacement>(Windows) { [window] = placement } };
}

/// <summary>Atomic load/save of settings.json with a .bak fallback.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DataPaths _paths;
    private readonly object _lock = new();

    public SettingsStore(DataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            return AtomicFile.ReadWithBackup(_paths.SettingsPath, json => JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)) ?? new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            AtomicFile.WriteAllText(_paths.SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
    }
}
