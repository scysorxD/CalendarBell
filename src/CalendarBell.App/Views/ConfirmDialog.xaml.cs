using System.Windows;

namespace CalendarBell.App.Views;

/// <summary>Small themed confirmation dialog (used instead of MessageBox for non-critical questions).</summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirmText, bool destructive)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Body.Text = message;
        ConfirmButton.Content = confirmText;
        if (!destructive)
        {
            ConfirmButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
        }
    }

    public static bool Ask(Window? owner, string title, string message, string confirmText, bool destructive)
    {
        var dialog = new ConfirmDialog(title, message, confirmText, destructive);
        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
