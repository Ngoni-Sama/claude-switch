// Keeping Claude current.
//
// Two different things can fall behind, and they fail in different ways:
//
//  1. Claude Desktop itself. On Windows you can end up with TWO installs — the
//     Microsoft Store (MSIX) package and the older "classic" installer under
//     %LOCALAPPDATA%\AnthropicClaude — each with its own updater. One updates,
//     the other rots, and whichever your shortcut points at is what you get.
//  2. The Claude Code runtime bundled inside each profile
//     (<profile data>/claude-code/<version>). It only updates while that
//     profile is open, so a profile you rarely use stays on an old build.
//
// Claude Switch can see all of that and say so. It deliberately does NOT
// pretend to know "the latest version from the website": there's no official
// version feed to read, so inventing one would mean inventing answers. What it
// does instead is compare what's actually installed, flag anything behind, and
// hand you to the right updater for your system.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeSwitch.Core;

public enum UpdateMode { Off, Notify, Automatic }

public class ClaudeInstall
{
    public string Kind;          // "Microsoft Store", "classic installer", "app bundle", "apt package"
    public string Version;
    public string Path;
    public bool IsPrimary;       // the one Claude Switch launches
    public DateTime Updated = DateTime.MinValue;
    // Classic installs update (and uninstall) through their own Update.exe.
    // When it's gone the copy can never update again.
    public bool UpdaterMissing;

    public Version Parsed => System.Version.TryParse(Version, out Version v) ? v : null;
    public string Describe => Kind + " " + (Version ?? "(unknown version)");
}

public class UpdateStatus
{
    public List<ClaudeInstall> Installs = new();
    public Dictionary<string, string> CodeVersions = new();   // profile -> bundled Claude Code version
    // Profiles whose window is open on an install that isn't the current one.
    public Dictionary<string, ClaudeInstall> RunningOn = new();
    public string NewestCode;
    public DateTime CheckedAt = DateTime.Now;

    /// <summary>
    /// Profiles open right now on an out-of-date copy of Claude. These can't be
    /// "updated" in place: the old and new apps are different distributions
    /// with different version schemes. They share the same profile folder
    /// though, so reopening the profile on the current app keeps everything.
    /// </summary>
    public List<string> OnOldApp =>
        RunningOn.Where(kv => kv.Value != null && !kv.Value.IsPrimary).Select(kv => kv.Key).ToList();

    public ClaudeInstall Primary => Installs.FirstOrDefault(i => i.IsPrimary) ?? Installs.FirstOrDefault();

    // A second install is the real trap: it has its own updater and its own
    // shortcuts, so it can sit months behind without you noticing.
    public bool Duplicate => Installs.Count > 1;

    // Every copy that isn't the one in use and has fallen behind it, or can no
    // longer update at all. There can be several: a machine can collect a copy
    // per installer run (one in %LOCALAPPDATA%, another under %ProgramData%).
    public List<ClaudeInstall> OldCopies
    {
        get
        {
            ClaudeInstall current = Primary;
            if (current == null || !Duplicate) return new List<ClaudeInstall>();
            return Installs
                .Where(i => i != current && (i.UpdaterMissing || (current.Updated - i.Updated).TotalDays > 14))
                .OrderBy(i => i.Updated)
                .ToList();
        }
    }

    // Profiles whose bundled Claude Code is older than the newest one present.
    public List<string> ProfilesBehind =>
        NewestCode == null ? new List<string>()
        : CodeVersions.Where(kv => kv.Value != null && kv.Value != NewestCode).Select(kv => kv.Key).ToList();

    public bool AnythingBehind => OldCopies.Count > 0 || ProfilesBehind.Count > 0 || OnOldApp.Count > 0;
}

public static class Updater
{
    public static UpdateMode Mode
    {
        get => Settings.UpdateMode switch { "off" => UpdateMode.Off, "auto" => UpdateMode.Automatic, _ => UpdateMode.Notify };
        set => Settings.UpdateMode = value switch { UpdateMode.Off => "off", UpdateMode.Automatic => "auto", _ => "notify" };
    }

