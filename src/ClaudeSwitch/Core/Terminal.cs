// Claude Code in a terminal, per profile.
//
// Claude Code keeps its sign-in, memory and history in CLAUDE_CONFIG_DIR, so
// running it "as Work" is just running it with Work's folder. Three ways in:
//
//   * Open in Terminal: a new terminal window already set up for that profile.
//   * Terminal commands: claude-work, claude-personal, ... and claude-default,
//     usable in any shell once turned on.
//   * Folder rules: plain `claude` inside a chosen folder picks that folder's
//     profile by itself (PowerShell, bash and zsh).
//
// Turning terminal commands on adds one marked block to the shell start-up
// files (backed up first) and, on Windows, Claude Switch's command folder to
// the user PATH. Turning it off removes exactly those.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeSwitch.Core;

public static class Terminal
{
    const string Begin = "# >>> claude-switch >>>";
    const string End = "# <<< claude-switch <<<";

    public static string ShellDir => Path.Combine(Platform.AppDir, "shell");
    public static string BinDir => Path.Combine(Platform.AppDir, "bin");
    public const string InstallDocs = "https://code.claude.com/docs/en/setup";

    public static string CommandName(ProfileEntry p) => "claude-" + p.Slug;

    // Null for the default profile: Claude Code then uses ~/.claude.
    static string ConfigFor(ProfileEntry p) => string.IsNullOrEmpty(p.ConfigDir) ? null : p.Config;

    // ---- is Claude Code installed? -------------------------------------------------

