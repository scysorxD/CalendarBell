using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
