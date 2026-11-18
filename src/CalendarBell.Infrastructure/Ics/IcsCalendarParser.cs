using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Notifications;
using Ical.Net;
using Calendar = Ical.Net.Calendar;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarBell.Infrastructure.Ics;

/// <summary>
/// RFC 5545 parsing and recurrence expansion with Ical.Net (spec §5.2). Only normalized fields leave this
/// class: DESCRIPTION, ATTENDEE, LOCATION, URLs and the raw text are never returned or logged.
/// </summary>
public sealed class IcsCalendarParser : ICalendarParser
{
    public const string FallbackUidPrefix = "calendarbell-fallback-";
    public const int DefaultMaxInstances = 100_000;

    private readonly ILogger _logger;
    private readonly TimeZoneInfo? _floatingZone;
    private readonly int _maxInstances;
    private readonly TimeSpan _maxDuration;

    public IcsCalendarParser(ILogger<IcsCalendarParser>? logger = null, TimeZoneInfo? floatingZone = null, int maxInstances = DefaultMaxInstances, TimeSpan? maxDuration = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _floatingZone = floatingZone;
        _maxInstances = maxInstances;
        _maxDuration = maxDuration ?? TimeSpan.FromSeconds(10);
        if (logger is not null)
        {
            IcsTimeZones.Warning ??= message => _logger.LogWarning("{Message}", message);
        }

        IcsTimeZones.Install();
    }

