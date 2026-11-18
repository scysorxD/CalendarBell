using System.Globalization;

namespace CalendarBell.Core.Notifications;

public enum ToastActionKind
{
    Snooze,
    Dismiss,
    Open,
    View,
    TestAction,
}

/// <summary>A validated toast activation: what was clicked, on which batch, and the chosen snooze target.</summary>
public sealed record ToastAction(ToastActionKind Kind, string BatchId, int? SnoozeMinutes, string RawAction);

/// <summary>Formats and strictly parses toast arguments (<c>action=…&amp;b=…</c>). Anything unexpected is rejected.</summary>
public static class ToastArguments
{
    private const int BatchIdLength = 12;

    public static string NewBatchId() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().AsSpan(0, BatchIdLength / 2));

    public static string Format(string action, string batchId) => $"action={action}&b={batchId}";

    public static ToastAction? Parse(string? arguments, IReadOnlyDictionary<string, string>? userInput)
    {
        if (string.IsNullOrWhiteSpace(arguments) || arguments.Length > 200)
        {
            return null;
        }

        string? action = null;
        string? batchId = null;
        foreach (var part in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                return null;
            }

            var key = part[..eq];
            var value = part[(eq + 1)..];
            switch (key)
            {
                case "action":
                    action = value;
                    break;
                case "b":
                    batchId = value;
                    break;
                default:
                    return null;
            }
        }

        if (action is null || batchId is null || !IsValidBatchId(batchId))
        {
            return null;
        }

        ToastActionKind? kind = action switch
        {
            "snooze" => ToastActionKind.Snooze,
            "dismiss" => ToastActionKind.Dismiss,
            "open" => ToastActionKind.Open,
            "view" => ToastActionKind.View,
            "test-snooze" or "test-dismiss" or "test-open" or "test-view" => ToastActionKind.TestAction,
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        int? minutes = null;
        if (userInput is not null &&
            userInput.TryGetValue(ToastXmlBuilder.SnoozeInputId, out var raw) &&
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed is 10 or 5 or 0)
        {
            minutes = parsed;
        }

        return new ToastAction(kind.Value, batchId, minutes, action);
    }

    private static bool IsValidBatchId(string id) =>
        id.Length == BatchIdLength && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
