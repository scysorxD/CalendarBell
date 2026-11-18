using System.Collections.Concurrent;
using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;

namespace CalendarBell.Core.Tests.Fakes;

/// <summary>Test double for <see cref="IReminderStore"/>; survives "restarts" by being shared between scheduler instances.</summary>
internal sealed class InMemoryReminderStore : IReminderStore, IToastBatchStore
{
    public ConcurrentDictionary<string, ReminderRecord> Records { get; } = new();
    public ConcurrentDictionary<string, EventOccurrence> Occurrences { get; } = new();
    public ConcurrentDictionary<string, ToastBatchRecord> Batches { get; } = new();
    public int SaveCalls { get; private set; }
    public bool FailSaves { get; set; }

    public Task<ReminderSnapshot> LoadAsync(CancellationToken ct) =>
        Task.FromResult(new ReminderSnapshot(Records.Values.ToList(), Occurrences.Values.ToList()));

    public Task SaveAsync(IReadOnlyList<ReminderRecord> records, IReadOnlyList<EventOccurrence> occurrences, CancellationToken ct)
    {
        if (FailSaves)
        {
            throw new IOException("simulated storage failure");
        }

        SaveCalls++;
        foreach (var r in records)
        {
            Records[r.OccurrenceKey] = r;
        }

        foreach (var o in occurrences)
        {
            Occurrences[o.OccurrenceKey] = o;
        }

        return Task.CompletedTask;
    }

    public Task DeleteCalendarDataAsync(Guid calendarId, CancellationToken ct)
    {
        foreach (var r in Records.Values.Where(r => r.CalendarId == calendarId).ToList())
        {
            Records.TryRemove(r.OccurrenceKey, out _);
        }

        foreach (var o in Occurrences.Values.Where(o => o.CalendarId == calendarId).ToList())
        {
            Occurrences.TryRemove(o.OccurrenceKey, out _);
        }

        return Task.CompletedTask;
    }

    public Task PruneAsync(DateTimeOffset olderThanUtc, CancellationToken ct)
    {
        foreach (var r in Records.Values.Where(r => r.EventStartUtc < olderThanUtc).ToList())
        {
            Records.TryRemove(r.OccurrenceKey, out _);
            Occurrences.TryRemove(r.OccurrenceKey, out _);
        }

        return Task.CompletedTask;
    }

    public Task SaveBatchAsync(ToastBatchRecord batch, CancellationToken ct)
    {
        Batches[batch.BatchId] = batch;
        return Task.CompletedTask;
    }

    public Task<ToastBatchRecord?> GetBatchAsync(string batchId, CancellationToken ct) =>
        Task.FromResult(Batches.TryGetValue(batchId, out var b) ? b : null);

    public Task PruneBatchesAsync(DateTimeOffset olderThanUtc, CancellationToken ct)
    {
        foreach (var b in Batches.Values.Where(b => b.ExpiresUtc < olderThanUtc).ToList())
        {
            Batches.TryRemove(b.BatchId, out _);
        }

        return Task.CompletedTask;
    }
}