    public static string ComputeOccurrenceKey(Guid calendarId, string uid, DateTimeOffset? recurrenceIdUtc)
    {
        ArgumentNullException.ThrowIfNull(uid);
        var rid = recurrenceIdUtc?.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) ?? string.Empty;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(calendarId.ToString("N") + "\n" + uid + "\n" + rid));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 16));
    }

    public IReadOnlyList<EventOccurrence> Parse(string ics, Guid calendarId, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc)
    {
        var prepared = IcsPreprocessor.Prepare(ics, out var generatedUids);
        if (generatedUids > 0)
        {
            _logger.LogWarning("Calendar {CalendarId}: {Count} event(s) without UID got a fallback identity (renaming or moving them may look like a new event)", calendarId, generatedUids);
        }

        Calendar calendar;
        try
        {
            calendar = Calendar.Load(prepared) ?? throw new IcsFormatException("The iCalendar document is empty.");
        }
        catch (IcsFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new IcsFormatException("The iCalendar document could not be parsed.", ex);
        }

        var localZone = _floatingZone ?? TimeZoneInfo.Local;
        var results = new List<EventOccurrence>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var floatingWarned = false;
        var stopwatch = Stopwatch.StartNew();
        var expanded = 0;

        using (IcsTimeZones.Use(calendar, localZone))
        {
            var removed = RemoveInvalidEvents(calendar, localZone);
            if (removed > 0)
            {
                _logger.LogWarning("Calendar {CalendarId}: {Count} event(s) with an invalid start/end were ignored", calendarId, removed);
            }

            var start = new CalDateTime(windowStartUtc.UtcDateTime, "UTC");
            var end = new CalDateTime(windowEndUtc.UtcDateTime, "UTC");
            try
            {
                foreach (var occurrence in calendar.GetOccurrences(start, new EvaluationOptions { MaxUnmatchedIncrementsLimit = _maxInstances }).TakeWhileBefore(end))
                {
                    if (++expanded > _maxInstances)
                    {
                        throw new IcsFormatException($"The calendar expands to more than {_maxInstances} instances in the window.");
                    }

                    if (stopwatch.Elapsed > _maxDuration)
                    {
                        throw new IcsFormatException("Expanding the calendar took too long.");
                    }

                    if (occurrence.Source is not CalendarEvent ev || ev.IsAllDay || !occurrence.Period.StartTime.HasTime)
                    {
                        continue;
                    }

                    var mapped = Map(calendarId, ev, occurrence, localZone, windowStartUtc, windowEndUtc);
                    if (mapped is null || !seenKeys.Add(mapped.OccurrenceKey))
                    {
                        continue;
                    }

                    if (!floatingWarned && occurrence.Period.StartTime.IsFloating)
                    {
                        floatingWarned = true;
                        _logger.LogWarning("Calendar {CalendarId} uses floating times; interpreted in the local time zone", calendarId);
                    }

                    results.Add(mapped);
                }
            }
            catch (IcsFormatException)
            {
                throw;
            }
            catch (Exception ex) when (ex is EvaluationException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            {
                throw new IcsFormatException("The calendar's recurrences could not be expanded.", ex);
            }
        }

        _logger.LogDebug("Calendar {CalendarId}: {Count} occurrence(s) in window ({Expanded} expanded) in {Elapsed} ms", calendarId, results.Count, expanded, stopwatch.ElapsedMilliseconds);
        return results;
    }

    private static EventOccurrence? Map(Guid calendarId, CalendarEvent ev, Occurrence occurrence, TimeZoneInfo localZone, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc)
    {
        var startUtc = ToUtc(occurrence.Period.StartTime, localZone);
        var endTime = occurrence.Period.EffectiveEndTime ?? occurrence.Period.StartTime;
        var endUtc = ToUtc(endTime, localZone);
        if (endUtc < startUtc || startUtc < windowStartUtc || startUtc > windowEndUtc)
        {
            return null; // invalid interval or outside the window
        }

        var cancelled = string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        var availability = AvailabilityMapper.Map(ev);
        if (availability is null)
        {
            return null; // free / transparent time never produces reminders
        }

        var uid = ev.Uid ?? string.Empty;
        DateTimeOffset? recurrenceId = ev.RecurrenceIdentifier?.StartTime is { } rid
            ? ToUtc(rid, localZone)
            : IsRecurring(ev) ? startUtc : null;

        var title = AvailabilityMapper.IsAvailabilityWord(ev.Summary)
            ? null
            : NotificationText.Sanitize(ev.Summary, NotificationText.MaxTitleLength) is { Length: > 0 } t ? t : null;

        return new EventOccurrence(
            calendarId,
            uid,
            ComputeOccurrenceKey(calendarId, uid, recurrenceId),
            startUtc,
            endUtc,
            availability,
            IsAllDay: false,
            cancelled,
            title,
            Fingerprint(startUtc, endUtc, availability));
    }

    /// <summary>
    /// Ical.Net aborts the whole expansion on a single malformed event (e.g. DTEND before DTSTART), so such events
    /// are removed up front: one bad event must not hide every other meeting of the feed.
    /// </summary>
    private static int RemoveInvalidEvents(Calendar calendar, TimeZoneInfo localZone)
    {
        var invalid = new List<CalendarEvent>();
        foreach (var ev in calendar.Events)
        {
            try
            {
                if (ev.DtStart is null)
                {
                    invalid.Add(ev);
                    continue;
                }

                if (!ev.DtStart.HasTime)
                {
                    continue; // all-day: skipped later, never fatal
                }

                var start = ToUtc(ev.DtStart, localZone);
                if ((ev.DtEnd is { HasTime: true } end && ToUtc(end, localZone) < start) ||
                    (ev.Duration is { } duration && duration.Sign < 0))
                {
                    invalid.Add(ev);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
            {
                invalid.Add(ev);
            }
        }

        foreach (var ev in invalid)
        {
            calendar.Events.Remove(ev);
        }

        return invalid.Count;
    }

    private static bool IsRecurring(CalendarEvent ev) =>
        ev.RecurrenceRule is not null || (ev.RecurrenceDates is not null && ev.RecurrenceDates.GetAllDates().Any());

    private static DateTimeOffset ToUtc(CalDateTime value, TimeZoneInfo localZone)
    {
        if (!value.IsFloating)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc));
        }

        var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        if (localZone.IsInvalidTime(local))
        {
            local = local.AddHours(1); // inside a DST gap: move forward like most calendar clients
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, localZone), TimeSpan.Zero);
    }

    private static string Fingerprint(DateTimeOffset startUtc, DateTimeOffset endUtc, string availability)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{startUtc.UtcTicks}|{endUtc.UtcTicks}|{availability}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));
    }
}
