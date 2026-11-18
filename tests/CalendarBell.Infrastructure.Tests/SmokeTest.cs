namespace CalendarBell.Infrastructure.Tests;

public class SmokeTest
{
    [Fact]
    public void Test_host_runs_on_windows() => Assert.True(OperatingSystem.IsWindows());
}
