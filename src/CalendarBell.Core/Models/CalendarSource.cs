namespace CalendarBell.Core.Models;

/// <summary>A configured ICS calendar. The URL is only ever held encrypted (DPAPI) in this model.</summary>
public sealed record CalendarSource(
    Guid CalendarId,
    string Name,
    string EncryptedUrl,
    bool Enabled);
