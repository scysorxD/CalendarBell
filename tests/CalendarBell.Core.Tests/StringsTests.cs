using System.Globalization;
using CalendarBell.Core.Resources;

namespace CalendarBell.Core.Tests;

public class StringsTests
{
    [Fact]
    public void Default_ui_language_is_english()
    {
        Assert.Equal("Snooze", Strings.Get("Action_Snooze"));
    }

    [Fact]
    public void Spanish_resources_are_available_for_es_AR()
    {
        // Explicit culture: tests run in parallel, so the global Strings.Culture must never be mutated here.
        Assert.Equal("Posponer", Strings.Get("Action_Snooze", CultureInfo.GetCultureInfo("es-AR")));
    }

    [Fact]
    public void Missing_key_falls_back_to_key()
    {
        Assert.Equal("No_Such_Key", Strings.Get("No_Such_Key"));
    }
}
