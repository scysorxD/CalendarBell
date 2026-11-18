using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CalendarBell.Windows.SmokeTests;

/// <summary>
/// Simulates a click on a toast button exactly the way the Windows shell delivers it: by calling the app's
/// registered COM activator (INotificationActivationCallback). If the app is not running, COM starts it with
/// "----AppNotificationActivated:" just like a real click in Notification Center.
/// </summary>
internal static class ToastActivator
{
    private const uint ClsctxLocalServer = 0x4;
    private static readonly Guid CallbackIid = new("53E31837-6600-4A81-9395-75CFFE746F94");

    public static (string Aumid, Guid Clsid)? FindRegistration(string exePath)
    {
        using var aumids = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
        if (aumids is null)
        {
            return null;
        }

        foreach (var name in aumids.GetSubKeyNames())
        {
            using var key = aumids.OpenSubKey(name);
            if (key?.GetValue("CustomActivator") is not string activator || !Guid.TryParse(activator, out var clsid))
            {
                continue;
            }

            using var server = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{clsid}}}\LocalServer32");
            if (server?.GetValue(null) is string command && command.Contains(exePath, StringComparison.OrdinalIgnoreCase))
            {
                return (name, clsid);
            }
        }

        return null;
    }

    public static void Activate((string Aumid, Guid Clsid) registration, string arguments, IReadOnlyDictionary<string, string> inputs)
    {
        var iid = CallbackIid;
        var clsid = registration.Clsid;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxLocalServer, ref iid, out var instance));
        try
        {
            var callback = (INotificationActivationCallback)instance;
            var data = inputs.Select(kv => new NotificationUserInputData { Key = kv.Key, Value = kv.Value }).ToArray();
            callback.Activate(registration.Aumid, arguments, data, (uint)data.Length);
        }
        finally
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    /// <summary>Removes the COM/AUMID registration a smoke-test copy created (it lives in a temp folder).</summary>
    public static void RemoveRegistrationsUnder(string directory)
    {
        using var aumids = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId", writable: true);
        if (aumids is null)
        {
            return;
        }

        foreach (var name in aumids.GetSubKeyNames())
        {
            Guid clsid;
            using (var key = aumids.OpenSubKey(name))
            {
                if (key?.GetValue("CustomActivator") is not string activator || !Guid.TryParse(activator, out clsid))
                {
                    continue;
                }
            }

            string? command;
            using (var server = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{clsid}}}\LocalServer32"))
            {
                command = server?.GetValue(null) as string;
            }

            if (command is not null && command.Contains(directory, StringComparison.OrdinalIgnoreCase))
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{clsid}}}", throwOnMissingSubKey: false);
                aumids.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationUserInputData
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Key;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Value;
    }

    [ComImport]
    [Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INotificationActivationCallback
    {
        void Activate(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] NotificationUserInputData[] data,
            uint count);
    }
}
