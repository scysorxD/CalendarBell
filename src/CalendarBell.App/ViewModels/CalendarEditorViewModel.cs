using System.Windows.Input;
using CalendarBell.App.Mvvm;
using CalendarBell.Core.Models;
using CalendarBell.Core.Resources;
using CalendarBell.Core.Services;
using CalendarBell.Infrastructure.Security;
using CalendarBell.Infrastructure.Sync;

namespace CalendarBell.App.ViewModels;

/// <summary>Add / Edit calendar dialog (spec §3.2). Nothing is persisted until Save.</summary>
public sealed class CalendarEditorViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly CalendarSource? _original;
    private readonly IReadOnlyList<CalendarSource> _existing;
    private readonly string _originalUrl = string.Empty;
    private CancellationTokenSource? _testCts;
    private string _name;
    private string _url;
    private bool _enabled;
    private bool _isUrlVisible;
    private string? _nameError;
    private string? _urlError;
    private string? _testMessage;
    private bool? _testSucceeded;
    private bool _isTesting;
    private string? _saveError;

    public CalendarEditorViewModel(AppHost host, CalendarSource? original, IReadOnlyList<CalendarSource> existing)
    {
        _host = host;
        _original = original;
        _existing = existing;
        _name = original?.Name ?? string.Empty;
        _enabled = original?.Enabled ?? true;
        if (original is not null)
        {
            try
            {
                _originalUrl = host.UrlProtector.Unprotect(original.EncryptedUrl);
            }
            catch (UrlProtectionException)
            {
                _urlError = Strings.Get("Sync_UrlUnreadable");
            }
        }

        _url = _originalUrl;
        TestCommand = new RelayCommand(_ => _ = ToggleTestAsync());
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        ToggleUrlVisibilityCommand = new RelayCommand(_ => IsUrlVisible = !IsUrlVisible);
    }

    public event EventHandler<bool>? CloseRequested;

    public string Title => Strings.Get(_original is null ? "Editor_AddTitle" : "Editor_EditTitle");

    public ICommand TestCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand ToggleUrlVisibilityCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                NameError = null;
            }
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (Set(ref _url, value))
            {
                UrlError = null;
                TestMessage = null;
                TestSucceeded = null;
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public bool IsUrlVisible
    {
        get => _isUrlVisible;
        set
        {
            if (Set(ref _isUrlVisible, value))
            {
                Raise(nameof(UrlVisibilityLabel));
            }
        }
    }

    public string UrlVisibilityLabel => Strings.Get(IsUrlVisible ? "Editor_HideUrl" : "Editor_ShowUrl");

    public string? NameError
    {
        get => _nameError;
        set => Set(ref _nameError, value);
    }

    public string? UrlError
    {
        get => _urlError;
        set => Set(ref _urlError, value);
    }

    public string? TestMessage
    {
        get => _testMessage;
        set => Set(ref _testMessage, value);
    }

    public bool? TestSucceeded
    {
        get => _testSucceeded;
        set => Set(ref _testSucceeded, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set
        {
            if (Set(ref _isTesting, value))
            {
                Raise(nameof(TestButtonLabel));
            }
        }
    }

    public string TestButtonLabel => Strings.Get(IsTesting ? "Editor_CancelTest" : "Editor_Test");

    public string? SaveError
    {
        get => _saveError;
        set => Set(ref _saveError, value);
    }

    public void Dispose()
    {
        _testCts?.Cancel();
        _testCts?.Dispose();
    }

    private async Task ToggleTestAsync()
    {
        if (IsTesting)
        {
            await (_testCts?.CancelAsync() ?? Task.CompletedTask);
            return;
        }

        var validation = UrlPolicy.Validate(Url, _host.SyncOptions.AllowHttpLocalhost);
        if (!validation.IsValid)
        {
            UrlError = validation.Error;
            return;
        }

        if (_host.Sync is null)
        {
            return;
        }

        _testCts?.Dispose();
        _testCts = new CancellationTokenSource();
        IsTesting = true;
        TestSucceeded = null;
        TestMessage = Strings.Get("Editor_Testing");
        try
        {
            var result = await _host.Sync.TestConnectionAsync(validation.Uri!.OriginalString, _testCts.Token);
            TestSucceeded = result.Success;
            TestMessage = result.Message;
        }
        catch (OperationCanceledException)
        {
            TestSucceeded = null;
            TestMessage = Strings.Get("Editor_TestCancelled");
        }
        finally
        {
            IsTesting = false;
        }
    }

    private async Task SaveAsync()
    {
        SaveError = null;
        var nameResult = CalendarValidation.ValidateName(Name, _original?.CalendarId, _existing);
        NameError = nameResult.ErrorKey is null ? null : Strings.Get(nameResult.ErrorKey);
        var urlResult = UrlPolicy.Validate(Url, _host.SyncOptions.AllowHttpLocalhost);
        UrlError = urlResult.IsValid ? null : urlResult.Error;
        if (NameError is not null || UrlError is not null || _host.Calendars is null || _host.Sync is null)
        {
            return;
        }

        var normalizedUrl = urlResult.Uri!.OriginalString;
        var urlChanged = _original is null || !string.Equals(normalizedUrl, _originalUrl.Trim(), StringComparison.Ordinal);
        try
        {
            var calendar = new CalendarSource(
                _original?.CalendarId ?? Guid.NewGuid(),
                nameResult.Name,
                urlChanged ? _host.UrlProtector.Protect(normalizedUrl) : _original!.EncryptedUrl,
                Enabled);
            await _host.Calendars.SaveAsync(calendar, CancellationToken.None);
            if (_original is null)
            {
                await _host.Sync.CalendarAddedAsync(calendar.CalendarId, CancellationToken.None);
            }
            else
            {
                await _host.Sync.CalendarUpdatedAsync(calendar.CalendarId, urlChanged, CancellationToken.None);
            }

            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or System.Security.Cryptography.CryptographicException)
        {
            SaveError = Strings.Format("Editor_SaveFailed", LogSanitizer.Redact(ex.Message));
        }
    }
}
