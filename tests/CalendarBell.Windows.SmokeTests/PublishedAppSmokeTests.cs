using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CalendarBell.Core.Models;
using CalendarBell.Infrastructure.Security;
using CalendarBell.Infrastructure.Storage;
using CalendarBell.Infrastructure.Windows;
using Microsoft.Data.Sqlite;

namespace CalendarBell.Windows.SmokeTests;

/// <summary>
/// End-to-end checks of the published app on a real Windows 11 session (spec §10.2/§10.3). Opt-in:
/// set CALENDARBELL_SMOKE=1 and publish first (scripts/publish.ps1). Uses a temporary copy of the app, an isolated
/// data folder and a local mock feed, so the user's real CalendarBell data is never touched.
/// </summary>
public sealed class PublishedAppSmokeTests : IAsyncLifetime
{
    private const string Secret = "SMOKE-SECRET-7731";
    private static readonly string RepoRoot = FindRepoRoot();
    private readonly string _work = Path.Combine(Path.GetTempPath(), "CalendarBellSmoke", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private Process? _mock;
    private string _appDir = string.Empty;
    private string _dataDir = string.Empty;
    private int _port;

    private static bool Enabled => Environment.GetEnvironmentVariable("CALENDARBELL_SMOKE") == "1";

    private static string PublishedApp => Environment.GetEnvironmentVariable("CALENDARBELL_SMOKE_APPDIR") ?? Path.Combine(RepoRoot, "dist", "CalendarBell-win-x64");

    private string Exe => Path.Combine(_appDir, "CalendarBell.exe");

    private string MockUrl(string name) => $"http://localhost:{_port}/{name}.ics?token={Secret}";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var p in Process.GetProcessesByName("CalendarBell").Where(p => SafePath(p)?.StartsWith(_work, StringComparison.OrdinalIgnoreCase) == true))
        {
            p.Kill();
            await p.WaitForExitAsync();
        }

        if (_mock is { HasExited: false })
        {
            _mock.Kill();
            await _mock.WaitForExitAsync();
        }

        _http.Dispose();
        SqliteConnection.ClearAllPools();
        if (Environment.GetEnvironmentVariable("CALENDARBELL_SMOKE_KEEP") == "1")
        {
            return; // keep the copy, data and logs for diagnosis
        }

