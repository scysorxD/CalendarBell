using CalendarBell.Core.Interfaces;

namespace CalendarBell.Core.Tests.Fakes;

internal sealed class RecordingSink : IReminderSink
{
    private readonly object _lock = new();

    public List<IReadOnlyList<DueReminder>> Enqueued { get; } = [];
    public List<(string Tag, string Group)> Retracted { get; } = [];

    public IReadOnlyList<DueReminder> AllFired
    {
        get
        {
            lock (_lock)
            {
                return Enqueued.SelectMany(b => b).ToList();
            }
        }
    }

    public void Enqueue(IReadOnlyList<DueReminder> due)
    {
        lock (_lock)
        {
            Enqueued.Add(due);
        }
    }

    public void RetractToast(string tag, string group)
    {
        lock (_lock)
        {
            Retracted.Add((tag, group));
        }
    }
}
