using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CalendarBell.App.ViewModels;

namespace CalendarBell.App.Views;

public partial class CalendarEditorWindow : Window
{
    private readonly CalendarEditorViewModel _viewModel;
    private bool _syncing;

    public CalendarEditorWindow(CalendarEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        UrlPassword.Password = viewModel.Url; // masked by default; revealed only through "Show URL"
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.CloseRequested += (_, saved) =>
        {
            DialogResult = saved;
            Close();
        };
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelChanged;
            viewModel.Dispose();
        };
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            _syncing = true;
            _viewModel.Url = ((PasswordBox)sender).Password;
            _syncing = false;
        }
    }

    private void OnUrlPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        try
        {
            var clipboard = Clipboard.GetDataObject();
            // Let WPF handle ordinary text paste, including caret position and selection.
            if (clipboard is null || clipboard.GetDataPresent(DataFormats.UnicodeText) ||
                clipboard.GetDataPresent(DataFormats.Text) ||
                !clipboard.TryGetData<MemoryStream>("UniformResourceLocatorW", autoConvert: false, out var link))
            {
                return;
            }

            // Windows can copy a hyperlink as a null-terminated UTF-16 URL without plain text.
            // Treat it as a complete URL and keep it in the editor's existing masked input flow.
            if (link.Length > (UrlPassword.MaxLength + 1) * sizeof(char) || link.Length % sizeof(char) != 0)
            {
                return;
            }

            var url = Encoding.Unicode.GetString(link.ToArray()).TrimEnd('\0');
            if (url.Length == 0 || url.Length > UrlPassword.MaxLength || url.Any(char.IsControl))
            {
                return;
            }

            _viewModel.Url = url;
            e.Handled = true;
        }
        catch (ExternalException)
        {
            // Another application can have the clipboard open. The user can retry the paste.
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarEditorViewModel.Url) && !_syncing && UrlPassword.Password != _viewModel.Url)
        {
            _syncing = true;
            UrlPassword.Password = _viewModel.Url;
            _syncing = false;
        }
        else if (e.PropertyName == nameof(CalendarEditorViewModel.IsUrlVisible))
        {
            if (_viewModel.IsUrlVisible)
            {
                UrlText.Focus();
                UrlText.CaretIndex = UrlText.Text.Length;
            }
            else
            {
                UrlPassword.Focus();
            }
        }
    }
}
