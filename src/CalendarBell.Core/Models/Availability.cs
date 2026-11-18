namespace CalendarBell.Core.Models;

/// <summary>Availability values exposed by ICS feeds. Never inferred beyond what the feed states.</summary>
public static class Availability
{
    public const string Busy = "Busy";
    public const string Tentative = "Tentative";
    public const string OutOfOffice = "OutOfOffice";
    public const string Unknown = "Unknown";

    public static bool IsKnown(string? value) =>
        value is Busy or Tentative or OutOfOffice or Unknown;
}
