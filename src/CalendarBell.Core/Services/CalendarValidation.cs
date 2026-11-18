using System.Globalization;
using CalendarBell.Core.Models;

namespace CalendarBell.Core.Services;

public sealed record NameValidationResult(string Name, string? ErrorKey);

/// <summary>Calendar name rules (spec §3.2): required, trimmed, 1–40 user-perceived characters, unique ignoring case.</summary>
public static class CalendarValidation
{
    public const int MaxNameLength = 40;

    public static NameValidationResult ValidateName(string? input, Guid? editingCalendarId, IEnumerable<CalendarSource> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var name = input?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return new NameValidationResult(name, "Name_Required");
        }

        if (name.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
        {
            return new NameValidationResult(name, "Name_Invalid");
        }

        if (new StringInfo(name).LengthInTextElements > MaxNameLength)
        {
            return new NameValidationResult(name, "Name_TooLong");
        }

        var duplicate = existing.Any(c =>
            c.CalendarId != editingCalendarId &&
            string.Compare(c.Name.Trim(), name, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase) == 0);
        return duplicate ? new NameValidationResult(name, "Name_Duplicate") : new NameValidationResult(name, null);
    }
}