    public static string FindCli()
    {
        string[] names = Platform.IsWindows ? new[] { "claude.exe", "claude.cmd", "claude.ps1" } : new[] { "claude" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.Add(Path.Combine(Platform.Home, ".local", "bin"));
        if (!Platform.IsWindows) { dirs.Add("/opt/homebrew/bin"); dirs.Add("/usr/local/bin"); }
        foreach (string d in dirs)
            foreach (string n in names)
            {
                try
                {
                    string f = Path.Combine(Environment.ExpandEnvironmentVariables(d.Trim('"')), n);
                    if (File.Exists(f) && !f.StartsWith(BinDir, Platform.PathComparison)) return f;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    // ---- a one-liner to copy ----------------------------------------------------------

    public static string CommandLine(ProfileEntry p)
    {
        string cfg = ConfigFor(p);
        if (Platform.IsWindows)
            return cfg == null
                ? "Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue; claude"
                : "$env:CLAUDE_CONFIG_DIR = '" + cfg.Replace("'", "''") + "'; claude";
        return cfg == null ? "env -u CLAUDE_CONFIG_DIR claude" : "CLAUDE_CONFIG_DIR=" + Sh(cfg) + " claude";
    }

    static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";
    static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    // ---- open a terminal as a profile ----------------------------------------------

    // Returns null on success, or what went wrong.
    public static string Open(ProfileEntry p, string folder = null, string email = null)
    {
        folder = Directory.Exists(folder ?? "") ? folder : Platform.Home;
        string cfg = ConfigFor(p);
        try
        {
            if (Platform.IsWindows) return OpenWindows(p, folder, cfg, email);
            if (Platform.IsMac)
            {
                string cmd = "cd " + Sh(folder) + " && clear && echo " + Sh("Claude Code as " + p.Name + (email != null ? " (" + email + ")" : "")) +
                             " && " + (cfg == null ? "env -u CLAUDE_CONFIG_DIR claude" : "CLAUDE_CONFIG_DIR=" + Sh(cfg) + " claude");
                string esc = cmd.Replace("\\", "\\\\").Replace("\"", "\\\"");
                Process.Start("osascript", new[] { "-e", "tell application \"Terminal\" to do script \"" + esc + "\"", "-e", "tell application \"Terminal\" to activate" });
                return null;
            }
            string run = "cd " + Sh(folder) + "; echo " + Sh("Claude Code as " + p.Name) + "; " +
                         (cfg == null ? "env -u CLAUDE_CONFIG_DIR claude" : "CLAUDE_CONFIG_DIR=" + Sh(cfg) + " claude") + "; exec bash";
            foreach (var (exe, args) in new (string, string[])[]
            {
                ("x-terminal-emulator", new[] { "-e", "bash", "-lc", run }),
                ("gnome-terminal", new[] { "--", "bash", "-lc", run }),
                ("konsole", new[] { "-e", "bash", "-lc", run }),
                ("xfce4-terminal", new[] { "-x", "bash", "-lc", run }),
                ("xterm", new[] { "-e", "bash", "-lc", run }),
            })
            {
                try { Process.Start(exe, args); return null; }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return "No terminal app was found. Copy the command instead and run it in your terminal.";
        }
        catch (Exception ex) { ErrorLog.Write(ex); return ex.Message; }
    }

    static string OpenWindows(ProfileEntry p, string folder, string cfg, string email)
    {
        // The script travels base64-encoded, so no path or name needs quoting
        // through Windows Terminal's or cmd's own rules.
        var s = new StringBuilder();
        s.AppendLine(cfg == null ? "Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue" : "$env:CLAUDE_CONFIG_DIR = " + Ps(cfg));
        s.AppendLine("$Host.UI.RawUI.WindowTitle = " + Ps("Claude Code (" + p.Name + ")"));
        s.AppendLine("Set-Location -LiteralPath " + Ps(folder));
        s.AppendLine("Write-Host " + Ps("Claude Code as " + p.Name + (email != null ? " (" + email + ")" : "")) + " -ForegroundColor Cyan");
        s.AppendLine("if (Get-Command claude -ErrorAction SilentlyContinue) { claude } else { Write-Host " +
                     Ps("Claude Code isn't installed. See " + InstallDocs) + " -ForegroundColor Yellow }");
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(s.ToString()));

        string shell = FindOnPath("pwsh.exe") ?? "powershell.exe";
        string wt = FindOnPath("wt.exe") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        var psi = File.Exists(wt) ? new ProcessStartInfo(wt) : new ProcessStartInfo(shell);
        if (File.Exists(wt))
        {
            foreach (string a in new[] { "new-tab", "--title", "Claude Code (" + p.Name + ")", "-d", folder, shell }) psi.ArgumentList.Add(a);
        }
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);
        psi.UseShellExecute = false;
        psi.WorkingDirectory = folder;
        // Don't hand our own environment's CLAUDE_CONFIG_DIR to the new shell.
        psi.Environment.Remove("CLAUDE_CONFIG_DIR");
        Process.Start(psi);
        return null;
    }

    static string FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => { try { return Path.Combine(d.Trim('"'), exe); } catch (ArgumentException) { return null; } })
            .FirstOrDefault(f => f != null && File.Exists(f));

    // ---- terminal commands -------------------------------------------------------

    public static bool Installed => Settings.TerminalCommands;

    public static void RefreshIfInstalled()
    {
        if (!Installed) return;
        try { WriteScripts(Profiles.LoadOrEmpty()); } catch (Exception ex) { ErrorLog.Write(ex); }
    }

    /// <summary>Turns the commands on. Returns the files it changed, for the confirmation.</summary>
    public static List<string> Install()
    {
        var changed = new List<string>();
        WriteScripts(Profiles.LoadOrEmpty());
        foreach (string rc in StartupFiles())
            if (AddBlock(rc, SourceLine(rc))) changed.Add(Platform.Shorten(rc));
        if (Platform.IsWindows && AddToUserPath(BinDir)) changed.Add("your user PATH (+ " + Platform.Shorten(BinDir) + ")");
        Settings.TerminalCommands = true;
        return changed;
    }

    public static List<string> Uninstall()
    {
        var changed = new List<string>();
        foreach (string rc in StartupFiles())
            if (RemoveBlock(rc)) changed.Add(Platform.Shorten(rc));
        if (Platform.IsWindows && RemoveFromUserPath(BinDir)) changed.Add("your user PATH");
        try { if (Directory.Exists(BinDir)) Directory.Delete(BinDir, true); } catch (Exception ex) { ErrorLog.Write(ex); }
        Settings.TerminalCommands = false;
        return changed;
    }

    // The start-up files we manage. PowerShell 5 and 7 each have their own.
    static IEnumerable<string> StartupFiles()
    {
        if (Platform.IsWindows)
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return Path.Combine(docs, "WindowsPowerShell", "profile.ps1");
            yield return Path.Combine(docs, "PowerShell", "profile.ps1");
            yield break;
        }
        yield return Path.Combine(Platform.Home, ".bashrc");
        yield return Path.Combine(Platform.Home, ".zshrc");
    }

    static string SourceLine(string rc) => rc.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
        ? "$cs = " + Ps(Path.Combine(ShellDir, "claude-switch.ps1")) + "; if (Test-Path -LiteralPath $cs) { . $cs }"
        : "[ -f " + Sh(Path.Combine(ShellDir, "claude-switch.sh")) + " ] && . " + Sh(Path.Combine(ShellDir, "claude-switch.sh"));

    static bool AddBlock(string rc, string line)
    {
        string text = File.Exists(rc) ? File.ReadAllText(rc) : "";
        if (text.Contains(Begin)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(rc)!);
        if (File.Exists(rc)) File.Copy(rc, rc + ".claude-switch.bak", overwrite: true);
        string nl = text.Contains("\r\n") || Platform.IsWindows ? "\r\n" : "\n";
        string block = (text.Length > 0 && !text.EndsWith("\n") ? nl : "") + Begin + nl + line + nl + End + nl;
        File.WriteAllText(rc, text + block, new UTF8Encoding(false));
        return true;
    }

    static bool RemoveBlock(string rc)
    {
        if (!File.Exists(rc)) return false;
        string text = File.ReadAllText(rc);
        int a = text.IndexOf(Begin, StringComparison.Ordinal), b = text.IndexOf(End, StringComparison.Ordinal);
        if (a < 0 || b < a) return false;
        int stop = b + End.Length;
        while (stop < text.Length && (text[stop] == '\r' || text[stop] == '\n')) stop++;
        File.WriteAllText(rc, text[..a] + text[stop..], new UTF8Encoding(false));
        return true;
    }

    // ---- the generated scripts ------------------------------------------------------

    static void WriteScripts(List<ProfileEntry> profiles)
    {
        Directory.CreateDirectory(ShellDir);
        var rules = Settings.FolderRules();
        if (Platform.IsWindows)
        {
            Directory.CreateDirectory(BinDir);
            foreach (string old in Directory.GetFiles(BinDir, "claude-*.cmd")) File.Delete(old);
            foreach (var (name, text) in CmdShims(profiles)) File.WriteAllText(Path.Combine(BinDir, name), text, Encoding.ASCII);
            File.WriteAllText(Path.Combine(ShellDir, "claude-switch.ps1"), PowerShellScript(profiles, rules), new UTF8Encoding(true));
            return;
        }
        File.WriteAllText(Path.Combine(ShellDir, "claude-switch.sh"), ShScript(profiles, rules), new UTF8Encoding(false));
    }

    static List<(string path, ProfileEntry profile)> Resolve(List<ProfileEntry> profiles, IEnumerable<(string Path, string Profile)> rules) =>
        rules.Select(r => (path: Platform.Normal(r.Path), profile: Profiles.Find(profiles, r.Profile)))
             .Where(r => r.profile != null)
             .OrderByDescending(r => r.path.Length)            // the most specific folder wins
             .ToList();

    // claude-<profile>.cmd for every profile, plus claude-default.cmd.
    internal static List<(string name, string text)> CmdShims(List<ProfileEntry> profiles)
    {
        var shims = profiles.Select(p => (CommandName(p) + ".cmd",
            "@echo off\r\nrem Claude Code as " + p.Name + ". Generated by Claude Switch.\r\nsetlocal\r\n" +
            "set \"CLAUDE_CONFIG_DIR=" + (ConfigFor(p) ?? "") + "\"\r\nclaude %*\r\n")).ToList();
        shims.Add(("claude-default.cmd",
            "@echo off\r\nrem Claude Code on its own default folder. Generated by Claude Switch.\r\nsetlocal\r\nset \"CLAUDE_CONFIG_DIR=\"\r\nclaude %*\r\n"));
        return shims;
    }

    internal static string PowerShellScript(List<ProfileEntry> profiles, IEnumerable<(string Path, string Profile)> ruleList)
    {
        var rules = Resolve(profiles, ruleList);
        {
            var ps = new StringBuilder();
            ps.AppendLine("# Generated by Claude Switch; edits are overwritten. Turn it off in Claude Switch.");
            ps.AppendLine("$global:ClaudeSwitchRules = @(");
            foreach (var r in rules)
                ps.AppendLine("  @{ Path = " + Ps(r.path) + "; Name = " + Ps(r.profile.Name) + "; Config = " + Ps(ConfigFor(r.profile) ?? "") + " }");
            ps.AppendLine(")");
            ps.AppendLine(@"function global:claude {
  $real = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $real) { Write-Host 'Claude Code is not installed.' -ForegroundColor Yellow; return }
  $old = $env:CLAUDE_CONFIG_DIR
  if (-not $old) {
    $here = (Get-Location).ProviderPath
    foreach ($r in $global:ClaudeSwitchRules) {
      if ($here -eq $r.Path -or $here.StartsWith($r.Path + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host ('-> profile ' + $r.Name) -ForegroundColor DarkGray
        if ($r.Config) { $env:CLAUDE_CONFIG_DIR = $r.Config }
        break
      }
    }
  }
  try { & $real.Source @args } finally { $env:CLAUDE_CONFIG_DIR = $old }
}");
            return ps.ToString();
        }
    }

    internal static string ShScript(List<ProfileEntry> profiles, IEnumerable<(string Path, string Profile)> ruleList)
    {
        var rules = Resolve(profiles, ruleList);
        var sh = new StringBuilder();
        sh.AppendLine("# Generated by Claude Switch; edits are overwritten. Turn it off in Claude Switch.");
        sh.AppendLine("unalias claude 2>/dev/null");
        foreach (ProfileEntry p in profiles)
        {
            string cfg = ConfigFor(p);
            sh.AppendLine(CommandName(p) + "() { " + (cfg == null ? "env -u CLAUDE_CONFIG_DIR command claude \"$@\"" : "CLAUDE_CONFIG_DIR=" + Sh(cfg) + " command claude \"$@\"") + "; }");
        }
        sh.AppendLine("claude-default() { env -u CLAUDE_CONFIG_DIR command claude \"$@\"; }");
        sh.AppendLine("claude() {");
        sh.AppendLine("  if [ -z \"$CLAUDE_CONFIG_DIR\" ]; then");
        sh.AppendLine("    case \"$PWD/\" in");
        foreach (var r in rules)
        {
            string cfg = ConfigFor(r.profile);
            sh.AppendLine("      " + Sh(r.path + "/") + "*) printf '\\033[2m-> profile %s\\033[0m\\n' " + Sh(r.profile.Name) + " >&2; " +
                          (cfg == null ? "command claude \"$@\"" : "CLAUDE_CONFIG_DIR=" + Sh(cfg) + " command claude \"$@\"") + "; return ;;");
        }
        sh.AppendLine("    esac");
        sh.AppendLine("  fi");
        sh.AppendLine("  command claude \"$@\"");
        sh.AppendLine("}");
        return sh.ToString();
    }

    // ---- folder rules -------------------------------------------------------------

    public static void AddRule(string folder, string profile)
    {
        var rules = Settings.FolderRules().Where(r => !Platform.SamePath(Platform.Normal(r.Path), Platform.Normal(folder))).ToList();
        rules.Add((Platform.Normal(folder), profile));
        Settings.SetFolderRules(rules);
        RefreshIfInstalled();
    }

    public static void RemoveRule(string folder)
    {
        Settings.SetFolderRules(Settings.FolderRules().Where(r => !Platform.SamePath(Platform.Normal(r.Path), Platform.Normal(folder))));
        RefreshIfInstalled();
    }

    // ---- Windows user PATH ----------------------------------------------------------
    // Written as REG_EXPAND_SZ through the registry: Environment.SetEnvironmentVariable
    // would rewrite it as REG_SZ and break entries like %USERPROFILE%\bin.

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool AddToUserPath(string dir)
    {
        using var env = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        string path = env?.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = path.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Any(p => Platform.SamePath(Environment.ExpandEnvironmentVariables(p).TrimEnd('\\'), dir.TrimEnd('\\')))) return false;
        parts.Add(dir);
        env.SetValue("Path", string.Join(";", parts), Microsoft.Win32.RegistryValueKind.ExpandString);
        Broadcast();
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool RemoveFromUserPath(string dir)
    {
        using var env = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        string path = env?.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (path == null) return false;
        var parts = path.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        int removed = parts.RemoveAll(p => Platform.SamePath(Environment.ExpandEnvironmentVariables(p).TrimEnd('\\'), dir.TrimEnd('\\')));
        if (removed == 0) return false;
        env.SetValue("Path", string.Join(";", parts), Microsoft.Win32.RegistryValueKind.ExpandString);
        Broadcast();
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    // Tells Explorer (and new terminals it starts) that the environment changed.
    static void Broadcast()
    {
        try { SendMessageTimeout((IntPtr)0xffff, 0x001A, UIntPtr.Zero, "Environment", 0x0002, 3000, out _); }
        catch (Exception) { }
    }
}
