using CalendarBell.Core.Models;

namespace CalendarBell.Core.Interfaces;

public sealed record ReminderSnapshot(IReadOnlyList<ReminderRecord> Records, IReadOnlyList<EventOccurrence> Occurrences);

/// <summary>Durable storage of reminder state and the normalized occurrence cache. Implementations must be transactional.</summary>
public interface IReminderStore
{
    Task<ReminderSnapshot> LoadAsync(CancellationToken ct);

    /// <summary>Upserts records and occurrences atomically (one transaction).</summary>
    Task SaveAsync(IReadOnlyList<ReminderRecord> records, IReadOnlyList<EventOccurrence> occurrences, CancellationToken ct);

    Task DeleteCalendarDataAsync(Guid calendarId, CancellationToken ct);

    /// <summary>Deletes records and occurrences of events that started before <paramref name="olderThanUtc"/>.</summary>
    Task PruneAsync(DateTimeOffset olderThanUtc, CancellationToken ct);
}

/// <summary>A toast as shown to the user: which occurrences it represents, so actions survive restarts.</summary>
public sealed record ToastBatchRecord(
    string BatchId,
    string Group,
    IReadOnlyList<string> OccurrenceKeys,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);

public interface IToastBatchStore
{
    Task SaveBatchAsync(ToastBatchRecord batch, CancellationToken ct);

    Task<ToastBatchRecord?> GetBatchAsync(string batchId, CancellationToken ct);

    Task PruneBatchesAsync(DateTimeOffset olderThanUtc, CancellationToken ct);
}
