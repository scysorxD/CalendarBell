namespace CalendarBell.Core.Services;

/// <summary>Timing constants from the spec. Fixed in v1 (not user-editable), configurable in code.</summary>
public sealed record ReminderOptions
{
    public static ReminderOptions Default { get; } = new();

    /// <summary>Initial reminder lead time: 15 minutes before start.</summary>
    public TimeSpan InitialLead { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Absolute snooze targets, in minutes before start.</summary>
    public IReadOnlyList<int> SnoozeTargetsMinutes { get; init; } = [10, 5, 0];

    /// <summary>A snooze target must be at least this far in the future to be offered/accepted.</summary>
    public TimeSpan SnoozeMargin { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A reminder for a meeting that started less than this long ago still fires once ("starting now").</summary>
    public TimeSpan StartGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Reminders due within this window are coalesced into one batch.</summary>
    public TimeSpan CoalesceWindow { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>At most one new toast per this interval.</summary>
    public TimeSpan MinToastInterval { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Two occurrences whose starts differ by at most this belong to the same group toast.</summary>
    public TimeSpan SameStartTolerance { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Calendars whose last successful sync is older than this are considered stale.</summary>
    public TimeSpan MaxCacheAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Recurrence expansion window: from now − 1 day …</summary>
    public TimeSpan ExpansionPast { get; init; } = TimeSpan.FromDays(1);

    /// <summary>… to now + 30 days.</summary>
    public TimeSpan ExpansionFuture { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Periodic re-evaluation to catch clock jumps and sleep/resume drift.</summary>
    public TimeSpan DriftCheckInterval { get; init; } = TimeSpan.FromSeconds(60);
}
