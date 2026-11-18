using CalendarBell.Core.Models;
using CalendarBell.Core.Services;

namespace CalendarBell.Core.Tests;

public class CalendarValidationTests
{
    private static readonly CalendarSource Existing = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Calendar A", "enc", true);

    [Theory]
    [InlineData("Calendar B", "Calendar B")]
    [InlineData("  Calendar B  ", "Calendar B")]
    [InlineData("Ñandú", "Ñandú")]
    [InlineData("Personal", "Personal")]
    public void Valid_names_are_trimmed(string input, string expected)
    {
        var result = CalendarValidation.ValidateName(input, null, [Existing]);

        Assert.Null(result.ErrorKey);
        Assert.Equal(expected, result.Name);
    }

    [Theory]
    [InlineData(null, "Name_Required")]
    [InlineData("", "Name_Required")]
    [InlineData("   ", "Name_Required")]
    [InlineData("calendar a", "Name_Duplicate")]
    [InlineData("CALENDAR A", "Name_Duplicate")]
    [InlineData("Bad\u0001Name", "Name_Invalid")]
    public void Invalid_names_are_rejected(string? input, string errorKey)
    {
        Assert.Equal(errorKey, CalendarValidation.ValidateName(input, null, [Existing]).ErrorKey);
    }

    [Fact]
    public void Forty_characters_is_the_limit()
    {
        Assert.Null(CalendarValidation.ValidateName(new string('a', 40), null, []).ErrorKey);
        Assert.Equal("Name_TooLong", CalendarValidation.ValidateName(new string('a', 41), null, []).ErrorKey);
    }

    [Fact]
    public void Length_counts_user_perceived_characters_not_utf16_units()
    {
        var forty = string.Concat(Enumerable.Repeat("📅", 40));

        Assert.Null(CalendarValidation.ValidateName(forty, null, []).ErrorKey);
        Assert.Equal("Name_TooLong", CalendarValidation.ValidateName(forty + "📅", null, []).ErrorKey);
    }

    [Fact]
    public void Editing_a_calendar_may_keep_or_recase_its_own_name()
    {
        Assert.Null(CalendarValidation.ValidateName("calendar a", Existing.CalendarId, [Existing]).ErrorKey);
    }
}
