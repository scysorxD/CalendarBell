using Microsoft.Extensions.Time.Testing;

namespace CalendarBell.Core.Tests.Fakes;

internal static class FakeClock
{
    /// <summary>
    /// Moves fake time forward in 1-second steps and lets background work settle after each step. A single big
    /// jump would let FakeTimeProvider fire later timers while a background callback is still re-arming an
    /// earlier one (a race that cannot happen with real time, which never jumps minutes in microseconds).
    /// </summary>
    public static async Task StepToAsync(this FakeTimeProvider time, DateTimeOffset target, Func<Task> settle)
    {
        while (time.GetUtcNow() < target)
        {
            var next = time.GetUtcNow() + TimeSpan.FromSeconds(1);
            time.SetUtcNow(next > target ? target : next);
            await settle();
        }

        await settle();
    }
}
