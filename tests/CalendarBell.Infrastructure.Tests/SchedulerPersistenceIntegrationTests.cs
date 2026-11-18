using CalendarBell.Core.Interfaces;
using CalendarBell.Core.Models;
using CalendarBell.Core.Services;
using CalendarBell.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace CalendarBell.Infrastructure.Tests;

/// <summary>The real scheduler on the real SQLite store: restarts must neither lose a snooze nor repeat a phase.</summary>
public sealed class SchedulerPersistenceIntegrationTests : IDisposable
{
    private static readonly Guid CalendarA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset T1030 = new(2026, 11, 18, 10, 30, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CalendarBellTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(T1030.AddMinutes(-30));
    private readonly List<DueReminder> _fired = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(ReminderScheduler Scheduler, CalendarBellDatabase Db)> StartAsync()
    {
        var db = CalendarBellDatabase.Open(new DataPaths(_root));
        var scheduler = new ReminderScheduler(new SqliteReminderStore(db), new Sink(_fired), _time, ReminderOptions.Default);
        scheduler.UpdateCalendarState(CalendarA, "Calendar A", true, _time.GetUtcNow());
        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.IdleAsync();
        return (scheduler, db);
    }

    [Fact]
    public async Task Snooze_survives_restart_and_initial_phase_is_not_repeated()
    {
        var occurrence = new EventOccurrence(CalendarA, "uid", "key-1", T1030, T1030.AddMinutes(30), Availability.Busy, false, false, null, "fp");
        var (scheduler, db) = await StartAsync();
        await scheduler.ReconcileAsync(CalendarA, [occurrence], CancellationToken.None);
        await StepToAsync(scheduler, T1030.AddMinutes(-15));
        _time.SetUtcNow(T1030.AddMinutes(-14));
        await scheduler.SnoozeAsync(["key-1"], 5, CancellationToken.None);
        await scheduler.DisposeAsync();
        db.Dispose();
        Assert.Single(_fired);

        (scheduler, db) = await StartAsync();
        await scheduler.ReconcileAsync(CalendarA, [occurrence], CancellationToken.None);
        await StepToAsync(scheduler, T1030.AddMinutes(-5));
        await scheduler.DisposeAsync();
        db.Dispose();

        Assert.Equal(2, _fired.Count);
        Assert.Equal(ReminderKind.Snooze, _fired[1].Kind);
    }

    private async Task StepToAsync(ReminderScheduler scheduler, DateTimeOffset target)
    {
        while (_time.GetUtcNow() < target)
        {
            var next = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
            _time.SetUtcNow(next > target ? target : next);
            await scheduler.IdleAsync();
        }
    }

    private sealed class Sink(List<DueReminder> fired) : IReminderSink
    {
        public void Enqueue(IReadOnlyList<DueReminder> due)
        {
            lock (fired)
            {
                fired.AddRange(due);
            }
        }

        public void RetractToast(string tag, string group)
        {
        }
    }
}
