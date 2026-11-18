namespace CalendarBell.Infrastructure.Ics;

/// <summary>The downloaded content is not a complete, valid iCalendar document. Messages never contain feed content.</summary>
public sealed class IcsFormatException : FormatException
{
    public IcsFormatException()
    {
    }

    public IcsFormatException(string message)
        : base(message)
    {
    }

    public IcsFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
