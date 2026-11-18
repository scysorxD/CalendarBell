using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CalendarBell.App.Services;

/// <summary>Read-only facts about the Windows session, used for diagnostics and banners. Never changes settings.</summary>
internal static partial class WindowsEnvironment
{
    private const int AsfwAny = -1;

    /// <summary>App notifications do not work in elevated processes (Microsoft docs).</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Best effort: true when Windows reports quiet hours / presentation / full-screen (notifications may be held).</summary>
    public static bool IsNotificationQuietTime()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state is UserNotificationState.Busy or UserNotificationState.RunningD3DFullScreen or UserNotificationState.PresentationMode or UserNotificationState.QuietTime;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Lets the resident instance bring its window to the front when a second launch asks it to.</summary>
    public static void AllowAnyProcessToSetForeground() => AllowSetForegroundWindow(AsfwAny);

    public static void OpenNotificationSettings() => OpenUri("ms-settings:notifications");

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
    }

    public static void OpenUri(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out UserNotificationState state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    private enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }
}
