// Claude Switch: run several Claude Desktop accounts side by side.
// Copyright (c) 2026 Ngoni Maphosa. MIT License (see LICENSE).
//
//   ClaudeSwitch                          the app (dashboard + tray); one per user
//   ClaudeSwitch --tray                   start in the tray only (used at sign-in)
//   ClaudeSwitch --launch <Name>          open/focus a profile (used by its launcher)
//   ClaudeSwitch --launch-dir <dir> [--config <dir>]
//   ClaudeSwitch --link "claude://..."    route a sign-in link (Windows, opt-in)
//   ClaudeSwitch --cleanup                uninstaller hook (Windows)
//   ClaudeSwitch --refresh-shortcuts [--desktop]   recreate profile launchers (installer)
//   ClaudeSwitch --screenshots <dir> [--theme dark] [--size 1440x900] [--demo]   render the UI to PNG (docs)
//   ClaudeSwitch --demo                   made-up profiles and sessions (screenshots, videos)
//
//   For scripts (prints to the terminal; tab-separated with --plain):
//   ClaudeSwitch --list [--plain]         profiles, accounts, open or not
//   ClaudeSwitch --open <Name>            open or bring forward a profile
//   ClaudeSwitch --diagnose [--show-emails]
//   ClaudeSwitch --check-update           compare with the latest release
//   ClaudeSwitch --version

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using ClaudeSwitch.Core;
using ClaudeSwitch.Ui;

namespace ClaudeSwitch;

public class App : Application
{
    public static string[] Args = Array.Empty<string>();

    public override void Initialize()
    {
        Name = "Claude Switch";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        string theme = Array.IndexOf(Args, "--theme") is int i and >= 0 && i + 1 < Args.Length ? Args[i + 1] : Settings.Theme;
        Neu.Use(theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
        {
            life.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            Shell.Start(life, Args);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public static class Program
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    static string Arg(string[] args, string flag)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static bool Has(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { ErrorLog.Write(e.Exception); e.SetObserved(); };

        if (Has(args, "--cleanup") || Has(args, "/cleanup")) { ProfileSetup.UninstallCleanup(); return 0; }
        if (Cli.Handles(args)) return Cli.Run(args);
        if (Has(args, "--refresh-shortcuts"))
        {
            foreach (ProfileEntry p in Profiles.LoadOrEmpty()) Shortcuts.Create(p, Has(args, "--desktop"));
            return 0;
        }

        // Command-line launches show dialogs (errors, first sign-in) on demand.
        Launcher.ShowError = Dialogs.ErrorSync;
        Launcher.ConfirmFirstSignIn = Dialogs.FirstSignInSync;
        LinkRouter.PickProfile = Dialogs.PickSync;

        string link = Arg(args, "--link");
        if (link != null) return LinkRouter.Handle(link);

        string dir = Arg(args, "--launch-dir");
        if (dir != null) return Launcher.Run(dir, Arg(args, "--config"));

        string name = Arg(args, "--launch");
        if (name != null)
        {
            ProfileEntry p = Profiles.Find(Profiles.LoadOrEmpty(), name);
            if (p == null)
            {
                Dialogs.ErrorSync("There's no profile named “" + name + "”.\n\nOpen Claude Switch to see your profiles or add it.");
                return 1;
            }
            return Launcher.Run(p);
        }

        if (!Has(args, "--screenshots") && !Has(args, "--demo") && !SingleInstance.TryBecomePrimary("show")) return 0;
        App.Args = args;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }
}
