using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using CalendarBell.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace CalendarBell.Infrastructure.Windows;

public enum StartupMethod
{
    TaskScheduler,
    RunKey,
}

/// <summary>
/// "Start with Windows" (spec §8.2): a per-user Task Scheduler logon task (interactive token, least privilege,
/// no time limit) that runs the installed EXE directly. If the task cannot be registered, the HKCU Run key is
/// used instead. Only one mechanism is ever active; Enable/Disable are idempotent.
/// </summary>
public sealed class StartupRegistration
{
    public const string RunValueName = "CalendarBell";
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly string _exePath;
    private readonly string _runKeyPath;
    private readonly bool _useTaskScheduler;
    private readonly ILogger _logger;

    public StartupRegistration(string exePath, string runKeyPath = DefaultRunKeyPath, bool useTaskScheduler = true, ILogger<StartupRegistration>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        _exePath = Path.GetFullPath(exePath);
        _runKeyPath = runKeyPath;
        _useTaskScheduler = useTaskScheduler;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public static string TaskName => "CalendarBell-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    public StartupMethod? CurrentMethod()
    {
        if (_useTaskScheduler && TaskExists())
        {
            return StartupMethod.TaskScheduler;
        }

        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        return key?.GetValue(RunValueName) is string ? StartupMethod.RunKey : null;
    }

    public StartupMethod Enable()
    {
        if (_useTaskScheduler)
        {
            try
            {
                RegisterTask();
                RemoveRunKey();
                _logger.LogInformation("Start with Windows: logon task registered");
                return StartupMethod.TaskScheduler;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                _logger.LogWarning("Task Scheduler registration failed ({Error}); using the HKCU Run key instead", LogSanitizer.Describe(ex));
            }
        }

        using (var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true))
        {
            key.SetValue(RunValueName, $"\"{_exePath}\" --background", RegistryValueKind.String);
        }

        if (_useTaskScheduler)
        {
            TryDeleteTask();
        }

        _logger.LogInformation("Start with Windows: Run key registered");
        return StartupMethod.RunKey;
    }

    public void Disable()
    {
        if (_useTaskScheduler)
        {
            TryDeleteTask();
        }

        RemoveRunKey();
        _logger.LogInformation("Start with Windows disabled");
    }

    public static string BuildTaskXml(string exePath, string userId)
    {
        var task = new XElement(TaskNs + "Task", new XAttribute("version", "1.3"),
            new XElement(TaskNs + "RegistrationInfo",
                new XElement(TaskNs + "Author", "CalendarBell"),
                new XElement(TaskNs + "Description", "Starts CalendarBell meeting reminders when you sign in.")),
            new XElement(TaskNs + "Triggers",
                new XElement(TaskNs + "LogonTrigger",
                    new XElement(TaskNs + "Enabled", "true"),
                    new XElement(TaskNs + "UserId", userId),
                    new XElement(TaskNs + "Delay", "PT5S"))),
            new XElement(TaskNs + "Principals",
                new XElement(TaskNs + "Principal", new XAttribute("id", "Author"),
                    new XElement(TaskNs + "UserId", userId),
                    new XElement(TaskNs + "LogonType", "InteractiveToken"),
                    new XElement(TaskNs + "RunLevel", "LeastPrivilege"))),
            new XElement(TaskNs + "Settings",
                new XElement(TaskNs + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(TaskNs + "DisallowStartIfOnBatteries", "false"),
                new XElement(TaskNs + "StopIfGoingOnBatteries", "false"),
                new XElement(TaskNs + "AllowHardTerminate", "true"),
                new XElement(TaskNs + "StartWhenAvailable", "false"),
                new XElement(TaskNs + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(TaskNs + "IdleSettings",
                    new XElement(TaskNs + "StopOnIdleEnd", "false"),
                    new XElement(TaskNs + "RestartOnIdle", "false")),
                new XElement(TaskNs + "AllowStartOnDemand", "true"),
                new XElement(TaskNs + "Enabled", "true"),
                new XElement(TaskNs + "Hidden", "false"),
                new XElement(TaskNs + "RunOnlyIfIdle", "false"),
                new XElement(TaskNs + "WakeToRun", "false"),
                new XElement(TaskNs + "ExecutionTimeLimit", "PT0S"),
                new XElement(TaskNs + "Priority", "5")),
            new XElement(TaskNs + "Actions", new XAttribute("Context", "Author"),
                new XElement(TaskNs + "Exec",
                    new XElement(TaskNs + "Command", $"\"{exePath}\""),
                    new XElement(TaskNs + "Arguments", "--background"),
                    new XElement(TaskNs + "WorkingDirectory", Path.GetDirectoryName(exePath)))));
        return new XDocument(task).ToString();
    }

    private static dynamic ConnectService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available.");
        dynamic service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Task Scheduler is not available.");
        service.Connect();
        return service;
    }

    private void RegisterTask()
    {
        var identity = WindowsIdentity.GetCurrent();
        var xml = BuildTaskXml(_exePath, identity.Name);
        var service = ConnectService();
        try
        {
            var folder = service.GetFolder(@"\");
            folder.RegisterTask(TaskName, xml, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    private bool TaskExists()
    {
        try
        {
            var service = ConnectService();
            try
            {
                var task = service.GetFolder(@"\").GetTask(TaskName);
                string path = task.Definition.Actions[1].Path;
                return string.Equals(path.Trim('"'), _exePath, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Marshal.FinalReleaseComObject(service);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or FileNotFoundException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }

    private void TryDeleteTask()
    {
        try
        {
            var service = ConnectService();
            try
            {
                service.GetFolder(@"\").DeleteTask(TaskName, 0);
            }
            finally
            {
                Marshal.FinalReleaseComObject(service);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or FileNotFoundException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // not registered
        }
    }

    private void RemoveRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
