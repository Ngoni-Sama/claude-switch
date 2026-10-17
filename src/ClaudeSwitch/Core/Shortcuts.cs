// "Claude (Work)"-style launchers for each profile, the native way per OS:
//   Windows  Start menu\Claude Switch\Claude (Work).lnk  (+ Desktop if wanted)
//   macOS    ~/Applications/Claude Switch/Claude (Work).app   (tiny bundle that runs us)
//   Linux    ~/.local/share/applications/claude-switch-work.desktop
// Each just runs:  ClaudeSwitch --launch "Work"

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ClaudeSwitch.Core;

public static class Shortcuts
{
    public static string Label(string profile) => "Claude (" + profile + ")";

    static string WinStartMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Switch");
    static string WinDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    static string MacFolder => Path.Combine(Platform.Home, "Applications", "Claude Switch");
    static string LinuxFolder => Path.Combine(Platform.Home, ".local", "share", "applications");

    // The real executable to launch: on macOS the binary inside our .app.
    static string Self => Platform.ExePath;

    public static void Create(ProfileEntry p, bool desktop = false)
    {
        try
        {
            // The launcher wears the profile's colour.
            string icon = null;
            try { icon = ProfileIcon.Write(p.Slug, Profiles.ColorOf(Profiles.LoadOrEmpty(), p)); }
            catch (Exception ex) { ErrorLog.Write(ex); }

            if (Platform.IsWindows)
            {
                Directory.CreateDirectory(WinStartMenu);
                WindowsLink(Path.Combine(WinStartMenu, Label(p.Name) + ".lnk"), "--launch \"" + p.Name + "\"", "Open Claude with the " + p.Name + " profile", icon);
                string d = Path.Combine(WinDesktop, Label(p.Name) + ".lnk");
                if (desktop) WindowsLink(d, "--launch \"" + p.Name + "\"", "Open Claude with the " + p.Name + " profile", icon);
            }
            else if (Platform.IsMac) MacBundle(p.Name, icon);
            else LinuxDesktopFile(p.Name, icon);
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    // Whether the profile's launcher exists (Start menu, ~/Applications, or the app menu).
    public static bool HasLauncher(string name) =>
        Platform.IsWindows ? File.Exists(Path.Combine(WinStartMenu, Label(name) + ".lnk"))
        : Platform.IsMac ? Directory.Exists(Path.Combine(MacFolder, Label(name) + ".app"))
        : File.Exists(Path.Combine(LinuxFolder, "claude-switch-" + Platform.Slug(name, false) + ".desktop"));

    public static void Remove(string name)
    {
        try
        {
            if (Platform.IsWindows)
            {
                foreach (string f in new[] { Path.Combine(WinStartMenu, Label(name) + ".lnk"), Path.Combine(WinDesktop, Label(name) + ".lnk") })
                    if (File.Exists(f)) File.Delete(f);
            }
            else if (Platform.IsMac)
            {
                string app = Path.Combine(MacFolder, Label(name) + ".app");
                if (Directory.Exists(app)) Directory.Delete(app, true);
            }
            else
            {
                string f = Path.Combine(LinuxFolder, "claude-switch-" + Platform.Slug(name, false) + ".desktop");
                if (File.Exists(f)) File.Delete(f);
            }
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    public static bool HasDesktopShortcut(string name) =>
        Platform.IsWindows && File.Exists(Path.Combine(WinDesktop, Label(name) + ".lnk"));

    /// <summary>Where a Windows .lnk points, or null if it can't be read.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string TargetOf(string lnkPath)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;
        object shell = Activator.CreateInstance(shellType);
        try
        {
            object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            try { return lnk.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null) as string; }
            finally { Marshal.ReleaseComObject(lnk); }
        }
        catch (Exception) { return null; }
        finally { Marshal.ReleaseComObject(shell); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void WindowsLink(string lnkPath, string args, string description, string icon = null)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        try
        {
            object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type t = lnk.GetType();
            void Set(string prop, object value) => t.InvokeMember(prop, BindingFlags.SetProperty, null, lnk, new[] { value });
            Set("TargetPath", Self);
            Set("Arguments", args);
            Set("WorkingDirectory", Path.GetDirectoryName(Self));
            Set("IconLocation", (icon != null && File.Exists(icon) ? icon : Self) + ",0");
            Set("Description", description);
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            Marshal.ReleaseComObject(lnk);
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    static void MacBundle(string name, string icon)
    {
        string app = Path.Combine(MacFolder, Label(name) + ".app");
        string macos = Path.Combine(app, "Contents", "MacOS"), res = Path.Combine(app, "Contents", "Resources");
        Directory.CreateDirectory(macos);
        Directory.CreateDirectory(res);
        string id = "io.github.ngonisama.claudeswitch.profile." + Platform.Slug(name, false);
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleName</key><string>{Esc(Label(name))}</string>
              <key>CFBundleIdentifier</key><string>{id}</string>
              <key>CFBundleExecutable</key><string>launch</string>
              <key>CFBundleIconFile</key><string>icon.icns</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>LSUIElement</key><true/>
            </dict></plist>
            """);
        string script = Path.Combine(macos, "launch");
        File.WriteAllText(script, "#!/bin/sh\nexec \"" + Self.Replace("\"", "\\\"") + "\" --launch \"" + name.Replace("\"", "") + "\"\n");
        Chmod(script);
        // The profile's coloured icon, else our own from ClaudeSwitch.app.
        string ownIcon = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Self), "..", "Resources", "icon.icns"));
        string use = icon != null && File.Exists(icon) ? icon : ownIcon;
        if (File.Exists(use)) File.Copy(use, Path.Combine(res, "icon.icns"), true);
    }

    static void LinuxDesktopFile(string name, string icon)
    {
        Directory.CreateDirectory(LinuxFolder);
        string file = Path.Combine(LinuxFolder, "claude-switch-" + Platform.Slug(name, false) + ".desktop");
        File.WriteAllText(file, string.Join("\n",
            "[Desktop Entry]",
            "Type=Application",
            "Name=" + Label(name),
            "Comment=Open Claude with the " + name + " profile",
            "Exec=\"" + Self + "\" --launch \"" + name.Replace("\"", "") + "\"",
            "Icon=" + (icon != null && File.Exists(icon) ? icon : "claude-switch"),
            "Terminal=false",
            "Categories=Utility;",
            ""));
        Chmod(file);
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static void Chmod(string path)
    {
        if (Platform.IsWindows) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    // Start Claude Switch when the user signs in (tray only).
    public static void SetStartAtLogin(bool on)
    {
        try
        {
            if (Platform.IsWindows)
            {
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Claude Switch.lnk");
                if (on) WindowsLink(lnk, "--tray", "Claude Switch in the tray");
                else if (File.Exists(lnk)) File.Delete(lnk);
            }
            else if (Platform.IsMac)
            {
                string plist = Path.Combine(Platform.Home, "Library", "LaunchAgents", "io.github.ngonisama.claudeswitch.plist");
                if (!on) { if (File.Exists(plist)) File.Delete(plist); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(plist));
                File.WriteAllText(plist, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0"><dict>
                      <key>Label</key><string>io.github.ngonisama.claudeswitch</string>
                      <key>ProgramArguments</key><array><string>{Esc(Self)}</string><string>--tray</string></array>
                      <key>RunAtLoad</key><true/>
                    </dict></plist>
                    """);
            }
            else
            {
                string dir = Path.Combine(Platform.ClaudeDataRoot, "autostart");
                string file = Path.Combine(dir, "claude-switch.desktop");
                if (!on) { if (File.Exists(file)) File.Delete(file); return; }
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, "[Desktop Entry]\nType=Application\nName=Claude Switch\nExec=\"" + Self + "\" --tray\nIcon=claude-switch\nX-GNOME-Autostart-enabled=true\n");
            }
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    public static bool StartsAtLogin =>
        Platform.IsWindows ? File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Claude Switch.lnk"))
        : Platform.IsMac ? File.Exists(Path.Combine(Platform.Home, "Library", "LaunchAgents", "io.github.ngonisama.claudeswitch.plist"))
        : File.Exists(Path.Combine(Platform.ClaudeDataRoot, "autostart", "claude-switch.desktop"));
}
