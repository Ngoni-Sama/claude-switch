// Where things live on each OS. Everything platform-specific about paths is
// here; process handling is in ClaudeApp, launchers in Shortcuts.
//
//                    Windows                       macOS                                   Linux
//   Claude data      %APPDATA%\Claude              ~/Library/Application Support/Claude    ~/.config/Claude
//   extra profiles   %APPDATA%\Claude-<Name>       ~/Library/Application Support/Claude-…  ~/.config/Claude-<Name>
//   our settings     %APPDATA%\Claude Switch       ~/Library/Application Support/Claude Switch   ~/.config/claude-switch
//   Claude Code      ~/.claude (or CLAUDE_CONFIG_DIR) on every OS
//
// Profiles sit next to Claude's own folder: on Windows the Cowork VM service
// looks for <AppData>\<folder>\vm_bundles, so they must be direct children.

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClaudeSwitch.Core;

public static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();

    public static string Name => IsWindows ? "Windows" : IsMac ? "macOS" : "Linux";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // The folder Claude Desktop's own data folder lives in.
    public static string ClaudeDataRoot
    {
        get
        {
            if (IsWindows) return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (IsMac) return Path.Combine(Home, "Library", "Application Support");
            string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".config") : xdg;
        }
    }

    public static string DefaultDataDir => Normal(Path.Combine(ClaudeDataRoot, "Claude"));

    public static string NewDataDir(string profileName) => Path.Combine(ClaudeDataRoot, "Claude-" + Slug(profileName, keepCase: true));

    // Claude Code's store when no CLAUDE_CONFIG_DIR is set.
    public static string DefaultConfigDir => Normal(Path.Combine(Home, ".claude"));

    public static string NewConfigDir(string profileName) => Path.Combine(Home, ".claude-" + Slug(profileName, keepCase: false));

    // Our own settings + profiles.json.
    public static string AppDir
    {
        get
        {
            string dir = IsLinux ? Path.Combine(ClaudeDataRoot, "claude-switch") : Path.Combine(ClaudeDataRoot, "Claude Switch");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // Where 1.x (Windows-only "Claude Profiles") kept its files, for migration.
    public static string LegacyAppDir => IsWindows
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Claude Profiles")
        : null;

    public static string ExePath => Environment.ProcessPath;

    public static StringComparison PathComparison => IsLinux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static bool SamePath(string a, string b) => string.Equals(a, b, PathComparison);

    public static string Key(string path) => IsLinux ? path : path.ToLowerInvariant();

    public static string Normal(string p)
    {
        string expanded = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded == "~")
            expanded = Home + expanded.Substring(1);
        return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // Stored as %APPDATA%\… / ~/… so profiles.json stays readable and portable.
    public static string Compress(string p)
    {
        if (string.IsNullOrEmpty(p)) return null;
        if (IsWindows)
        {
            foreach (string envVar in new[] { "APPDATA", "USERPROFILE" })   // APPDATA first: it's inside USERPROFILE
            {
                string root = (Environment.GetEnvironmentVariable(envVar) ?? "").TrimEnd('\\');
                if (root.Length == 0) continue;
                if (p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    return "%" + envVar + "%" + p.Substring(root.Length);
            }
            return p;
        }
        string home = Home.TrimEnd('/');
        return p == home ? "~" : p.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + p.Substring(home.Length) : p;
    }

    // Windows, Store build only: Claude's writes to %APPDATA%\<name> are
    // redirected into the package's own store, leaving the real folder empty.
    // Read where the data actually is, or a profile looks signed out and
    // sessionless when it isn't.
    //
    //   %APPDATA%\Claude-Work                                      (empty)
    //   %LOCALAPPDATA%\Packages\<family>\LocalCache\Roaming\Claude-Work   (real)
    //
    // Only used when the real folder has nothing in it, so profiles that write
    // normally (the default one does) are untouched.
    static readonly Dictionary<string, string> redirects = new(StringComparer.OrdinalIgnoreCase);

    public static string ResolveData(string dataDir)
    {
        string real = Normal(dataDir);
        if (!IsWindows) return real;
        lock (redirects)
        {
            if (redirects.TryGetValue(real, out string cached)) return cached ?? real;
            string found = FindRedirect(real);
            redirects[real] = found;
            return found ?? real;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string FindRedirect(string real)
    {
        try
        {
            if (HasContent(real)) return null;                       // it writes normally
            string appData = ClaudeDataRoot.TrimEnd('\\');
            if (!real.StartsWith(appData + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            string family = PackageFamily();
            if (family == null) return null;
            string candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", family, "LocalCache", "Roaming", real.Substring(appData.Length + 1));
            return HasContent(candidate) ? candidate : null;
        }
        catch (Exception ex) { ErrorLog.Write(ex); return null; }
    }

    // The Store build's private Roaming folder, where its redirected profile
    // folders live. Null when there's no Store build.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string PackageRoaming()
    {
        string family = PackageFamily();
        if (family == null) return null;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family, "LocalCache", "Roaming");
        return Directory.Exists(dir) ? dir : null;
    }

    static bool HasContent(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch (Exception) { return false; }
    }

    // "Claude_<version>_<arch>__<publisherId>" -> "Claude_<publisherId>"
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string PackageFamily()
    {
        const string repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(repo);
        if (key == null) return null;
        foreach (string name in key.GetSubKeyNames())
        {
            if (!name.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase)) continue;
            int sep = name.LastIndexOf("__", StringComparison.Ordinal);
            if (sep > 0) return "Claude_" + name[(sep + 2)..];
        }
        return null;
    }

    public static string Shorten(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string home = Home.TrimEnd('\\', '/');
        return path.StartsWith(home, PathComparison) ? "~" + path.Substring(home.Length) : path;
    }

    // Claude Code names a project folder after its path: C:\Users\me -> C--Users-me, /Users/me -> -Users-me
    public static string Encode(string path) => Regex.Replace(path, "[^A-Za-z0-9]", "-");

    public static string Slug(string name, bool keepCase)
    {
        string s = Regex.Replace(name.Trim(), "[^A-Za-z0-9 _.-]", "").Replace(' ', '-');
        return keepCase ? s : s.ToLowerInvariant();
    }

    public static bool ValidProfileName(string name)
    {
        name = name?.Trim() ?? "";
        return name.Length is > 0 and <= 40 && Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9 _.-]*$");
    }

    public static string Arch => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
}
