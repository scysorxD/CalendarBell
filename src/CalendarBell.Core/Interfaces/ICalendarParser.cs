using CalendarBell.Core.Models;

namespace CalendarBell.Core.Interfaces;

/// <summary>Parses an ICS document and expands occurrences that start inside the window (spec §5.2).</summary>
public interface ICalendarParser
{
    /// <exception cref="FormatException">The content is not a complete, valid iCalendar document (the caller keeps its cache).</exception>
    IReadOnlyList<EventOccurrence> Parse(string ics, Guid calendarId, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc);
}
