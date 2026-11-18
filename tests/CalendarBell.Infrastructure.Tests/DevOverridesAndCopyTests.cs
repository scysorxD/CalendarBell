using System.Net;
using CalendarBell.Core.Models;
using CalendarBell.Core.Services;
using CalendarBell.Infrastructure.Ics;
using CalendarBell.Infrastructure.Install;
using CalendarBell.Infrastructure.Storage;
using CalendarBell.Infrastructure.Sync;
using CalendarBell.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Win32;

namespace CalendarBell.Infrastructure.Tests;

public sealed class DevOverridesAndCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CalendarBellTests", Guid.NewGuid().ToString("N"));
    private readonly string _testKey = @"Software\CalendarBellTests\" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false);
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void No_dev_file_means_no_overrides()
    {
        Directory.CreateDirectory(_root);

        Assert.Null(DevOverrides.Load(_root));
    }

    [Fact]
    public void Dev_file_next_to_the_exe_overrides_data_folder_and_localhost_http()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, DevOverrides.FileName), """{ "dataDirectory": "C:/cb-test-data", "allowHttpLocalhost": true }""");

        var overrides = DevOverrides.Load(_root);

        Assert.NotNull(overrides);
        Assert.Equal("C:/cb-test-data", overrides.DataDirectory);
        Assert.True(overrides.AllowHttpLocalhost);
    }

    [Fact]
    public void Malformed_dev_file_is_ignored()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, DevOverrides.FileName), "{ nope");

        Assert.Null(DevOverrides.Load(_root));
    }

    [Fact]
    public void Installer_never_copies_the_dev_overrides_file()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "CalendarBell.exe"), "exe");
        File.WriteAllText(Path.Combine(source, DevOverrides.FileName), "{}");
        var paths = new InstallerPaths(Path.Combine(_root, "Programs", "CalendarBell"), Path.Combine(_root, "StartMenu", "CalendarBell.lnk"), _testKey + @"\Uninstall\CalendarBell");

        new PerUserInstaller(paths, "1.0.0").Install(source);

        Assert.False(File.Exists(Path.Combine(paths.InstallDirectory, DevOverrides.FileName)));
    }

    [Fact]
    public async Task Test_connection_uses_singular_for_one_event()
    {
        const string url = "https://calendar-a.example/one.ics";
        var http = new FakeHttpHandler();
        http.Respond(url, () => FakeHttpHandler.Ics("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//T//EN\r\nBEGIN:VEVENT\r\nUID:one@calendar-a.example\r\nDTSTART:20261118T133000Z\r\nDTEND:20261118T140000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var options = new SyncOptions();
        await using var sync = new CalendarSyncService(new InMemoryCalendarRepository(), new InMemorySyncStateStore(),
            new CalendarFeedClient(new FakeUrlProtector(), options, time, http), new IcsCalendarParser(), new FakeScheduler(), time, options, ReminderOptions.Default, () => true);

        var result = await sync.TestConnectionAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal("Connected · 1 upcoming event", result.Message);
    }
}
