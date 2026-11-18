using System.Windows.Markup;
using CalendarBell.Core.Resources;

namespace CalendarBell.App.Localization;

/// <summary>XAML access to localized strings: <c>Text="{l:Loc Settings_Title}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
