using System.Diagnostics;
using CalendarBell.App.Notifications;
using CalendarBell.Infrastructure.Install;
using CalendarBell.Infrastructure.Storage;
using CalendarBell.Infrastructure.Sync;
using CalendarBell.Infrastructure.Windows;

namespace CalendarBell.App.Services;

/// <summary>Install / update / uninstall steps shared by the setup dialog and the quiet command line.</summary>
internal static class SetupActions
{
    public static PerUserInstaller CreateInstaller() => new(InstallerPaths.Default(), SyncOptions.AppVersion);

    /// <summary>Asks a running CalendarBell (for this user and data folder) to exit and waits until it has.</summary>
    public static async Task StopRunningInstanceAsync(DataPaths paths)
    {
        var alreadyFree = await Task.Run(() =>
        {
            using var probe = SingleInstance.Create(paths.Root);
            if (probe.IsPrimary)
            {
                return true;
            }

            probe.SendAsync(new InstanceMessage("exit", null, null), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return false;
        }).ConfigureAwait(false);

        if (!alreadyFree)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var free = await Task.Run(() =>
                {
                    using var probe = SingleInstance.Create(paths.Root);
                    return probe.IsPrimary;
                }).ConfigureAwait(false);
                if (free)
                {
                    break;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        // The process may still be releasing file handles after the mutex is free.
        foreach (var process in Process.GetProcessesByName("CalendarBell").Where(p => p.Id != Environment.ProcessId))
        {
            using (process)
            {
                try
                {
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    public static async Task InstallAsync(DataPaths paths)
    {
        var installer = CreateInstaller();
        await StopRunningInstanceAsync(paths).ConfigureAwait(false);
        await Task.Run(() => installer.Install(AppContext.BaseDirectory)).ConfigureAwait(false);
    }

    public static void LaunchInstalled(params string[] args)
    {
        var info = new ProcessStartInfo(CreateInstaller().Paths.InstalledExe) { UseShellExecute = false, WorkingDirectory = CreateInstaller().Paths.InstallDirectory };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        Process.Start(info)?.Dispose();
    }

    public static void RelaunchSelf(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        Process.Start(info)?.Dispose();
    }

    public static async Task UninstallAsync(DataPaths paths, bool deleteUserData, bool removeInstallDirectory = true)
    {
        var installer = CreateInstaller();
        await StopRunningInstanceAsync(paths).ConfigureAwait(false);
        await Task.Run(() =>
        {
            new StartupRegistration(installer.Paths.InstalledExe).Disable();
            WindowsToastService.UnregisterAll();
            installer.Uninstall(paths, deleteUserData, removeInstallDirectory);
        }).ConfigureAwait(false);
    }
}
