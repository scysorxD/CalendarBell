using System.Globalization;
using System.Text;
using System.Xml.Linq;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Notifications;
using CalendarBell.Core.Services;
using static CalendarBell.Core.Tests.ReminderStateMachineTests;

namespace CalendarBell.Core.Tests;

public class NotificationContentTests
{
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CalendarB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly NotificationPreferences Defaults = new(PlaySound: true, PrivacyMode: true);

    private readonly NotificationBatcher _batcher = new(ReminderOptions.Default, new DisplayFormatter(TimeZoneInfo.Utc, CultureInfo.InvariantCulture));

    private static DueReminder Due(DateTimeOffset start, string calendarName = "Calendar A", Guid? calendarId = null, string availability = Availability.Busy, string key = "k1", ReminderKind kind = ReminderKind.Initial, string? title = null, int duration = 30)
    {
        var occurrence = Occ(start, key, calendarId ?? CalendarA, availability, duration) with { PrivacySafeTitle = title };
        return new DueReminder(ReminderRecord.CreateFor(occurrence, ReminderOptions.Default), occurrence, calendarName, kind, start);
    }

    private static XDocument Parse(NotificationBatch batch) => XDocument.Parse(batch.Xml);

    private static string[] Texts(NotificationBatch batch) => Parse(batch).Descendants("text").Select(t => t.Value).ToArray();

    private static string[] Buttons(NotificationBatch batch) => Parse(batch).Descendants("action").Select(a => (string)a.Attribute("content")!).ToArray();

    private static string[] Selections(NotificationBatch batch) => Parse(batch).Descendants("selection").Select(s => (string)s.Attribute("id")!).ToArray();

    [Fact]
    public void Single_initial_toast_matches_spec_copy_and_controls()
    {
        var batch = _batcher.Build([Due(T(10, 30))], T(10, 15), Defaults, "b1");

        Assert.Equal(BatchKind.Single, batch.Kind);
        Assert.Equal(["Calendar A · Meeting in 15 min", "Busy · 10:30–11:00"], Texts(batch));
        Assert.Equal(["10", "5", "0"], Selections(batch));
        Assert.Equal(["Snooze", "Dismiss"], Buttons(batch));
        var toast = Parse(batch).Root!;
        Assert.Equal("reminder", (string?)toast.Attribute("scenario"));
        Assert.Equal("action=open&b=b1", (string?)toast.Attribute("launch"));
        Assert.Equal("10", (string?)toast.Descendants("input").Single().Attribute("defaultInput"));
        Assert.Equal("ms-winsoundevent:Notification.Reminder", (string?)toast.Element("audio")!.Attribute("src"));
        Assert.Contains(Parse(batch).Descendants("selection"), s => (string?)s.Attribute("content") == "10 min before");
        Assert.Contains(Parse(batch).Descendants("selection"), s => (string?)s.Attribute("content") == "At start");
    }

    [Fact]
    public void Snoozed_to_five_minutes_shows_real_remaining_time_and_only_future_targets()
    {
        var batch = _batcher.Build([Due(T(10, 30), kind: ReminderKind.Snooze)], T(10, 25), Defaults, "b1");

        Assert.Equal("Calendar A · Meeting in 5 min", Texts(batch)[0]);
        Assert.Equal(["0"], Selections(batch));
    }

    [Fact]
    public void At_start_toast_says_starting_now_and_only_offers_dismiss()
    {
        var batch = _batcher.Build([Due(T(10, 30), kind: ReminderKind.Snooze)], T(10, 30), Defaults, "b1");

        Assert.Equal(["Calendar A · Meeting starting now", "Busy · 10:30–11:00"], Texts(batch));
        Assert.Empty(Selections(batch));
        Assert.Equal(["Dismiss"], Buttons(batch));
        Assert.Empty(Parse(batch).Descendants("input"));
    }

    [Fact]
    public void Late_toast_uses_real_minutes()
    {
        var batch = _batcher.Build([Due(T(10, 30))], T(10, 18), Defaults, "b1");

        Assert.Equal("Calendar A · Meeting in 12 min", Texts(batch)[0]);
        Assert.Equal(["10", "5", "0"], Selections(batch));
    }

    [Fact]
    public void Unknown_availability_reads_meeting()
    {
        var batch = _batcher.Build([Due(T(10, 30), availability: Availability.Unknown)], T(10, 15), Defaults, "b1");

        Assert.Equal("Meeting · 10:30–11:00", Texts(batch)[1]);
    }

    [Fact]
    public void T09_two_calendars_same_start_produce_one_group_toast()
    {
        var batch = _batcher.Build(
            [Due(T(10, 30), "Calendar A", CalendarA, key: "a"), Due(T(10, 30), "Calendar B", CalendarB, Availability.Tentative, "b")],
            T(10, 15), Defaults, "b1");

        Assert.Equal(BatchKind.SameStartGroup, batch.Kind);
        Assert.Equal(["2 meetings in 15 min", "Calendar A · Busy · 10:30 | Calendar B · Tentative · 10:30"], Texts(batch));
        Assert.Equal(["Snooze", "Dismiss"], Buttons(batch));
        Assert.Equal(2, batch.Members.Count);
    }

    [Fact]
    public void Three_or_more_show_two_names_and_more_count()
    {
        var batch = _batcher.Build(
            [Due(T(10, 30), "Calendar A", key: "a"), Due(T(10, 30), "Calendar B", CalendarB, key: "b"), Due(T(10, 30, 20), "Calendar C", Guid.NewGuid(), key: "c")],
            T(10, 15), Defaults, "b1");

        Assert.Equal(["3 meetings in 15 min", "Calendar A · Busy · 10:30 | Calendar B · Busy · 10:30", "+1 more"], Texts(batch));
    }

