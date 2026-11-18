using System.Globalization;
using System.Xml.Linq;
using CalendarBell.Core.Resources;

namespace CalendarBell.Core.Notifications;

/// <summary>
/// Builds the ToastGeneric XML payload with <see cref="XElement"/> so every external value is escaped.
/// Snooze is an app-handled action with a selection input (absolute target), never the Windows system snooze.
/// </summary>
public static class ToastXmlBuilder
{
    public const string SnoozeInputId = "snoozeTarget";

    public static string Build(NotificationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var prefix = batch.Kind == BatchKind.Test ? "test-" : string.Empty;

        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", new XAttribute("hint-maxLines", "2"), batch.Title));
        foreach (var line in batch.Lines.Take(2))
        {
            binding.Add(new XElement("text", line));
        }

        var actions = new XElement("actions");
        if (batch.Kind == BatchKind.Summary)
        {
            actions.Add(Action(Strings.Get("Action_ViewReminders"), prefix + "view", batch.BatchId));
            actions.Add(Action(Strings.Get("Action_DismissAll"), prefix + "dismiss", batch.BatchId));
        }
        else
        {
            if (batch.SnoozeOptions.Count > 0)
            {
                var input = new XElement("input",
                    new XAttribute("id", SnoozeInputId),
                    new XAttribute("type", "selection"),
                    new XAttribute("defaultInput", batch.SnoozeOptions[0].ToString(CultureInfo.InvariantCulture)));
                foreach (var minutes in batch.SnoozeOptions)
                {
                    input.Add(new XElement("selection",
                        new XAttribute("id", minutes.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("content", DisplayFormatter.SnoozeLabel(minutes))));
                }

                actions.Add(input);
                actions.Add(Action(Strings.Get("Action_Snooze"), prefix + "snooze", batch.BatchId));
            }

            actions.Add(Action(Strings.Get("Action_Dismiss"), prefix + "dismiss", batch.BatchId));
        }

        var audio = batch.PlaySound
            ? new XElement("audio", new XAttribute("src", "ms-winsoundevent:Notification.Reminder"))
            : new XElement("audio", new XAttribute("silent", "true"));

        var toast = new XElement("toast",
            new XAttribute("scenario", "reminder"),
            new XAttribute("launch", ToastArguments.Format(prefix + "open", batch.BatchId)),
            new XElement("visual", binding),
            actions,
            audio);

        return toast.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Action(string content, string action, string batchId) =>
        new("action",
            new XAttribute("content", content),
            new XAttribute("arguments", ToastArguments.Format(action, batchId)),
            new XAttribute("activationType", "foreground"));
}
