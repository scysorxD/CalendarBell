using System.Globalization;
using System.Resources;

namespace CalendarBell.Core.Resources;

/// <summary>
/// Access to localized UI text (Strings.resx, Strings.es-AR.resx; edit scripts/strings.json and run scripts/resx.ps1).
/// <see cref="Culture"/> defaults to en-US so the initial UI is English regardless of the OS language.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new("CalendarBell.Core.Resources.Strings", typeof(Strings).Assembly);

    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("en-US");

    public static string Get(string key) => Get(key, Culture);

    public static string Get(string key, CultureInfo culture) => Manager.GetString(key, culture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
