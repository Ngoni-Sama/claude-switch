// Finding Claude Desktop and its running instances on each OS.
//
// Claude Desktop is Electron. Every process carries --user-data-dir except a
// plain launch of the default profile; helper processes carry --type=..., the
// main (window-owning) process doesn't. Claude Code's own `claude` binary has a
// similar name, so desktop processes are told apart by their executable path.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeSwitch.Core;

public static class ClaudeApp
{
    public class Proc
    {
        public int Pid;
        public string Exe;
        public string Cmd;
    }

    static readonly Regex[] DataDirArg =
    {
        new("\"--user-data-dir=([^\"]+)\""),
        new("--user-data-dir=\"([^\"]+)\""),
        new("--user-data-dir='([^']+)'"),
        new("--user-data-dir=(\\S+)"),
    };

    // ---- the Claude executable ---------------------------------------------

    public static string ResolveExe()
    {
        if (Platform.IsWindows) return WindowsExe();
        if (Platform.IsMac)
        {
            foreach (string app in new[] { "/Applications/Claude.app", Path.Combine(Platform.Home, "Applications", "Claude.app") })
            {
                string exe = Path.Combine(app, "Contents", "MacOS", "Claude");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }
        foreach (string exe in new[] { "/usr/bin/claude-desktop", "/usr/local/bin/claude-desktop", "/opt/Claude/claude-desktop",
                                        "/usr/lib/claude-desktop/claude-desktop", "/snap/bin/claude-ai-desktop" })
            if (File.Exists(exe)) return exe;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            string exe = Path.Combine(dir, "claude-desktop");
            if (dir.Length > 0 && File.Exists(exe)) return exe;
        }
        return null;
    }

    // MSIX: the install path has a version in it that changes on every update,
    // and WindowsApps can't be listed without admin. The per-user AppModel
    // repository in HKCU names every installed package with its root folder.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string WindowsExe()
    {
        const string repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        string best = null;
        Version bestVersion = null;
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(repo))
        {
            if (key != null)
            {
                foreach (string name in key.GetSubKeyNames())
                {
                    string[] parts = name.Split('_');   // Claude_<version>_<arch>__<publisherId>
                    if (parts.Length < 3 || parts[0] != "Claude" || !Version.TryParse(parts[1], out Version v)) continue;
                    using var pkg = key.OpenSubKey(name);
                    if (pkg?.GetValue("PackageRootFolder") is not string root) continue;
                    string exe = Path.Combine(root, "app", "Claude.exe");
                    if (File.Exists(exe) && (bestVersion == null || v > bestVersion)) { best = exe; bestVersion = v; }
                }
            }
        }
        if (best != null) return best;
        // Non-Store installs (Squirrel) live under %LOCALAPPDATA%\AnthropicClaude.
        string squirrel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude", "claude.exe");
        if (File.Exists(squirrel)) return squirrel;
        return Query().Select(p => p.Exe).FirstOrDefault(IsDesktopExe);
    }

    // ---- processes ------------------------------------------------------------

    static bool IsDesktopExe(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        if (Platform.IsWindows)
            return Regex.IsMatch(exe, @"\\WindowsApps\\Claude_[^\\]+\\app\\Claude\.exe$", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(exe, @"\\AnthropicClaude\\(app-[^\\]+\\)?claude\.exe$", RegexOptions.IgnoreCase);
        if (Platform.IsMac) return exe.Contains("/Claude.app/Contents/MacOS/Claude", StringComparison.Ordinal);
        return exe.EndsWith("/claude-desktop", StringComparison.Ordinal) || exe.Contains("/claude-desktop/", StringComparison.Ordinal) ||
               exe.Contains("claude-ai-desktop", StringComparison.Ordinal);
    }

    public static List<Proc> Query() => Platform.IsWindows ? QueryWindows() : QueryPs();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static List<Proc> QueryWindows()
    {
        var list = new List<Proc>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='Claude.exe'");
            foreach (System.Management.ManagementObject p in searcher.Get())
            {
                using (p)
                    list.Add(new Proc { Pid = Convert.ToInt32(p["ProcessId"]), Exe = p["ExecutablePath"] as string, Cmd = p["CommandLine"] as string });
            }
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
        return list;
    }

    // macOS / Linux: `ps` gives the full command line; the executable is the
    // part before the first " --" argument (paths may contain spaces).
    static List<Proc> QueryPs()
    {
        var list = new List<Proc>();
        try
        {
            var psi = new ProcessStartInfo("ps", "-axww -o pid=,command=") { RedirectStandardOutput = true, UseShellExecute = false };
            using var ps = Process.Start(psi);
            string output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                int sp = line.IndexOf(' ');
                if (sp <= 0 || !int.TryParse(line.AsSpan(0, sp), out int pid)) continue;
                string cmd = line.Substring(sp + 1).Trim();
                int dash = cmd.IndexOf(" --", StringComparison.Ordinal);
                string exe = dash > 0 ? cmd.Substring(0, dash) : cmd;
                if (!IsDesktopExe(exe)) continue;
                list.Add(new Proc { Pid = pid, Exe = exe, Cmd = cmd });
            }
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
        return list;
    }

    // Data dir key a desktop-app process belongs to, or null for other processes.
    static string DirOf(Proc p)
    {
        if (p.Cmd == null || !IsDesktopExe(p.Exe)) return null;
        foreach (Regex r in DataDirArg)
        {
            Match m = r.Match(p.Cmd);
            if (m.Success)
            {
                try { return Platform.Key(Platform.Normal(m.Groups[1].Value)); }
                catch (Exception) { return null; }
            }
        }
        return Platform.Key(Platform.DefaultDataDir);
    }

    // Data dir key -> PID of the Electron main process.
    public static Dictionary<string, int> FindRunning()
    {
        var result = new Dictionary<string, int>();
        foreach (Proc p in Query())
        {
            if (p.Cmd == null || p.Cmd.Contains("--type=")) continue;
            string dir = DirOf(p);
            if (dir != null) result.TryAdd(dir, p.Pid);
        }
        return result;
    }

    public static int FindMain(string dataDir) =>
        FindRunning().TryGetValue(Platform.Key(dataDir), out int pid) ? pid : 0;

    // Every process (main + helpers) of one profile.
    public static List<int> AllProcesses(string dataDir)
    {
        string want = Platform.Key(dataDir);
        return Query().Where(p => DirOf(p) == want).Select(p => p.Pid).ToList();
    }
}
