using System.Diagnostics;
using System.Runtime.InteropServices;
using CalendarBell.Infrastructure.Storage;
using Microsoft.Win32;

namespace CalendarBell.Infrastructure.Install;

public sealed record InstallerPaths(string InstallDirectory, string StartMenuShortcut, string UninstallKeyPath)
{
    public const string ExeName = "CalendarBell.exe";

    public string InstalledExe => Path.Combine(InstallDirectory, ExeName);

    public static InstallerPaths Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CalendarBell"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CalendarBell.lnk"),
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CalendarBell");
}

/// <summary>
/// Per-user installation without administrator rights (spec §8.2): binaries in %LOCALAPPDATA%\Programs\CalendarBell,
/// Start menu shortcut, and an "Apps &amp; features" entry under HKCU. User data is never touched by install;
/// uninstall removes it only when explicitly asked.
/// </summary>
public sealed class PerUserInstaller
{
    private readonly InstallerPaths _paths;
    private readonly string _version;

    public PerUserInstaller(InstallerPaths paths, string version)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _version = version;
    }

    public InstallerPaths Paths => _paths;

    public bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_paths.UninstallKeyPath);
            return key is not null && File.Exists(_paths.InstalledExe);
        }
    }

    public bool IsRunningFromInstallDirectory(string baseDirectory) =>
        string.Equals(Path.GetFullPath(baseDirectory).TrimEnd('\\'), Path.GetFullPath(_paths.InstallDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies the application folder (overwriting older binaries) and registers it for the current user.</summary>
    public void Install(string sourceDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        Directory.CreateDirectory(_paths.InstallDirectory);
        long totalBytes = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (string.Equals(relative, DevOverrides.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue; // development overrides must never reach an installed copy
            }

            var target = Path.Combine(_paths.InstallDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            totalBytes += new FileInfo(file).Length;
        }

        CreateShortcut(_paths.StartMenuShortcut, _paths.InstalledExe);
        using var key = Registry.CurrentUser.CreateSubKey(_paths.UninstallKeyPath, writable: true);
        key.SetValue("DisplayName", "CalendarBell");
        key.SetValue("DisplayVersion", _version);
        key.SetValue("Publisher", "CalendarBell");
        key.SetValue("InstallLocation", _paths.InstallDirectory);
        key.SetValue("DisplayIcon", _paths.InstalledExe + ",0");
        key.SetValue("UninstallString", $"\"{_paths.InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{_paths.InstalledExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, totalBytes / 1024), RegistryValueKind.DWord);
    }

    /// <summary>
    /// Removes the shortcut and Apps &amp; features entry, optionally the user's data (settings, encrypted URLs, logs),
    /// and the program folder. When the running EXE lives in that folder, deletion is finished by a short-lived hidden
    /// process after this one exits.
    /// </summary>
    public void Uninstall(DataPaths data, bool deleteUserData, bool removeInstallDirectory)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (File.Exists(_paths.StartMenuShortcut))
        {
            File.Delete(_paths.StartMenuShortcut);
        }

        Registry.CurrentUser.DeleteSubKeyTree(_paths.UninstallKeyPath, throwOnMissingSubKey: false);
        if (deleteUserData && Directory.Exists(data.Root))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(data.Root, recursive: true);
        }

        if (!removeInstallDirectory || !Directory.Exists(_paths.InstallDirectory))
        {
            return;
        }

        if (IsRunningFromInstallDirectory(AppContext.BaseDirectory))
        {
            ScheduleDirectoryDeletion(_paths.InstallDirectory, Environment.ProcessId);
        }
        else
        {
            Directory.Delete(_paths.InstallDirectory, recursive: true);
        }
    }

    private static void ScheduleDirectoryDeletion(string directory, int waitForProcessId)
    {
        // A hidden, short-lived helper removes the folder once this process has exited (no window is shown).
        var script = $"$p = Get-Process -Id {waitForProcessId} -ErrorAction SilentlyContinue; if ($p) {{ $p.WaitForExit(15000) }}; " +
                     $"Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath '{directory.Replace("'", "''", StringComparison.Ordinal)}' -Recurse -Force -ErrorAction SilentlyContinue";
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-WindowStyle");
        info.ArgumentList.Add("Hidden");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        Process.Start(info)?.Dispose();
    }

    private static void CreateShortcut(string shortcutPath, string targetExe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            var shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = targetExe;
                shortcut.Arguments = "--settings";
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetExe);
                shortcut.IconLocation = targetExe + ",0";
                shortcut.Description = "CalendarBell – meeting reminders";
                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