        if (Directory.Exists(_work))
        {
            ToastActivator.RemoveRegistrationsUnder(_work);
            try
            {
                Directory.Delete(_work, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void Published_exe_is_a_gui_subsystem_binary_so_no_console_can_appear()
    {
        var exe = Path.Combine(PublishedApp, "CalendarBell.exe");
        Assert.SkipUnless(File.Exists(exe), "Publish first (scripts/publish.ps1).");

        var bytes = File.ReadAllBytes(exe);
        var pe = BitConverter.ToInt32(bytes, 0x3C);
        var subsystem = BitConverter.ToUInt16(bytes, pe + 4 + 20 + 68);

        Assert.Equal(2, subsystem); // IMAGE_SUBSYSTEM_WINDOWS_GUI
        Assert.False(File.Exists(Path.Combine(PublishedApp, DevOverrides.FileName)));
    }

    [Fact]
    public async Task End_to_end_reminders_group_snooze_dismiss_single_instance_and_activation_when_closed()
    {
        Assert.SkipUnless(Enabled, "Set CALENDARBELL_SMOKE=1 to run Windows smoke tests.");
        Assert.SkipUnless(File.Exists(Path.Combine(PublishedApp, "CalendarBell.exe")), "Publish first (scripts/publish.ps1).");
        var ct = TestContext.Current.CancellationToken;
        await PrepareAsync(ct);

        // Two calendars with a meeting at the same minute (group) and a second meeting for Calendar A 30 s later.
        var a1 = await AddEventAsync("a", "smoke-a1@calendar-a.example", 15 * 60 + 25, ct);
        await AddEventAsync("b", "smoke-b1@calendar-b.example", 15 * 60 + 25, ct, busy: "TENTATIVE");
        var a2 = await AddEventAsync("a", "smoke-a2@calendar-a.example", 15 * 60 + 55, ct);
        await AddEventAsync("b", "smoke-b4@calendar-b.example", 15 * 60 + 80, ct); // its toast is left unanswered
        await SeedCalendarsAsync(ct);

        // 1. Start: no console, tray process resident, calendars synced, group toast at T-15 for A+B.
        var app = StartApp();
        var group = await WaitForBatchAsync(b => b.Keys.Count == 2, TimeSpan.FromSeconds(75), ct);
        var registration = ToastActivator.FindRegistration(Exe);
        Assert.NotNull(registration);
        Assert.All(group.Keys, k => Assert.True(Record(k).InitialSent));

        // 2. Snooze the group to "5 min before": each member gets its own absolute target (start − 5 min).
        ToastActivator.Activate(registration.Value, $"action=snooze&b={group.Id}", new Dictionary<string, string> { ["snoozeTarget"] = "5" });
        await WaitUntilAsync(() => group.Keys.All(k => Record(k).SnoozedUntil == a1.Start.AddMinutes(-5)), TimeSpan.FromSeconds(15), ct);

        // 3. The later single reminder arrives as its own toast; Dismiss is permanent.
        var single = await WaitForBatchAsync(b => b.Keys.Count == 1, TimeSpan.FromSeconds(60), ct);
        ToastActivator.Activate(registration.Value, $"action=dismiss&b={single.Id}", new Dictionary<string, string>());
        await WaitUntilAsync(() => Record(single.Keys[0]).Dismissed, TimeSpan.FromSeconds(15), ct);
        Assert.Equal(a2.Start, Record(single.Keys[0]).Start);

        // 3b. A third toast (Calendar B) arrives and is deliberately left on screen.
        await WaitUntilAsync(() => Batches().Count >= 3, TimeSpan.FromSeconds(60), ct);
        var pending = Batches()[2];
        Assert.False(Record(pending.Keys[0]).Dismissed);

        // 4. A second launch forwards to the resident instance and exits; still one process.
        var second = Process.Start(new ProcessStartInfo(Exe, "--upcoming") { UseShellExecute = false })!;
        Assert.True(second.WaitForExit(15_000));
        Assert.Equal(0, second.ExitCode);
        Assert.False(app.HasExited);
        Assert.Single(AppProcesses());

        // 5. Exit through the single-instance channel (same path as tray → Exit / installer update).
        await RequestExitAsync();
        Assert.True(app.WaitForExit(20_000));

        // 6. App closed + click on the (still visible) group toast: Windows relaunches CalendarBell, which processes
        //    the action without opening a window and stays resident.
        ToastActivator.Activate(registration.Value, $"action=dismiss&b={group.Id}", new Dictionary<string, string>());
        await WaitUntilAsync(() => group.Keys.All(k => Record(k).Dismissed), TimeSpan.FromSeconds(30), ct);
        var relaunched = Assert.Single(AppProcesses());

        // 6b. The relaunch must keep the unanswered toast (it still waits for the user) instead of wiping it.
        var relaunchLog = ReadShared(Path.Combine(_dataDir, "logs", "calendarbell.log")); // the app still has it open
        Assert.Contains("Start-up toast reconciliation: kept 1 pending toast(s)", relaunchLog, StringComparison.Ordinal);
        Assert.False(Record(pending.Keys[0]).Dismissed);

        // 7. Restart: dismissed reminders never come back.
        await RequestExitAsync();
        Assert.True(relaunched.WaitForExit(20_000));
        var batchesBefore = Batches().Count;
        var restarted = StartApp();
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        Assert.Equal(batchesBefore, Batches().Count);
        await RequestExitAsync();
        Assert.True(restarted.WaitForExit(20_000));

        // 8. Nothing on disk contains the calendar URL or its token.
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataDir, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, ct));
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain($"localhost:{_port}", text, StringComparison.Ordinal);
        }

        var log = await File.ReadAllTextAsync(Path.Combine(_dataDir, "logs", "calendarbell.log"), ct);
        Assert.Contains("Toast shown: kind=SameStartGroup members=2", log, StringComparison.Ordinal);
        Assert.Contains("Snooze (5 min before start) applied to 2 occurrence(s)", log, StringComparison.Ordinal);
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        _appDir = Path.Combine(_work, "app");
        _dataDir = Path.Combine(_work, "data");
        CopyDirectory(PublishedApp, _appDir);
        File.WriteAllText(Path.Combine(_appDir, DevOverrides.FileName), JsonSerializer.Serialize(new { dataDirectory = _dataDir, allowHttpLocalhost = true }));
        _port = FreePort();
        var mockDll = Path.Combine(RepoRoot, "tools", "CalendarBell.MockFeedServer", "bin", "Release", "net10.0", "CalendarBell.MockFeedServer.dll");
        Assert.True(File.Exists(mockDll), "Build the solution in Release first (mock feed server).");
        _mock = Process.Start(new ProcessStartInfo("dotnet") { ArgumentList = { mockDll, "--urls", $"http://localhost:{_port}" }, UseShellExecute = false, CreateNoWindow = true })!;
        await WaitUntilAsync(() => Ping($"http://localhost:{_port}/a.ics"), TimeSpan.FromSeconds(20), ct);
    }