    [Fact]
    public void Different_starts_produce_summary_with_view_and_dismiss_all()
    {
        var batch = _batcher.Build(
            [Due(T(10, 30), key: "a"), Due(T(11, 0), "Calendar B", CalendarB, key: "b")],
            T(10, 30), Defaults, "b1");

        Assert.Equal(BatchKind.Summary, batch.Kind);
        Assert.Equal("2 reminders need attention", Texts(batch)[0]);
        Assert.Equal(["View reminders", "Dismiss all"], Buttons(batch));
        Assert.Empty(Selections(batch));
        Assert.Contains(Parse(batch).Descendants("action"), a => (string?)a.Attribute("arguments") == "action=view&b=b1");
    }

    [Fact]
    public void T12_hundred_reminders_fit_in_one_small_toast()
    {
        var members = Enumerable.Range(0, 100).Select(i => Due(T(10, 30), "Calendar " + i, Guid.NewGuid(), key: "k" + i)).ToList();

        var batch = _batcher.Build(members, T(10, 15), Defaults, "b1");

        Assert.Equal(100, batch.Members.Count);
        Assert.Equal("100 meetings in 15 min", Texts(batch)[0]);
        Assert.Equal(3, Texts(batch).Length);
        Assert.Equal("+98 more", Texts(batch)[2]);
        Assert.True(Encoding.UTF8.GetByteCount(batch.Xml) < 5 * 1024);
    }

    [Fact]
    public void T28_sound_off_produces_silent_toast()
    {
        var batch = _batcher.Build([Due(T(10, 30))], T(10, 15), Defaults with { PlaySound = false }, "b1");

        Assert.Equal("true", (string?)Parse(batch).Root!.Element("audio")!.Attribute("silent"));
    }

    [Fact]
    public void T33_markup_and_quotes_are_escaped_and_xml_stays_valid()
    {
        const string evil = "A <b>&amp;</b> \"quoted\" 'x' ]]>";
        var batch = _batcher.Build([Due(T(10, 30), evil, title: "<script>alert('x')</script> & co")], T(10, 15), Defaults with { PrivacyMode = false }, "b1");

        var texts = Texts(batch);
        Assert.Equal(evil + " · Meeting in 15 min", texts[0]);
        Assert.Equal("<script>alert('x')</script> & co", texts[2]);
        Assert.DoesNotContain("<script>", batch.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Privacy_mode_never_shows_titles()
    {
        var batch = _batcher.Build([Due(T(10, 30), title: "Budget review")], T(10, 15), Defaults, "b1");

        Assert.DoesNotContain(Texts(batch), t => t.Contains("Budget", StringComparison.Ordinal));
        Assert.Equal(2, Texts(batch).Length);
    }

    [Fact]
    public void Title_is_sanitized_and_truncated_to_80_characters()
    {
        var longTitle = "Line1\r\nLine2\t\u0001" + new string('x', 200);
        var batch = _batcher.Build([Due(T(10, 30), title: longTitle)], T(10, 15), Defaults with { PrivacyMode = false }, "b1");

        var shown = Texts(batch)[2];
        Assert.StartsWith("Line1 Line2 x", shown, StringComparison.Ordinal);
        Assert.Equal(80, shown.Length);
        Assert.EndsWith("…", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Expiration_is_after_the_meeting_start_and_in_the_future()
    {
        var batch = _batcher.Build([Due(T(10, 30))], T(10, 15), Defaults, "b1");

        Assert.True(batch.ExpiresUtc >= T(11, 0));
        Assert.True(batch.ExpiresUtc > T(10, 15));
    }

    [Fact]
    public void Test_notification_is_marked_and_uses_test_group()
    {
        var batch = _batcher.BuildTest(T(10, 15), Defaults, "t1");

        Assert.Equal(BatchKind.Test, batch.Kind);
        Assert.Equal(NotificationBatcher.TestGroup, batch.Group);
        Assert.Empty(batch.Members);
        Assert.All(Parse(batch).Descendants("action"), a => Assert.StartsWith("action=test-", (string?)a.Attribute("arguments"), StringComparison.Ordinal));
        Assert.Contains("Test notification", Texts(batch)[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("action=snooze&b=0123456789ab", ToastActionKind.Snooze, "0123456789ab")]
    [InlineData("action=dismiss&b=0123456789ab", ToastActionKind.Dismiss, "0123456789ab")]
    [InlineData("action=open&b=0123456789ab", ToastActionKind.Open, "0123456789ab")]
    [InlineData("action=view&b=0123456789ab", ToastActionKind.View, "0123456789ab")]
    [InlineData("action=test-snooze&b=0123456789ab", ToastActionKind.TestAction, "0123456789ab")]
    public void Toast_arguments_round_trip(string arguments, ToastActionKind kind, string batchId)
    {
        var action = ToastArguments.Parse(arguments, new Dictionary<string, string>());

        Assert.NotNull(action);
        Assert.Equal(kind, action.Kind);
        Assert.Equal(batchId, action.BatchId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("action=delete&b=0123456789ab")]
    [InlineData("action=snooze&b=../../etc")]
    [InlineData("action=snooze")]
    public void Malformed_toast_arguments_are_rejected(string arguments)
    {
        Assert.Null(ToastArguments.Parse(arguments, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("5", 5)]
    [InlineData("0", 0)]
    [InlineData("7", null)]
    [InlineData("abc", null)]
    public void Snooze_input_is_validated(string value, int? expected)
    {
        var action = ToastArguments.Parse("action=snooze&b=0123456789ab", new Dictionary<string, string> { [ToastXmlBuilder.SnoozeInputId] = value });

        Assert.Equal(expected, action!.SnoozeMinutes);
    }
}
