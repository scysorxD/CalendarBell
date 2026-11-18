using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Resources;
using CalendarBell.Core.Services;

namespace CalendarBell.Core.Notifications;

/// <summary>Turns a set of simultaneous due reminders into exactly one toast (spec §2.3, §4.2, §6.5).</summary>
public sealed class NotificationBatcher
{
    public const string ReminderGroup = "reminders";
    public const string TestGroup = "test";

    private static readonly TimeSpan StartingNowThreshold = TimeSpan.FromSeconds(30);

    private readonly ReminderOptions _options;
    private readonly DisplayFormatter _formatter;

    public NotificationBatcher(ReminderOptions options, DisplayFormatter formatter)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

    public NotificationBatch Build(IReadOnlyList<DueReminder> due, DateTimeOffset nowUtc, NotificationPreferences preferences, string batchId)
    {
        ArgumentNullException.ThrowIfNull(due);
        ArgumentNullException.ThrowIfNull(preferences);
        if (due.Count == 0)
        {
            throw new ArgumentException("A batch needs at least one reminder.", nameof(due));
        }

        var members = due
            .OrderBy(d => d.Occurrence.StartUtc)
            .ThenBy(d => d.CalendarName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var earliest = members[0].Occurrence.StartUtc;
        var latest = members[^1].Occurrence.StartUtc;
        var sameStart = latest - earliest <= _options.SameStartTolerance;

        BatchKind kind;
        string title;
        List<string> lines;
        IReadOnlyList<int> snoozeOptions;
        if (members.Count == 1)
        {
            var m = members[0];
            kind = BatchKind.Single;
            var name = NotificationText.Sanitize(m.CalendarName, NotificationText.MaxNameLength);
            title = Minutes(earliest, nowUtc) is { } minutes
                ? Strings.Format("Toast_SingleIn", name, minutes)
                : Strings.Format("Toast_SingleNow", name);
            lines = [Strings.Format("Toast_TimeRange", DisplayFormatter.AvailabilityLabel(m.Occurrence.Availability), _formatter.Time(m.Occurrence.StartUtc), _formatter.Time(m.Occurrence.EndUtc))];
            if (!preferences.PrivacyMode && NotificationText.Sanitize(m.Occurrence.PrivacySafeTitle, NotificationText.MaxTitleLength) is { Length: > 0 } eventTitle)
            {
                lines.Add(eventTitle);
            }

            snoozeOptions = SnoozeTargets.Available(earliest, nowUtc, _options);
        }
        else
        {
            kind = sameStart ? BatchKind.SameStartGroup : BatchKind.Summary;
            if (sameStart)
            {
                title = Minutes(earliest, nowUtc) is { } minutes
                    ? Strings.Format("Toast_GroupIn", members.Count, minutes)
                    : Strings.Format("Toast_GroupNow", members.Count);
                snoozeOptions = SnoozeTargets.Available(earliest, nowUtc, _options);
            }
            else
            {
                title = Strings.Format("Toast_Summary", members.Count);
                snoozeOptions = [];
            }

            lines = [string.Join(" | ", members.Take(2).Select(MemberLine))];
            if (members.Count > 2)
            {
                lines.Add(Strings.Format("Toast_More", members.Count - 2));
            }
        }

        var batch = new NotificationBatch(batchId, ReminderGroup, kind, members, title, lines, snoozeOptions, preferences.PlaySound, Expiration(members, nowUtc));
        return batch with { Xml = ToastXmlBuilder.Build(batch) };
    }

    /// <summary>A real toast that demonstrates sound and style without touching production reminder state.</summary>
    public NotificationBatch BuildTest(DateTimeOffset nowUtc, NotificationPreferences preferences, string batchId)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var start = nowUtc + _options.InitialLead;
        var title = Strings.Format("Toast_SingleIn", Strings.Get("Toast_TestCalendar"), (int)_options.InitialLead.TotalMinutes);
        var line = Strings.Format("Toast_TimeRange", DisplayFormatter.AvailabilityLabel(Availability.Busy), _formatter.Time(start), _formatter.Time(start.AddMinutes(30)));
        var batch = new NotificationBatch(batchId, TestGroup, BatchKind.Test, [], title, [line], _options.SnoozeTargetsMinutes, preferences.PlaySound, nowUtc.AddMinutes(10));
        return batch with { Xml = ToastXmlBuilder.Build(batch) };
    }

    private string MemberLine(DueReminder m) =>
        Strings.Format("Toast_MemberLine",
            NotificationText.Sanitize(m.CalendarName, NotificationText.MaxNameLength),
            DisplayFormatter.AvailabilityLabel(m.Occurrence.Availability),
            _formatter.Time(m.Occurrence.StartUtc));

    /// <summary>Real remaining minutes (rounded), or null when the meeting is starting now.</summary>
    private static int? Minutes(DateTimeOffset startUtc, DateTimeOffset nowUtc)
    {
        var remaining = startUtc - nowUtc;
        if (remaining <= StartingNowThreshold)
        {
            return null;
        }

        return Math.Max(1, (int)Math.Round(remaining.TotalMinutes, MidpointRounding.AwayFromZero));
    }

    private static DateTimeOffset Expiration(IReadOnlyList<DueReminder> members, DateTimeOffset nowUtc)
    {
        var expires = members.Max(m =>
        {
            var end = m.Occurrence.EndUtc > m.Occurrence.StartUtc ? m.Occurrence.EndUtc : m.Occurrence.StartUtc.AddMinutes(30);
            var cap = m.Occurrence.StartUtc.AddHours(2);
            return end < cap ? end : cap;
        });
        var minimum = nowUtc.AddMinutes(5);
        return expires > minimum ? expires : minimum;
    }
}