    public static UpdateStatus Scan(List<ProfileEntry> profiles)
    {
        var s = new UpdateStatus();
        try { s.Installs = FindInstalls(); } catch (Exception ex) { ErrorLog.Write(ex); }

        string primary = ClaudeApp.ResolveExe();
        foreach (ClaudeInstall i in s.Installs)
            i.IsPrimary = primary != null && primary.StartsWith(i.Path, Platform.PathComparison);

        foreach (ProfileEntry p in profiles)
        {
            try { s.CodeVersions[p.Name] = BundledCode(p); }
            catch (Exception) { s.CodeVersions[p.Name] = null; }
            try { s.RunningOn[p.Name] = InstallRunning(p, s.Installs); }
            catch (Exception) { }
        }
        s.NewestCode = s.CodeVersions.Values.Where(v => v != null)
            .OrderByDescending(v => System.Version.TryParse(v, out Version pv) ? pv : new Version(0, 0))
            .FirstOrDefault();
        return s;
    }

    // Which install a profile's open window actually came from. Null when it
    // isn't running.
    static ClaudeInstall InstallRunning(ProfileEntry p, List<ClaudeInstall> installs)
    {
        int pid = ClaudeApp.FindMain(p.LaunchDir);
        if (pid == 0) return null;
        string exe = ClaudeApp.Query().FirstOrDefault(q => q.Pid == pid)?.Exe;
        if (exe == null) return null;
        return installs.FirstOrDefault(i => exe.StartsWith(i.Path, Platform.PathComparison));
    }

    // The Claude Code build a profile carries: <data>/claude-code/<version>/
    static string BundledCode(ProfileEntry p)
    {
        string dir = Path.Combine(p.Data, "claude-code");
        if (!Directory.Exists(dir)) return null;
        return Directory.GetDirectories(dir)
            .Select(Path.GetFileName)
            .Where(n => System.Version.TryParse(n, out _))
            .OrderByDescending(n => System.Version.Parse(n))
            .FirstOrDefault();
    }

    // ---- what's installed, per OS ------------------------------------------------

