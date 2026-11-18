using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Ical.Net;
using Calendar = Ical.Net.Calendar;
using Ical.Net.CalendarComponents;
using NodaTime;

namespace CalendarBell.Infrastructure.Ics;

/// <summary>
/// TZID resolution for Ical.Net that never silently falls back to UTC. Order:
/// IANA id → Windows id → Windows display name → the calendar's own VTIMEZONE rules (matched against
/// system zones) → "(UTC±hh:mm)" prefix → local zone (with a warning).
/// Outlook publishes display-name TZIDs ("(UTC+01:00) Amsterdam, …") and "Customized Time Zone" ids that the
/// default resolver maps to UTC, which would shift every meeting.
/// </summary>
internal static partial class IcsTimeZones
{
    private static readonly ConcurrentDictionary<string, DateTimeZone> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object InstallLock = new();
    private static bool _installed;

    [ThreadStatic]
    private static ParseContext? t_context;

    /// <summary>Receives a sanitized warning (never feed content beyond the TZID name).</summary>
    public static Action<string>? Warning { get; set; }

    public static void Install()
    {
        lock (InstallLock)
        {
            if (_installed)
            {
                return;
            }

            TimeZoneResolvers.TimeZoneResolver = Resolve;
            _installed = true;
        }
    }

    /// <summary>Makes a calendar's VTIMEZONE definitions and the floating/local zone available to the resolver on this thread.</summary>
    public static IDisposable Use(Calendar calendar, TimeZoneInfo localZone)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var previous = t_context;
        t_context = new ParseContext(BuildCalendarZones(calendar), localZone);
        return new Scope(previous);
    }

    public static DateTimeZone Resolve(string tzId)
    {
        if (string.IsNullOrWhiteSpace(tzId))
        {
            return LocalZone();
        }

        var id = Normalize(tzId);
        if (Cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var known = DateTimeZoneProviders.Tzdb.GetZoneOrNull(id)
                    ?? FromWindowsId(id)
                    ?? FromDisplayName(id);
        if (known is not null)
        {
            Cache[id] = known;
            return known;
        }

        if (t_context?.CalendarZones.TryGetValue(id, out var fromVtimezone) == true)
        {
            return fromVtimezone;
        }

        // Not cached: a fixed offset is only a guess, and another feed may define this TZID with full DST rules.
        var fromOffset = FromUtcOffsetPrefix(id);
        if (fromOffset is not null)
        {
            return fromOffset;
        }

        Warning?.Invoke("Unrecognized TZID; times interpreted in the local time zone.");
        return LocalZone();
    }

    private static DateTimeZone LocalZone()
    {
        var local = t_context?.LocalZone ?? TimeZoneInfo.Local;
        return DateTimeZoneProviders.Bcl.GetZoneOrNull(local.Id) ?? BclDateTimeZoneFrom(local);
    }

    private static DateTimeZone BclDateTimeZoneFrom(TimeZoneInfo zone) => NodaTime.TimeZones.BclDateTimeZone.FromTimeZoneInfo(zone);

    private static DateTimeZone? FromWindowsId(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? BclDateTimeZoneFrom(zone) : null;

    private static DateTimeZone? FromDisplayName(string id)
    {
        var match = TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(z =>
            string.Equals(Normalize(z.DisplayName), id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Normalize(z.StandardName), id, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : BclDateTimeZoneFrom(match);
    }

    private static DateTimeZone? FromUtcOffsetPrefix(string id)
    {
        var m = OffsetPrefixRegex().Match(id);
        if (!m.Success)
        {
            return null;
        }

        var sign = m.Groups["sign"].Value == "-" ? -1 : 1;
        var offset = new TimeSpan(int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture), m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0, 0) * sign;
        return DateTimeZone.ForOffset(Offset.FromTimeSpan(offset));
    }

    private static Dictionary<string, DateTimeZone> BuildCalendarZones(Calendar calendar)
    {
        var map = new Dictionary<string, DateTimeZone>(StringComparer.OrdinalIgnoreCase);
        foreach (var vtz in calendar.TimeZones)
        {
            if (string.IsNullOrWhiteSpace(vtz.TzId))
            {
                continue;
            }

            try
            {
                var zone = FromVTimeZone(vtz);
                if (zone is not null)
                {
                    map[Normalize(vtz.TzId)] = zone;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            {
                Warning?.Invoke("A VTIMEZONE definition could not be interpreted.");
            }
        }

        return map;
    }

    /// <summary>Finds the system zone whose rules match the VTIMEZONE (offsets, DST months and weeks).</summary>
    private static DateTimeZone? FromVTimeZone(VTimeZone vtz)
    {
        var standard = vtz.TimeZoneInfos.Where(i => string.Equals(i.Name, "STANDARD", StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => i.DtStart?.Value).FirstOrDefault();
        var daylight = vtz.TimeZoneInfos.Where(i => string.Equals(i.Name, "DAYLIGHT", StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => i.DtStart?.Value).FirstOrDefault();
        if (standard?.OffsetTo is null)
        {
            return null;
        }

        var standardOffset = standard.OffsetTo.Offset;
        var candidates = TimeZoneInfo.GetSystemTimeZones().Where(z => z.BaseUtcOffset == standardOffset);
        if (daylight?.OffsetTo is null)
        {
            var noDst = candidates.FirstOrDefault(z => !z.SupportsDaylightSavingTime);
            return noDst is not null ? BclDateTimeZoneFrom(noDst) : DateTimeZone.ForOffset(Offset.FromTimeSpan(standardOffset));
        }

        var delta = daylight.OffsetTo.Offset - standardOffset;
        var (dstMonth, dstWeek) = MonthAndWeek(daylight);
        var (stdMonth, stdWeek) = MonthAndWeek(standard);
        var year = DateTime.UtcNow.Year;
        var match = candidates
            .Where(z => z.SupportsDaylightSavingTime)
            .OrderBy(z => z.Id, StringComparer.Ordinal)
            .FirstOrDefault(z => z.GetAdjustmentRules().Any(r =>
                r.DateStart.Year <= year && r.DateEnd.Year >= year &&
                r.DaylightDelta == delta &&
                (dstMonth is null || r.DaylightTransitionStart.Month == dstMonth) &&
                (stdMonth is null || r.DaylightTransitionEnd.Month == stdMonth) &&
                (dstWeek is null || r.DaylightTransitionStart.IsFixedDateRule || r.DaylightTransitionStart.Week == dstWeek) &&
                (stdWeek is null || r.DaylightTransitionEnd.IsFixedDateRule || r.DaylightTransitionEnd.Week == stdWeek)));
        if (match is not null)
        {
            return BclDateTimeZoneFrom(match);
        }

        Warning?.Invoke("A VTIMEZONE did not match any system time zone; using its standard offset.");
        return DateTimeZone.ForOffset(Offset.FromTimeSpan(standardOffset));
    }

    private static (int? Month, int? Week) MonthAndWeek(Ical.Net.VTimeZoneInfo info)
    {
        var rule = info.RecurrenceRule;
        if (rule is null)
        {
            return (null, null);
        }

        int? month = rule.ByMonth.Count > 0 ? rule.ByMonth[0] : null;
        int? week = null;
        if (rule.ByDay.Count > 0 && rule.ByDay[0].Offset is { } offset)
        {
            week = offset < 0 ? 5 : offset;
        }

        return (month, week);
    }

    private static string Normalize(string tzId) =>
        WhitespaceRegex().Replace(tzId.Replace("\\,", ",", StringComparison.Ordinal).Replace("\"", string.Empty, StringComparison.Ordinal), " ").Trim();

    [GeneratedRegex(@"^\(?(?:UTC|GMT)\s*(?<sign>[+-])(?<h>\d{1,2})(?::?(?<m>\d{2}))?\)?", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record ParseContext(Dictionary<string, DateTimeZone> CalendarZones, TimeZoneInfo LocalZone);

    private sealed class Scope(ParseContext? previous) : IDisposable
    {
        public void Dispose() => t_context = previous;
    }
}
