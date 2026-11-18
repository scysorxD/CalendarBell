using CalendarBell.Core.Interfaces;

namespace CalendarBell.Core.Notifications;

public enum BatchKind
{
    /// <summary>One occurrence.</summary>
    Single,

    /// <summary>Several occurrences whose starts are within ±60 s: group Snooze/Dismiss.</summary>
    SameStartGroup,

    /// <summary>Several occurrences with different starts: "View reminders" / "Dismiss all".</summary>
    Summary,

    /// <summary>"Send test notification": never linked to real reminders.</summary>
    Test,
}

public sealed record NotificationPreferences(bool PlaySound, bool PrivacyMode);

/// <summary>One toast. Every member keeps its own state; the batch only groups the presentation.</summary>
public sealed record NotificationBatch(
    string BatchId,
    string Group,
    BatchKind Kind,
    IReadOnlyList<DueReminder> Members,
    string Title,
    IReadOnlyList<string> Lines,
    IReadOnlyList<int> SnoozeOptions,
    bool PlaySound,
    DateTimeOffset ExpiresUtc)
{
    /// <summary>The toast payload (ToastGeneric XML), built by <see cref="ToastXmlBuilder"/>.</summary>
    public string Xml { get; init; } = string.Empty;

    /// <summary>The toast tag: equal to the batch id (≤ 64 chars as Windows requires).</summary>
    public string Tag => BatchId;
}
