using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CalendarBell.App.ViewModels;

namespace CalendarBell.App.Views;

/// <summary>Maps a calendar status tone to a Fluent theme brush (works in light, dark and high contrast).</summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusTone.Success => "SystemFillColorSuccessBrush",
            StatusTone.Caution => "SystemFillColorCautionBrush",
            StatusTone.Critical => "SystemFillColorCriticalBrush",
            _ => "TextFillColorTertiaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? SystemColors.GrayTextBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value is a non-empty string or true; collapsed otherwise. ConverterParameter=Invert flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            null => false,
            _ => true,
        };
        if (parameter is "Invert")
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Test result colour: success / failure / neutral (running or unknown).</summary>
public sealed class TestResultBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            true => "SystemFillColorSuccessBrush",
            false => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? SystemColors.ControlTextBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
