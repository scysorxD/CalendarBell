namespace CalendarBell.Core.Services;

/// <summary>Snooze targets are absolute instants relative to the event start: start − 10 min, start − 5 min, start.</summary>
public static class SnoozeTargets
{
    public static DateTimeOffset TargetUtc(DateTimeOffset startUtc, int minutesBeforeStart) =>
        startUtc - TimeSpan.FromMinutes(minutesBeforeStart);

    public static bool IsValidChoice(int minutesBeforeStart, ReminderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.SnoozeTargetsMinutes.Contains(minutesBeforeStart);
    }

    public static bool IsFuture(DateTimeOffset startUtc, int minutesBeforeStart, DateTimeOffset nowUtc, ReminderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TargetUtc(startUtc, minutesBeforeStart) > nowUtc + options.SnoozeMargin;
    }

    /// <summary>Targets (subset of 10, 5, 0) that are strictly in the future, with the technical margin.</summary>
    public static IReadOnlyList<int> Available(DateTimeOffset startUtc, DateTimeOffset nowUtc, ReminderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.SnoozeTargetsMinutes.Where(m => IsFuture(startUtc, m, nowUtc, options)).ToArray();
    }

    /// <summary>
    /// For an action coming from a toast that may have been on screen for a while: the chosen target if still
    /// future, otherwise the next later target that is (10, then 5, then 0), or null when none remains.
    /// </summary>
    public static int? ChosenOrNextFuture(DateTimeOffset startUtc, int chosenMinutes, DateTimeOffset nowUtc, ReminderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsValidChoice(chosenMinutes, options))
        {
            return null;
        }

        foreach (var m in options.SnoozeTargetsMinutes.Where(m => m <= chosenMinutes).OrderByDescending(m => m))
        {
            if (IsFuture(startUtc, m, nowUtc, options))
            {
                return m;
            }
        }

        return null;
    }
}
