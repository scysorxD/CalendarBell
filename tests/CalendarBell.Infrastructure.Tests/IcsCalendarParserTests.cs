using CalendarBell.Core.Models;
using CalendarBell.Infrastructure.Ics;

namespace CalendarBell.Infrastructure.Tests;

public class IcsCalendarParserTests
{
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CalendarB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStart = Now.AddDays(-1);
    private static readonly DateTimeOffset WindowEnd = Now.AddDays(30);

    private readonly IcsCalendarParser _parser = new(floatingZone: TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time"));

    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private IReadOnlyList<EventOccurrence> ParseA(string fixture) => _parser.Parse(Fixture(fixture), CalendarA, WindowStart, WindowEnd);

    private static EventOccurrence ByUid(IEnumerable<EventOccurrence> list, string uid) => list.Single(o => o.Uid == uid);

    [Fact]
    public void Busy_event_with_utc_times_is_parsed()
    {
        var o = ByUid(ParseA("calendar-a-basic.ics"), "a-busy-001@calendar-a.example");

        Assert.Equal(CalendarA, o.CalendarId);
        Assert.Equal(Utc(11, 18, 13, 30), o.StartUtc);
        Assert.Equal(Utc(11, 18, 14, 0), o.EndUtc);
        Assert.Equal(Availability.Busy, o.Availability);
        Assert.Equal("Planning", o.PrivacySafeTitle);
        Assert.False(o.IsCancelled);
        Assert.False(o.IsAllDay);
    }

    [Theory]
    [InlineData("a-tentative-002@calendar-a.example", Availability.Tentative)]
    [InlineData("a-oof-007@calendar-a.example", Availability.OutOfOffice)]
    [InlineData("a-summary-busy-008@calendar-a.example", Availability.Busy)]
    [InlineData("a-unknown-009@calendar-a.example", Availability.Unknown)]
    public void Availability_is_mapped_only_from_what_the_feed_states(string uid, string expected)
    {
        Assert.Equal(expected, ByUid(ParseA("calendar-a-basic.ics"), uid).Availability);
    }

    [Fact]
    public void Duration_defines_the_end()
    {
        Assert.Equal(Utc(11, 24, 9, 45), ByUid(ParseA("calendar-a-basic.ics"), "a-summary-busy-008@calendar-a.example").EndUtc);
    }

    [Theory]
    [InlineData("a-free-003@calendar-a.example")]
    [InlineData("a-allday-004@calendar-a.example")]
    [InlineData("a-invalid-006@calendar-a.example")]
    [InlineData("a-past-010@calendar-a.example")]
    [InlineData("a-free-status-011@calendar-a.example")]
    public void T25_free_all_day_invalid_and_out_of_window_events_are_excluded(string uid)
    {
        Assert.DoesNotContain(ParseA("calendar-a-basic.ics"), o => o.Uid == uid);
    }

    [Fact]
    public void T25_cancelled_event_is_returned_only_as_cancellation()
    {
        var o = ByUid(ParseA("calendar-a-basic.ics"), "a-cancelled-005@calendar-a.example");

        Assert.True(o.IsCancelled);
    }

    [Fact]
    public void Availability_word_in_summary_is_not_treated_as_a_title()
    {
        Assert.Null(ByUid(ParseA("calendar-a-basic.ics"), "a-summary-busy-008@calendar-a.example").PrivacySafeTitle);
    }

    [Fact]
    public void Event_without_uid_gets_a_stable_fallback_key()
    {
        var first = ParseA("calendar-a-basic.ics").Single(o => o.StartUtc == Utc(11, 26, 9));
        var second = ParseA("calendar-a-basic.ics").Single(o => o.StartUtc == Utc(11, 26, 9));

        Assert.StartsWith(IcsCalendarParser.FallbackUidPrefix, first.Uid, StringComparison.Ordinal);
        Assert.Equal(first.OccurrenceKey, second.OccurrenceKey);
    }

    [Fact]
    public void Keys_are_opaque_hashes_and_stable_across_parses()
    {
        var first = ParseA("calendar-a-basic.ics");
        var second = ParseA("calendar-a-basic.ics");

        Assert.Equal(first.Select(o => o.OccurrenceKey), second.Select(o => o.OccurrenceKey));
        Assert.Equal(first.Select(o => o.RevisionFingerprint), second.Select(o => o.RevisionFingerprint));
        Assert.All(first, o =>
        {
            Assert.Matches("^[0-9a-f]{32}$", o.OccurrenceKey);
            Assert.DoesNotContain("calendar-a", o.OccurrenceKey, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void T13_same_uid_in_two_calendars_gives_different_keys()
    {
        var a = ByUid(ParseA("calendar-a-basic.ics"), "a-busy-001@calendar-a.example");
        var b = ByUid(_parser.Parse(Fixture("calendar-b-outlook-tz.ics"), CalendarB, WindowStart, WindowEnd), "a-busy-001@calendar-a.example");

        Assert.NotEqual(a.OccurrenceKey, b.OccurrenceKey);
        Assert.Equal(CalendarB, b.CalendarId);
    }

    [Fact]
    public void Fingerprint_ignores_cosmetic_changes_but_tracks_time_changes()
    {
        var ics = Fixture("calendar-a-basic.ics");
        var baseline = ByUid(_parser.Parse(ics, CalendarA, WindowStart, WindowEnd), "a-busy-001@calendar-a.example");
        var renamed = ByUid(_parser.Parse(ics.Replace("SUMMARY:Planning", "SUMMARY:Planning v2", StringComparison.Ordinal), CalendarA, WindowStart, WindowEnd), "a-busy-001@calendar-a.example");
        var longer = ByUid(_parser.Parse(ics.Replace("DTEND:20261118T140000Z", "DTEND:20261118T143000Z", StringComparison.Ordinal), CalendarA, WindowStart, WindowEnd), "a-busy-001@calendar-a.example");

        Assert.Equal(baseline.RevisionFingerprint, renamed.RevisionFingerprint);
        Assert.NotEqual(baseline.RevisionFingerprint, longer.RevisionFingerprint);
        Assert.Equal(baseline.OccurrenceKey, longer.OccurrenceKey);
    }

    [Fact]
    public void T23_exdate_instance_is_not_expanded()
    {
        var series = ParseA("calendar-a-recurring.ics");

        Assert.DoesNotContain(series, o => o.StartUtc.Date == new DateTime(2026, 11, 23));
    }

    [Fact]
    public void T24_recurrence_id_moves_one_instance_without_duplicate()
    {
        var series = ParseA("calendar-a-recurring.ics");

        var nov30 = Assert.Single(series, o => o.StartUtc.Date == new DateTime(2026, 11, 30));
        Assert.Equal(Utc(11, 30, 19), nov30.StartUtc);
        Assert.Equal(IcsCalendarParser.ComputeOccurrenceKey(CalendarA, "a-weekly-100@calendar-a.example", Utc(11, 30, 14)), nov30.OccurrenceKey);
    }

    [Fact]
    public void Cancelled_instance_of_series_is_flagged()
    {
        var dec7 = Assert.Single(ParseA("calendar-a-recurring.ics"), o => o.StartUtc.Date == new DateTime(2026, 12, 7));

        Assert.True(dec7.IsCancelled);
    }

    [Fact]
    public void T19_series_keeps_local_wall_time_across_us_dst_change()
    {
        var series = _parser.Parse(Fixture("calendar-a-recurring.ics"), CalendarA, Utc(10, 20, 0), Utc(11, 10, 0));

        Assert.Contains(series, o => o.StartUtc == Utc(10, 26, 13)); // 09:00 EDT
        Assert.Contains(series, o => o.StartUtc == Utc(11, 2, 14));  // 09:00 EST
    }

    [Fact]
    public void T19_series_keeps_local_wall_time_across_eu_dst_change()
    {
        var series = _parser.Parse(Fixture("dst-berlin.ics"), CalendarA, Utc(10, 20, 0), Utc(11, 10, 0));

        Assert.Contains(series, o => o.StartUtc == Utc(10, 24, 7)); // 09:00 CEST
        Assert.Contains(series, o => o.StartUtc == Utc(10, 26, 8)); // 09:00 CET
        Assert.Equal(10, series.Count);
        Assert.Equal(10, series.Select(o => o.OccurrenceKey).Distinct().Count());
    }

    [Fact]
    public void Outlook_display_name_tzid_resolves()
    {
        var o = ByUid(_parser.Parse(Fixture("calendar-b-outlook-tz.ics"), CalendarB, WindowStart, WindowEnd), "b-outlook-200@calendar-b.example");

        Assert.Equal(Utc(11, 18, 9, 30), o.StartUtc);
        Assert.Equal(Availability.Tentative, o.Availability);
    }

    [Fact]
    public void Customized_vtimezone_resolves_from_its_rules()
    {
        var o = ByUid(_parser.Parse(Fixture("calendar-b-outlook-tz.ics"), CalendarB, WindowStart, WindowEnd), "b-custom-201@calendar-b.example");

        Assert.Equal(Utc(11, 19, 9, 30), o.StartUtc);
    }

    [Fact]
    public void Windows_tzid_resolves()
    {
        var o = ByUid(_parser.Parse(Fixture("calendar-b-outlook-tz.ics"), CalendarB, WindowStart, WindowEnd), "b-windows-202@calendar-b.example");

        Assert.Equal(Utc(11, 20, 9, 30), o.StartUtc);
    }

    [Fact]
    public void Unknown_tzid_does_not_break_the_feed()
    {
        var ics = Fixture("calendar-b-outlook-tz.ics").Replace("TZID=W. Europe Standard Time", "TZID=Mars/Olympus", StringComparison.Ordinal);

        var all = _parser.Parse(ics, CalendarB, WindowStart, WindowEnd);

        Assert.Contains(all, o => o.Uid == "b-windows-202@calendar-b.example");
        Assert.Contains(all, o => o.Uid == "b-outlook-200@calendar-b.example");
    }

    [Fact]
    public void Floating_time_is_interpreted_in_the_local_zone()
    {
        var o = Assert.Single(ParseA("floating.ics"));

        Assert.Equal(Utc(11, 18, 13, 30), o.StartUtc); // 10:30 at UTC-3
    }

    [Theory]
    [InlineData("invalid-html.ics")]
    [InlineData("truncated.ics")]
    public void T21_invalid_or_truncated_feed_throws_format_exception(string fixture)
    {
        Assert.Throws<IcsFormatException>(() => ParseA(fixture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BEGIN:VEVENT\r\nEND:VEVENT\r\n")]
    public void Garbage_is_rejected(string ics)
    {
        Assert.Throws<IcsFormatException>(() => _parser.Parse(ics, CalendarA, WindowStart, WindowEnd));
    }

    [Fact]
    public void Valid_empty_calendar_returns_no_events()
    {
        Assert.Empty(ParseA("empty-valid.ics"));
    }

    [Fact]
    public void Folded_lines_are_unfolded()
    {
        var all = ParseA("folded.ics");

        Assert.Contains(all, o => o.Uid == "fold-700@calendar-a.example");
        Assert.Contains(all, o => o.Uid.StartsWith(IcsCalendarParser.FallbackUidPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Crlf_and_lf_line_endings_give_identical_results()
    {
        var lf = Fixture("calendar-a-recurring.ics").Replace("\r\n", "\n", StringComparison.Ordinal);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Equal(
            _parser.Parse(lf, CalendarA, WindowStart, WindowEnd).Select(o => (o.OccurrenceKey, o.StartUtc)),
            _parser.Parse(crlf, CalendarA, WindowStart, WindowEnd).Select(o => (o.OccurrenceKey, o.StartUtc)));
    }

    [Fact]
    public void Expansion_is_bounded_by_the_window()
    {
        var all = ParseA("many-instances.ics");

        Assert.InRange(all.Count, 4000, 4500);
        Assert.All(all, o => Assert.InRange(o.StartUtc, WindowStart, WindowEnd));
    }

    [Fact]
    public void Exceeding_the_instance_cap_rejects_the_feed_instead_of_truncating_it()
    {
        var capped = new IcsCalendarParser(maxInstances: 1000);

        Assert.Throws<IcsFormatException>(() => capped.Parse(Fixture("many-instances.ics"), CalendarA, WindowStart, WindowEnd));
    }
}
