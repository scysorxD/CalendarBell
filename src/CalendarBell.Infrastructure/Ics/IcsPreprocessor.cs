using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CalendarBell.Infrastructure.Ics;

/// <summary>
/// Cheap structural checks and normalisation before handing text to Ical.Net:
/// rejects non-iCalendar and truncated bodies, unfolds content lines, and gives VEVENTs without UID a
/// deterministic fallback UID (Ical.Net would otherwise invent a random one on every load).
/// </summary>
internal static partial class IcsPreprocessor
{
    private static readonly string[] FingerprintProperties = ["DTSTART", "DTEND", "DURATION", "SUMMARY", "RRULE", "RDATE", "RECURRENCE-ID"];

    public static string Prepare(string ics, out int generatedUids)
    {
        generatedUids = 0;
        if (string.IsNullOrWhiteSpace(ics))
        {
            throw new IcsFormatException("The response body is empty.");
        }

        var text = ics.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (!text.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new IcsFormatException("The response is not an iCalendar document.");
        }

        var unfolded = FoldRegex().Replace(text, string.Empty);
        var lines = unfolded.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0 || !lines[^1].Trim().Equals("END:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new IcsFormatException("The iCalendar document is truncated (missing END:VCALENDAR).");
        }

        var output = new StringBuilder(unfolded.Length + 64);
        List<string>? eventLines = null;
        var nestedDepth = 0;
        foreach (var line in lines)
        {
            if (eventLines is null)
            {
                if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    eventLines = [line];
                    nestedDepth = 0;
                }
                else
                {
                    output.Append(line).Append("\r\n");
                }

                continue;
            }

            eventLines.Add(line);
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                nestedDepth++;
            }
            else if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (nestedDepth > 0)
                {
                    nestedDepth--;
                    continue;
                }

                if (!HasTopLevelUid(eventLines))
                {
                    eventLines.Insert(1, "UID:" + IcsCalendarParser.FallbackUidPrefix + FallbackHash(eventLines));
                    generatedUids++;
                }

                foreach (var l in eventLines)
                {
                    output.Append(l).Append("\r\n");
                }

                eventLines = null;
            }
        }

        if (eventLines is not null)
        {
            throw new IcsFormatException("The iCalendar document is truncated (unclosed VEVENT).");
        }

        return output.ToString();
    }

    private static bool HasTopLevelUid(List<string> eventLines)
    {
        var depth = 0;
        for (var i = 1; i < eventLines.Count - 1; i++)
        {
            var line = eventLines[i];
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                depth--;
            }
            else if (depth == 0 && PropertyName(line).Equals("UID", StringComparison.OrdinalIgnoreCase) && line.Contains(':', StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string FallbackHash(List<string> eventLines)
    {
        var relevant = eventLines
            .Where(l => FingerprintProperties.Contains(PropertyName(l), StringComparer.OrdinalIgnoreCase))
            .Select(l => l.Trim());
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", relevant)));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 12));
    }

    private static string PropertyName(string line)
    {
        var end = line.IndexOfAny([':', ';']);
        return end <= 0 ? line : line[..end];
    }

    // RFC 5545 §3.1: a CRLF followed by a single space or tab is a line continuation.
    [GeneratedRegex("\r?\n[ \t]")]
    private static partial Regex FoldRegex();
}