    static List<ClaudeInstall> FindInstalls()
    {
        var list = new List<ClaudeInstall>();
        if (Platform.IsWindows)
        {
            if (StorePackage() is ClaudeInstall store) list.Add(store);
            list.AddRange(ClassicInstalls());
            return list;
        }
        if (Platform.IsMac)
        {
            foreach (string app in new[] { "/Applications/Claude.app", Path.Combine(Platform.Home, "Applications", "Claude.app") })
            {
                string plist = Path.Combine(app, "Contents", "Info.plist");
                if (!File.Exists(plist)) continue;
                list.Add(new ClaudeInstall
                {
                    Kind = "app bundle", Path = app, Updated = Directory.GetLastWriteTime(app),
                    Version = PlistValue(plist, "CFBundleShortVersionString"),
                });
            }
            return list;
        }
        string deb = Run("dpkg-query", "-W", "-f=${Version}", "claude-desktop");
        if (!string.IsNullOrWhiteSpace(deb))
        {
            string exe = ClaudeApp.ResolveExe();
            list.Add(new ClaudeInstall
            {
                Kind = "apt package", Version = deb.Trim(), Path = exe ?? "/usr/bin/claude-desktop",
                Updated = exe != null && File.Exists(exe) ? File.GetLastWriteTime(exe) : DateTime.MinValue,
            });
        }
        return list;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static ClaudeInstall StorePackage()
    {
        const string repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(repo);
        if (key == null) return null;
        ClaudeInstall best = null;
        foreach (string name in key.GetSubKeyNames())
        {
            string[] parts = name.Split('_');
            if (parts.Length < 3 || parts[0] != "Claude" || !System.Version.TryParse(parts[1], out Version v)) continue;
            using var pkg = key.OpenSubKey(name);
            if (pkg?.GetValue("PackageRootFolder") is not string root || !Directory.Exists(root)) continue;
            if (best?.Parsed != null && v <= best.Parsed) continue;
            best = new ClaudeInstall { Kind = "Microsoft Store", Version = v.ToString(), Path = root, Updated = Directory.GetLastWriteTime(root) };
        }
        return best;
    }

    // Where the older Squirrel installer puts Claude. A per-user install lands
    // in %LOCALAPPDATA%; the "machine-wide" variant gives each user a folder
    // under %ProgramData%\<user>. Both look the same inside: app-<version>\.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static IEnumerable<string> ClassicRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.UserName, "AnthropicClaude");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static IEnumerable<ClaudeInstall> ClassicInstalls()
    {
        foreach (string root in ClassicRoots())
        {
            if (!Directory.Exists(root)) continue;
            string newest = NewestAppVersion(root);
            if (newest == null) continue;
            yield return new ClaudeInstall
            {
                Kind = "classic installer", Version = newest, Path = root,
                Updated = Directory.GetLastWriteTime(Path.Combine(root, "app-" + newest)),
                UpdaterMissing = !File.Exists(Path.Combine(root, "Update.exe")),
            };
        }
    }

    static string NewestAppVersion(string root) =>
        Directory.GetDirectories(root, "app-*")
            .Select(Path.GetFileName)
            .Select(n => n["app-".Length..])
            .Where(n => System.Version.TryParse(n, out _))
            .OrderByDescending(System.Version.Parse)
            .FirstOrDefault();

    static string PlistValue(string plist, string key)
    {
        try
        {
            string xml = File.ReadAllText(plist);
            Match m = Regex.Match(xml, "<key>" + Regex.Escape(key) + "</key>\\s*<string>([^<]*)</string>");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception) { return null; }
    }

    static string Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception) { return null; }
    }

    // ---- acting on it ---------------------------------------------------------------

    // Where updating actually happens, per system.
    public static string UpdateHow => Platform.IsWindows
        ? "Opens the Microsoft Store's Downloads and updates page, where Claude updates."
        : Platform.IsMac
            ? "Opens claude.ai/download for the current version."
            : "Claude updates with your system packages; the command is copied for you to run.";

    public static string LinuxCommand => "sudo apt update && sudo apt install --only-upgrade claude-desktop";

    public static void OpenUpdater()
    {
        if (Platform.IsWindows) { Links.Open("ms-windows-store://downloadsandupdates"); return; }
        Links.Open(Platform.IsMac ? Links.ClaudeDownload : Links.ClaudeLinux);
    }

    // A profile only refreshes its bundled Claude Code while it's open, so the
    // fix for "this profile is behind" is simply to open it.
    public static void RefreshProfile(ProfileEntry p) => Launcher.Spawn(p);

    /// <summary>
    /// Moves a profile from an old copy of Claude onto the current one.
    /// There's nothing to download: both copies read the same profile folder,
    /// so closing the old window and opening the profile again (Claude Switch
    /// always launches the newest install) brings every chat and the sign-in
    /// straight across. This is the fix for a window stuck on the old app,
    /// which can't update itself across distributions.
    /// </summary>
    public static void ReopenOnCurrent(ProfileEntry p)
    {
        Launcher.Stop(p.LaunchDir);
        Thread.Sleep(1200);
        Launcher.Spawn(p);
    }

    public static void ReopenAll(IEnumerable<ProfileEntry> profiles)
    {
        foreach (ProfileEntry p in profiles)
        {
            ReopenOnCurrent(p);
            Thread.Sleep(2500);
        }
    }

    // Automatic mode: open the profiles that are behind so they update
    // themselves. Nothing is installed behind your back — only Claude's own
    // updater runs, in Claude's own window.
    public static void AutoRefresh(UpdateStatus status, List<ProfileEntry> profiles)
    {
        if (Mode != UpdateMode.Automatic) return;
        Dictionary<string, int> running = ClaudeApp.FindRunning();
        foreach (string name in status.ProfilesBehind)
        {
            ProfileEntry p = Profiles.Find(profiles, name);
            if (p == null || running.ContainsKey(p.Key)) continue;   // already open: it'll update itself
            RefreshProfile(p);
            Thread.Sleep(2000);
        }
    }

    // ---- removing an old copy (Windows) -------------------------------------------
    //
    // Why not just run the copy's own uninstaller? Because the copies that most
    // need removing are the ones whose Update.exe is gone — that's usually why
    // they stopped updating — and then both their uninstaller and Settings >
    // Apps fail. Even where Update.exe exists it would run that old Claude's
    // own uninstall hook, which we can't vouch for. So this does what the
    // uninstaller does, itself, and nothing more:
    //
    //   * shortcuts that point into the copy (Desktop, Start menu, Startup, taskbar)
    //   * its entry in Installed apps, a start-at-login entry and a claude:// handler,
    //     but only where they point into the copy
    //   * the copy's own folder (app versions, packages, logs)
    //
    // It never touches %APPDATA%\Claude: that's your profile — sign-in, chats and
    // settings — and the current Claude uses the very same folder.

    /// <summary>Removes one old copy. Pass <paramref name="dryRun"/> to only list what would go.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static RemovalResult RemoveOldCopy(ClaudeInstall install, bool dryRun)
    {
        var refused = new RemovalResult();
        if (install == null || install.Kind != "classic installer" || install.IsPrimary)
        {
            refused.Problem = "Only an old classic copy can be removed here, never the Claude you're using.";
            return refused;
        }
        // Only ever a folder we found ourselves, whatever the caller passed in.
        string dir = ClassicRoots().FirstOrDefault(r => Platform.SamePath(Platform.Normal(r), Platform.Normal(install.Path)));
        if (dir == null)
        {
            refused.Problem = "That isn't a location Claude's classic installer uses.";
            return refused;
        }
        // Anything running from inside the copy holds its files open: Claude
        // itself, or helpers such as the Claude in Chrome bridge
        // (chrome-native-host.exe), which Chrome starts and keeps alive.
        List<string> busy = RunningFrom(dir);
        if (busy.Count > 0)   // in the preview too, so you hear it before confirming
        {
            bool chromeHelper = busy.Any(b => b.Contains("chrome-native-host", StringComparison.OrdinalIgnoreCase));
            bool claude = busy.Any(b => b.StartsWith("claude", StringComparison.OrdinalIgnoreCase));
            refused.Problem = "Still running from that copy: " + string.Join(", ", busy.Distinct(StringComparer.OrdinalIgnoreCase)) + ". " +
                (claude ? "Close that Claude window and its tray icon. " : "") +
                (chromeHelper ? "Quit Chrome or Edge completely (it keeps the old copy's Claude in Chrome helper open); it reconnects to the current Claude's helper when it starts again. " : "") +
                "Then press Remove again.";
            return refused;
        }
        return RemoveCopy(dir, WindowsShortcutFolders(), "Software", dryRun);
    }

    // Program names (e.g. "chrome-native-host.exe") with a process running from inside dir.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static List<string> RunningFrom(string dir)
    {
        var names = new List<string>();
        string inside = Platform.Normal(dir) + "\\";
        try
        {
            using var q = new System.Management.ManagementObjectSearcher("SELECT Name, ExecutablePath FROM Win32_Process");
            foreach (System.Management.ManagementObject o in q.Get())
                using (o)
                    if (o["ExecutablePath"] is string path && path.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                        names.Add(o["Name"] as string ?? Path.GetFileName(path));
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
        return names;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static IEnumerable<string> WindowsShortcutFolders()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch"),
        }.Where(d => !string.IsNullOrEmpty(d));
    }

    /// <summary>
    /// The removal itself, against explicit locations so tests can point it at
    /// a scratch folder and a scratch registry key. <paramref name="registryBase"/>
    /// is "Software" for real use (under HKEY_CURRENT_USER).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static RemovalResult RemoveCopy(string dir, IEnumerable<string> shortcutFolders, string registryBase, bool dryRun)
    {
        var r = new RemovalResult { DryRun = dryRun };
        dir = Platform.Normal(dir);

        // Belt and braces: it must look like a Squirrel Claude install, and it
        // must not be anywhere near the profile folders.
        string roamingClaude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (!string.Equals(Path.GetFileName(dir), "AnthropicClaude", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(dir) || !Directory.EnumerateDirectories(dir, "app-*").Any() ||
            roamingClaude.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
        {
            r.Problem = "That folder doesn't look like an old Claude install, so nothing was touched.";
            return r;
        }
        string inside = dir + "\\";
        bool Points(string s) => !string.IsNullOrEmpty(s) && s.Replace("\"", "").Contains(inside, StringComparison.OrdinalIgnoreCase);

        // 1. Shortcuts. A shortcut to a deleted program is worse than none.
        // Shallow: shortcuts live near the top (Desktop\Apps\Claude.lnk is depth 1),
        // and the Desktop can hold whole projects we've no reason to walk.
        var walk = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        foreach (string folder in shortcutFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string lnk in Directory.EnumerateFiles(folder, "*.lnk", walk))
            {
                if (!Points(Shortcuts.TargetOf(lnk))) continue;
                string label = "Shortcut " + Platform.Shorten(lnk);
                if (dryRun) { r.Removed.Add(label); continue; }
                try { File.Delete(lnk); r.Removed.Add(label); }
                catch (UnauthorizedAccessException) { r.Skipped.Add(label + " (shared by all users; needs an administrator)"); }
                catch (IOException ex) { r.Skipped.Add(label + " (" + ex.Message + ")"); }
            }
        }

        // 2. Registry: Installed apps entry, start-at-login, claude:// handler.
        Microsoft.Win32.RegistryKey hkcu = Microsoft.Win32.Registry.CurrentUser;   // shared; never dispose
        {
            string uninstall = registryBase + @"\Microsoft\Windows\CurrentVersion\Uninstall";
            using (var key = hkcu.OpenSubKey(uninstall, writable: !dryRun))
            {
                foreach (string name in key?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub == null) continue;
                    string loc = sub.GetValue("InstallLocation") as string, cmd = sub.GetValue("UninstallString") as string;
                    // InstallLocation has no trailing slash, so compare it exactly too.
                    bool ours = Points(cmd) || Points(sub.GetValue("DisplayIcon") as string) ||
                                (loc != null && Platform.SamePath(Platform.Normal(loc), dir));
                    if (!ours) continue;
                    string display = ((sub.GetValue("DisplayName") as string ?? name) + " " + (sub.GetValue("DisplayVersion") as string ?? "")).Trim();
                    sub.Dispose();
                    if (!dryRun) key.DeleteSubKeyTree(name, false);
                    r.Removed.Add("Installed-apps entry \"" + display + "\"");
                }
            }
            using (var run = hkcu.OpenSubKey(registryBase + @"\Microsoft\Windows\CurrentVersion\Run", writable: !dryRun))
            {
                foreach (string value in run?.GetValueNames() ?? Array.Empty<string>())
                {
                    if (!Points(run.GetValue(value) as string)) continue;
                    if (!dryRun) run.DeleteValue(value, false);
                    r.Removed.Add("Start-at-login entry \"" + value + "\"");
                }
            }
            using (var cmd = hkcu.OpenSubKey(registryBase + @"\Classes\claude\shell\open\command"))
            {
                if (Points(cmd?.GetValue("") as string))
                {
                    if (!dryRun) hkcu.DeleteSubKeyTree(registryBase + @"\Classes\claude", false);
                    r.Removed.Add("claude:// link handler (it pointed at the old copy)");
                }
            }
        }

        // 3. The program files themselves.
        r.Bytes = SizeOf(dir);
        if (dryRun) { r.Removed.Add("Program files in " + Platform.Shorten(dir)); return r; }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);   // Squirrel leaves some read-only
                Directory.Delete(dir, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    var busy = OperatingSystem.IsWindows() ? RunningFrom(dir) : new List<string>();
                    r.Problem = "Some files are still in use" + (busy.Count > 0 ? " by " + string.Join(", ", busy.Distinct()) : "") +
                                ", so part of " + Platform.Shorten(dir) + " is left. Close " + (busy.Count > 0 ? "that program" : "anything using it") +
                                " (or restart your computer) and press Remove again.";
                }
                else Thread.Sleep(1500);
            }
        }
        if (!Directory.Exists(dir)) r.Removed.Add("Program files in " + Platform.Shorten(dir));
        else r.Bytes -= SizeOf(dir);
        return r;
    }

    public static long SizeOf(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Sum(f => { try { return new FileInfo(f).Length; } catch (Exception) { return 0L; } });
        }
        catch (Exception) { return 0; }
    }
}

public class RemovalResult
{
    public bool DryRun;
    public List<string> Removed = new();   // what went (or, in a dry run, would go)
    public List<string> Skipped = new();   // found but couldn't be removed
    public long Bytes;                     // disk space freed (or that would be)
    public string Problem;                 // null when everything worked

    public bool Ok => Problem == null;
    public string Size => Bytes >= 1L << 30 ? (Bytes / (double)(1L << 30)).ToString("0.0") + " GB" : (Bytes / (double)(1L << 20)).ToString("0") + " MB";
}
