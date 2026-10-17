// Opening, focusing and restarting a profile's Claude window.
//
// A per-profile mutex serialises launches (double-clicks, shortcut + tray), so
// two windows never race on one data folder. If the profile is already open
// it's focused (Windows) or handed off through Electron's single-instance lock
// (all OSes): a second Claude on the same data folder forwards to the running
// one, which shows its window, and exits at once.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeSwitch.Core;

public static class Launcher
{
    // UI hooks (set by the app): errors, and the first sign-in guide (false = cancel).
    public static Action<string> ShowError = msg => ErrorLog.Write(new Exception(msg));
    public static Func<ProfileEntry, bool> ConfirmFirstSignIn = _ => true;

    // Same name scheme as the original PowerShell launcher, so they guard each other.
    static string MutexName(string dataDir)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant()));
        return "Local\\ClaudeDesktopClone_" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    static string Exe()
    {
        string exe = ClaudeApp.ResolveExe();
        if (exe == null)
            ShowError("Claude Desktop wasn't found.\n\nInstall it from claude.ai/download" +
                      (Platform.IsLinux ? " (Ubuntu/Debian: sudo apt install claude-desktop)" : "") + ", then try again.");
        return exe;
    }

    static Process StartClaude(string exe, string dataDir, string configDir, string extraArg = null)
    {
        var psi = new ProcessStartInfo(exe);
        if (Platform.IsWindows)
        {
            // ShellExecute is the documented way to start the Store (MSIX) exe;
            // it can't take ArgumentList, so quote by hand (paths may have spaces).
            psi.UseShellExecute = true;
            psi.Arguments = "--user-data-dir=\"" + dataDir + "\"" + (extraArg != null ? " \"" + extraArg.Replace("\"", "%22") + "\"" : "");
        }
        else
        {
            psi.UseShellExecute = false;
            psi.ArgumentList.Add("--user-data-dir=" + dataDir);
            if (extraArg != null) psi.ArgumentList.Add(extraArg);
        }
        // Always set or clear it: a profile without its own Claude Code folder
        // must not inherit another profile's (or the shell's) value.
        string value = string.IsNullOrEmpty(configDir) ? null : configDir;
        if (Platform.IsWindows) Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", value);   // ShellExecute inherits ours
        else if (value != null) psi.Environment["CLAUDE_CONFIG_DIR"] = value;
        else psi.Environment.Remove("CLAUDE_CONFIG_DIR");
        return Process.Start(psi);
    }

    public static int Run(ProfileEntry p) => Run(p.LaunchDir, string.IsNullOrEmpty(p.ConfigDir) ? null : p.Config);

    public static int Run(string dataDir, string configDir)
    {
        string exe = Exe();
        if (exe == null) return 1;
        dataDir = Platform.Normal(dataDir);

        using var mutex = new Mutex(false, MutexName(dataDir));
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(45)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            ShowError("Claude (" + Platform.Shorten(dataDir) + ") is still starting.\n\nWait for its window to appear.");
            return 1;
        }
        try
        {
            int pid = ClaudeApp.FindMain(dataDir);
            if (pid != 0)
            {
                if (!Focus(pid)) StartClaude(exe, dataDir, configDir);   // hand-off
                return 0;
            }

            ProfileEntry prof = Profiles.FindByDataDir(Profiles.LoadOrEmpty(), dataDir);
            if (prof != null && !Accounts.Read(prof).SignedIn && !ConfirmFirstSignIn(prof)) return 0;

            Directory.CreateDirectory(dataDir);
            if (!string.IsNullOrEmpty(configDir))
            {
                configDir = Platform.Normal(configDir);
                Directory.CreateDirectory(configDir);
            }
            Process proc = StartClaude(exe, dataDir, configDir);

            // Hold the mutex until the window exists, so a second click during
            // startup waits and then focuses this window instead of racing it.
            if (proc != null)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(40);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(300);
                    proc.Refresh();
                    if (proc.HasExited || (Platform.IsWindows && proc.MainWindowHandle != IntPtr.Zero)) break;
                    if (!Platform.IsWindows && DateTime.UtcNow > deadline.AddSeconds(-34)) break;   // ~6 s: no window handle API
                }
            }
            return 0;
        }
        finally { mutex.ReleaseMutex(); }
    }

    // Passes a claude:// link to one profile (Electron forwards it to the running window).
    public static void Forward(ProfileEntry p, string url)
    {
        string exe = Exe();
        if (exe != null) StartClaude(exe, p.LaunchDir, string.IsNullOrEmpty(p.ConfigDir) ? null : p.Config, url);
    }

    // Ends every process of a profile. Claude only hides to the tray when its
    // window is closed, so this is the only way to make it re-read sessions.
    public static void Stop(string dataDir)
    {
        dataDir = Platform.Normal(dataDir);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            List<int> pids = ClaudeApp.AllProcesses(dataDir);
            if (pids.Count == 0) return;
            int main = ClaudeApp.FindMain(dataDir);
            if (main != 0) { pids.Remove(main); pids.Insert(0, main); }
            foreach (int pid in pids)
            {
                try { using var proc = Process.GetProcessById(pid); proc.Kill(); }
                catch (Exception) { }
            }
            Thread.Sleep(1500);
        }
    }

    // Launch in a separate process so the UI never blocks on the wait-for-window loop.
    public static void Spawn(ProfileEntry p)
    {
        var psi = new ProcessStartInfo(Platform.ExePath) { UseShellExecute = false };
        psi.ArgumentList.Add("--launch-dir");
        psi.ArgumentList.Add(p.LaunchDir);
        if (!string.IsNullOrEmpty(p.ConfigDir)) { psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(p.Config); }
        Process.Start(psi);
    }

    public static void Restart(ProfileEntry p)
    {
        Stop(p.LaunchDir);
        Spawn(p);
    }

    public static void Activate(ProfileEntry p, int pid)
    {
        if (pid != 0 && Focus(pid)) return;
        Spawn(p);
    }

    // ---- focus ------------------------------------------------------------------

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    // Windows: bring the window forward directly. Elsewhere there's no portable
    // way, so return false and let the caller hand off through Electron.
    public static bool Focus(int pid)
    {
        if (!Platform.IsWindows) return false;
        try
        {
            using Process p = Process.GetProcessById(pid);
            IntPtr hwnd = p.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return false;
            if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);   // SW_RESTORE
            SetForegroundWindow(hwnd);
            Thread.Sleep(150);
            return GetForegroundWindow() == hwnd;
        }
        catch (ArgumentException) { return false; }
    }
}