    private async Task SeedCalendarsAsync(CancellationToken ct)
    {
        using var db = CalendarBellDatabase.Open(new DataPaths(_dataDir));
        var repo = new SqliteCalendarRepository(db, new DataPaths(_dataDir));
        var protector = new DpapiUrlProtector();
        await repo.SaveAsync(new CalendarSource(Guid.NewGuid(), "Calendar A", protector.Protect(MockUrl("a")), true), ct);
        await repo.SaveAsync(new CalendarSource(Guid.NewGuid(), "Calendar B", protector.Protect(MockUrl("b")), true), ct);
        SqliteConnection.ClearAllPools();
    }

    private async Task<(string Uid, DateTimeOffset Start)> AddEventAsync(string calendar, string uid, int inSeconds, CancellationToken ct, string busy = "BUSY")
    {
        var response = await _http.PostAsync(new Uri($"http://localhost:{_port}/{calendar}/events?inSeconds={inSeconds}&busy={busy}&uid={Uri.EscapeDataString(uid)}"), null, ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (uid, created.GetProperty("startUtc").GetDateTimeOffset().ToUniversalTime());
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Process StartApp() => Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false })!;

    private List<Process> AppProcesses() =>
        Process.GetProcessesByName("CalendarBell").Where(p => string.Equals(SafePath(p), Exe, StringComparison.OrdinalIgnoreCase)).ToList();

    private async Task RequestExitAsync()
    {
        var sent = await Task.Run(() =>
        {
            using var probe = SingleInstance.Create(new DataPaths(_dataDir).Root);
            return !probe.IsPrimary && probe.SendAsync(new InstanceMessage("exit", null, null), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        });
        Assert.True(sent, "exit request was not delivered");
    }

    private async Task<(string Id, List<string> Keys)> WaitForBatchAsync(Func<(string Id, List<string> Keys), bool> match, TimeSpan timeout, CancellationToken ct)
    {
        (string Id, List<string> Keys)? found = null;
        await WaitUntilAsync(() => (found = Batches().FirstOrDefault(b => match(b))) is { Id: not null }, timeout, ct);
        return found!.Value;
    }

    private List<(string Id, List<string> Keys)> Batches()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT batch_id, occurrence_keys FROM ToastBatches WHERE grp = 'reminders' ORDER BY created_utc";
        using var reader = command.ExecuteReader();
        var list = new List<(string, List<string>)>();
        while (reader.Read())
        {
            list.Add((reader.GetString(0), JsonSerializer.Deserialize<List<string>>(reader.GetString(1))!));
        }

        return list;
    }

    private (bool InitialSent, bool Dismissed, DateTimeOffset? SnoozedUntil, DateTimeOffset Start) Record(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT initial_sent, dismissed, snoozed_until_utc, event_start_utc FROM Reminders WHERE occurrence_key = $k";
        command.Parameters.AddWithValue("$k", key);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "reminder record not found");
        return (reader.GetInt64(0) != 0, reader.GetInt64(1) != 0,
            reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
            new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dataDir, "calendarbell.db"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or Xunit.Sdk.XunitException)
            {
                // database not created yet / record not there yet
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met within {timeout}.");
            }

            await Task.Delay(250, ct);
        }
    }

    private bool Ping(string url)
    {
        try
        {
            using var response = _http.GetAsync(new Uri(url)).GetAwaiter().GetResult();
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? SafePath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CalendarBell.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
