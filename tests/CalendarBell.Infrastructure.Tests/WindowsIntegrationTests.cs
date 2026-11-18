using System.Xml.Linq;
using CalendarBell.Infrastructure.Install;
using CalendarBell.Infrastructure.Logging;
using CalendarBell.Infrastructure.Storage;
using CalendarBell.Infrastructure.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Win32;

namespace CalendarBell.Infrastructure.Tests;

public sealed class WindowsIntegrationTests : IDisposable
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
    public void File_logger_writes_redacted_lines()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        using (var provider = new FileLoggerProvider(_root, time))
        {
            var logger = provider.CreateLogger("CalendarBell.Infrastructure.Sync.CalendarSyncService");
            logger.LogWarning("Fetch failed for https://calendar-a.example/x.ics?token=SECRET");
            logger.LogError(new HttpRequestException("boom https://calendar-a.example/y?token=SECRET2"), "Unhandled");
        }

        var text = string.Join("\n", Directory.GetFiles(_root, "*.log").Select(File.ReadAllText));
        Assert.Contains("CalendarSyncService", text, StringComparison.Ordinal);
        Assert.Contains("[redacted-url]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar-a.example", text, StringComparison.Ordinal);
    }

    [Fact]
    public void File_logger_rotates_and_keeps_at_most_five_files()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        using (var provider = new FileLoggerProvider(_root, time, maxFileBytes: 2048, maxFiles: 5))
        {
            var logger = provider.CreateLogger("Test");
            for (var i = 0; i < 400; i++)
            {
                logger.LogInformation("Line {Index} {Padding}", i, new string('x', 80));
            }
        }

        var files = Directory.GetFiles(_root, "*.log");
        Assert.InRange(files.Length, 2, 5);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 2048 + 512));
    }

    [Fact]
    public void File_logger_deletes_logs_older_than_retention()
    {
        Directory.CreateDirectory(_root);
        var old = Path.Combine(_root, "calendarbell.3.log");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

        using (new FileLoggerProvider(_root, time))
        {
        }

        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task T32_second_instance_is_not_primary_and_forwards_its_message()
    {
        var scope = "test-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.Create(scope);
        var received = new TaskCompletionSource<InstanceMessage>();
        first.MessageReceived += (_, m) => received.TrySetResult(m);
        first.StartListening();

        using var second = await Task.Run(() => SingleInstance.Create(scope), TestContext.Current.CancellationToken); // a mutex is re-entrant on its owning thread; real instances are separate processes
        var sent = await second.SendAsync(new InstanceMessage("open-settings", null, null), TimeSpan.FromSeconds(5));

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        Assert.True(sent);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("open-settings", message.Command);
    }

    [Fact]
    public async Task Notification_activation_payload_is_forwarded_intact()
    {
        var scope = "test-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.Create(scope);
        var received = new TaskCompletionSource<InstanceMessage>();
        first.MessageReceived += (_, m) => received.TrySetResult(m);
        first.StartListening();
        using var second = await Task.Run(() => SingleInstance.Create(scope), TestContext.Current.CancellationToken); // a mutex is re-entrant on its owning thread; real instances are separate processes

        await second.SendAsync(new InstanceMessage("notification-action", "action=snooze&b=0123456789ab", new Dictionary<string, string> { ["snoozeTarget"] = "5" }), TimeSpan.FromSeconds(5));

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("action=snooze&b=0123456789ab", message.Arguments);
        Assert.Equal("5", message.Inputs!["snoozeTarget"]);
    }

    [Fact]
    public void Primary_releases_on_dispose_so_a_new_instance_can_become_primary()
    {
        var scope = "test-" + Guid.NewGuid().ToString("N");
        var first = SingleInstance.Create(scope);
        Assert.True(first.IsPrimary);
        first.Dispose();

        using var next = SingleInstance.Create(scope);

        Assert.True(next.IsPrimary);
    }

    [Fact]
    public void Startup_task_xml_is_per_user_non_elevated_and_without_time_limit()
    {
        var xml = XDocument.Parse(StartupRegistration.BuildTaskXml(@"C:\Users\someone\AppData\Local\Programs\CalendarBell\CalendarBell.exe", @"PC\someone"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal(@"PC\someone", xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("\"C:\\Users\\someone\\AppData\\Local\\Programs\\CalendarBell\\CalendarBell.exe\"", xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--background", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.DoesNotContain("powershell", xml.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", xml.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("false", xml.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
    }

    [Fact]
    public void Run_key_fallback_is_idempotent_and_removable()
    {
        var startup = new StartupRegistration(@"C:\Apps\CalendarBell\CalendarBell.exe", runKeyPath: _testKey + @"\Run", useTaskScheduler: false);

        Assert.Equal(StartupMethod.RunKey, startup.Enable());
        Assert.Equal(StartupMethod.RunKey, startup.Enable());
        Assert.Equal(StartupMethod.RunKey, startup.CurrentMethod());
        using (var key = Registry.CurrentUser.OpenSubKey(_testKey + @"\Run"))
        {
            Assert.Equal("\"C:\\Apps\\CalendarBell\\CalendarBell.exe\" --background", key!.GetValue(StartupRegistration.RunValueName));
            Assert.Single(key.GetValueNames());
        }

        startup.Disable();

        Assert.Null(startup.CurrentMethod());
    }

    [Fact]
    public void Installer_copies_files_registers_uninstall_entry_and_shortcut()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "Resources"));
        File.WriteAllText(Path.Combine(source, "CalendarBell.exe"), "exe");
        File.WriteAllText(Path.Combine(source, "Resources", "icon.png"), "png");
        var paths = new InstallerPaths(Path.Combine(_root, "Programs", "CalendarBell"), Path.Combine(_root, "StartMenu", "CalendarBell.lnk"), _testKey + @"\Uninstall\CalendarBell");
        var installer = new PerUserInstaller(paths, "1.0.0");

        installer.Install(source);

        Assert.True(File.Exists(Path.Combine(paths.InstallDirectory, "CalendarBell.exe")));
        Assert.True(File.Exists(Path.Combine(paths.InstallDirectory, "Resources", "icon.png")));
        Assert.True(File.Exists(paths.StartMenuShortcut));
        using var key = Registry.CurrentUser.OpenSubKey(paths.UninstallKeyPath)!;
        Assert.Equal("CalendarBell", key.GetValue("DisplayName"));
        Assert.Equal("1.0.0", key.GetValue("DisplayVersion"));
        Assert.Contains("--uninstall", (string)key.GetValue("UninstallString")!, StringComparison.Ordinal);
        Assert.Equal(1, key.GetValue("NoModify"));
        Assert.True(installer.IsInstalled);
    }

    [Fact]
    public void Reinstall_overwrites_binaries_but_never_touches_user_data()
    {
        var data = new DataPaths(Path.Combine(_root, "data"));
        data.EnsureCreated();
        File.WriteAllText(data.SettingsPath, "{\"PlaySound\":false}");
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "CalendarBell.exe"), "v1");
        var paths = new InstallerPaths(Path.Combine(_root, "Programs", "CalendarBell"), Path.Combine(_root, "StartMenu", "CalendarBell.lnk"), _testKey + @"\Uninstall\CalendarBell");
        var installer = new PerUserInstaller(paths, "1.0.0");
        installer.Install(source);

        File.WriteAllText(Path.Combine(source, "CalendarBell.exe"), "v2");
        installer.Install(source);

        Assert.Equal("v2", File.ReadAllText(Path.Combine(paths.InstallDirectory, "CalendarBell.exe")));
        Assert.Equal("{\"PlaySound\":false}", File.ReadAllText(data.SettingsPath));
    }

    [Fact]
    public void Uninstall_removes_registration_and_optionally_data()
    {
        var data = new DataPaths(Path.Combine(_root, "data"));
        data.EnsureCreated();
        File.WriteAllText(data.SettingsPath, "{}");
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "CalendarBell.exe"), "exe");
        var paths = new InstallerPaths(Path.Combine(_root, "Programs", "CalendarBell"), Path.Combine(_root, "StartMenu", "CalendarBell.lnk"), _testKey + @"\Uninstall\CalendarBell");
        var installer = new PerUserInstaller(paths, "1.0.0");
        installer.Install(source);

        installer.Uninstall(data, deleteUserData: false, removeInstallDirectory: true);

        Assert.False(installer.IsInstalled);
        Assert.False(File.Exists(paths.StartMenuShortcut));
        Assert.Null(Registry.CurrentUser.OpenSubKey(paths.UninstallKeyPath));
        Assert.True(File.Exists(data.SettingsPath));

        installer.Uninstall(data, deleteUserData: true, removeInstallDirectory: true);
        Assert.False(Directory.Exists(data.Root));
    }
}
