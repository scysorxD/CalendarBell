using CalendarBell.Core.Models;

namespace CalendarBell.Core.Interfaces;

/// <summary>Downloads an ICS feed (spec §5.1). Results never carry the URL.</summary>
public interface ICalendarFeedClient
{
    Task<CalendarFetchResult> FetchAsync(CalendarSource calendar, CancellationToken ct);

    Task<CalendarFetchResult> FetchAsync(CalendarSource calendar, ConditionalRequest? conditional, CancellationToken ct);

    /// <summary>For "Test connection": a plain URL that is not stored anywhere.</summary>
    Task<CalendarFetchResult> FetchUrlAsync(string url, ConditionalRequest? conditional, CancellationToken ct);
}

/// <summary>Encrypts/decrypts calendar URLs at rest (DPAPI, current user).</summary>
public interface IUrlProtector
{
    string Protect(string url);

    string Unprotect(string protectedUrl);
}

public interface ICalendarRepository
{
    Task<IReadOnlyList<CalendarSource>> GetAllAsync(CancellationToken ct);

    Task<CalendarSource?> GetAsync(Guid id, CancellationToken ct);

    Task SaveAsync(CalendarSource calendar, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface ISyncStateStore
{
    Task<IReadOnlyList<SyncRecord>> GetAllAsync(CancellationToken ct);

    Task SaveAsync(SyncRecord state, CancellationToken ct);

    Task DeleteAsync(Guid calendarId, CancellationToken ct);
}
