using System.Globalization;
using CalendarBell.Core.Models;
using CalendarBell.Core.Resources;

namespace CalendarBell.Core.Notifications;

/// <summary>
/// Formats instants for display in the user's local zone and Windows regional format (12/24 h).
/// The default instance resolves <see cref="TimeZoneInfo.Local"/> and <see cref="CultureInfo.CurrentCulture"/>
/// on every call so a time zone or region change is picked up without restarting.
/// </summary>
public sealed class DisplayFormatter
{
    private readonly TimeZoneInfo? _zone;
    private readonly CultureInfo? _culture;

    public DisplayFormatter()
    {
    }

    public DisplayFormatter(TimeZoneInfo zone, CultureInfo culture)
    {
        _zone = zone;
        _culture = culture;
    }

    public TimeZoneInfo Zone => _zone ?? TimeZoneInfo.Local;

    public CultureInfo Culture => _culture ?? CultureInfo.CurrentCulture;

    public DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    public string Time(DateTimeOffset utc) => ToLocal(utc).ToString("t", Culture);

    public string Date(DateTimeOffset utc) => ToLocal(utc).ToString("D", Culture);

    public static string AvailabilityLabel(string availability) => availability switch
    {
        Availability.Busy => Strings.Get("Availability_Busy"),
        Availability.Tentative => Strings.Get("Availability_Tentative"),
        Availability.OutOfOffice => Strings.Get("Availability_OutOfOffice"),
        _ => Strings.Get("Availability_Unknown"),
    };

    public static string SnoozeLabel(int minutesBeforeStart) => Strings.Get("Snooze_" + minutesBeforeStart.ToString(CultureInfo.InvariantCulture));
}
