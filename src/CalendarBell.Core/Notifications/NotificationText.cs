using System.Globalization;
using System.Text;
using System.Xml;

namespace CalendarBell.Core.Notifications;

/// <summary>Treats ICS/user text as untrusted: strips control/format characters, collapses whitespace, truncates.</summary>
public static class NotificationText
{
    public const int MaxTitleLength = 80;
    public const int MaxNameLength = 40;

    public static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(value.Length, maxLength * 2));
        var lastWasSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var isSpace = Rune.IsWhiteSpace(rune);
            if (isSpace || category is UnicodeCategory.Control)
            {
                if (isSpace && !lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (category is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse || !IsXmlSafe(rune))
            {
                continue;
            }

            sb.Append(rune.ToString());
            lastWasSpace = false;
        }

        var text = sb.ToString().Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = maxLength - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut].TrimEnd() + "…";
    }

    private static bool IsXmlSafe(Rune rune)
    {
        var s = rune.ToString();
        return s.Length == 2 ? XmlConvert.IsXmlSurrogatePair(s[1], s[0]) : XmlConvert.IsXmlChar(s[0]);
    }
}
