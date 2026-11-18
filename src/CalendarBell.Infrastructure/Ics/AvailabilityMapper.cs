using CalendarBell.Core.Models;
using Ical.Net.CalendarComponents;

namespace CalendarBell.Infrastructure.Ics;

/// <summary>
/// Maps what the feed actually states to an availability, in priority order:
/// X-MICROSOFT-CDO-BUSYSTATUS → STATUS:TENTATIVE → SUMMARY that is exactly an availability word →
/// explicit TRANSP:OPAQUE (Busy) → Unknown. Free time (TRANSP:TRANSPARENT, BUSYSTATUS FREE, SUMMARY "Free")
/// returns null and is ignored. Nothing is inferred beyond that.
/// </summary>
internal static class AvailabilityMapper
{
    public static string? Map(CalendarEvent ev)
    {
        var busyStatus = ev.Properties.Get<string>("X-MICROSOFT-CDO-BUSYSTATUS")?.Trim().ToUpperInvariant();
        switch (busyStatus)
        {
            case "BUSY":
                return Availability.Busy;
            case "TENTATIVE":
                return Availability.Tentative;
            case "OOF":
                return Availability.OutOfOffice;
            case "FREE":
                return null;
            case "WORKINGELSEWHERE":
                return Availability.Unknown;
        }

        if (string.Equals(ev.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(ev.Status, "TENTATIVE", StringComparison.OrdinalIgnoreCase))
        {
            return Availability.Tentative;
        }

        var fromSummary = FromSummaryWord(ev.Summary);
        if (fromSummary is not null)
        {
            return fromSummary == "Free" ? null : fromSummary;
        }

        return string.Equals(ev.Transparency, "OPAQUE", StringComparison.OrdinalIgnoreCase)
            ? Availability.Busy
            : Availability.Unknown;
    }

    /// <summary>Availability-only feeds publish the availability as SUMMARY; such a summary is not a title.</summary>
    public static bool IsAvailabilityWord(string? summary) => FromSummaryWord(summary) is not null;

    private static string? FromSummaryWord(string? summary) => summary?.Trim().ToUpperInvariant() switch
    {
        "BUSY" => Availability.Busy,
        "TENTATIVE" => Availability.Tentative,
        "OUT OF OFFICE" or "OOF" or "AWAY" => Availability.OutOfOffice,
        "FREE" => "Free",
        _ => null,
    };
}
