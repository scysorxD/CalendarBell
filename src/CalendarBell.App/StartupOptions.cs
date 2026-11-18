namespace CalendarBell.App;

/// <summary>Command line understood by CalendarBell.exe.</summary>
public sealed record StartupOptions
{
    /// <summary>Argument Windows App SDK uses when Windows launches the app from a notification.</summary>
    public const string NotificationActivationArgument = "----AppNotificationActivated:";

    /// <summary>Started by the sign-in task / Run key: never show windows.</summary>
    public bool Background { get; init; }

    public bool OpenSettings { get; init; }

    public bool OpenUpcoming { get; init; }

    public bool Install { get; init; }

    public bool Uninstall { get; init; }

    public bool Quiet { get; init; }

    /// <summary>Uninstall cleanup only (an external installer such as Inno Setup removes the program files itself).</summary>
    public bool KeepFiles { get; init; }

    /// <summary>Skip the "Install CalendarBell" prompt when started outside the install folder (development, portable use).</summary>
    public bool NoInstallPrompt { get; init; }

    public bool IsNotificationActivation { get; init; }

    public static StartupOptions Parse(IEnumerable<string> args)
    {
        var options = new StartupOptions();
        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.StartsWith(NotificationActivationArgument, StringComparison.OrdinalIgnoreCase))
            {
                options = options with { IsNotificationActivation = true, Background = true };
                continue;
            }

            options = arg.ToLowerInvariant() switch
            {
                "--background" => options with { Background = true },
                "--settings" => options with { OpenSettings = true },
                "--upcoming" => options with { OpenUpcoming = true },
                "--install" => options with { Install = true },
                "--uninstall" => options with { Uninstall = true },
                "--quiet" => options with { Quiet = true },
                "--keep-files" => options with { KeepFiles = true },
                "--no-install-prompt" or "--portable" => options with { NoInstallPrompt = true },
                _ => options,
            };
        }

        return options;
    }
}
