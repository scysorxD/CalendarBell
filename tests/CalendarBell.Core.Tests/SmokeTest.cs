using CalendarBell.Core;

namespace CalendarBell.Core.Tests;

public class SmokeTest
{
    [Fact]
    public void Calendar_source_keeps_identity()
    {
        var id = Guid.NewGuid();
        var source = new Models.CalendarSource(id, "Calendar A", "enc", true);
        Assert.Equal(id, source.CalendarId);
    }
}
