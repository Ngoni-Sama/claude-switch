// Creating, adding and removing profiles (first-run setup and the dashboard).

namespace ClaudeSwitch.Core;

public static class ProfileSetup
{
    // First run: `current` names the account Claude is already signed into (it
    // keeps the default data folder); `others` get their own folders. Profiles
    // that already exist keep their folders: never move a signed-in account.
    public static List<ProfileEntry> Create(string current, IEnumerable<string> others, bool separateMemory, bool desktopShortcuts)
    {
        List<ProfileEntry> list = Profiles.LoadOrEmpty();
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(current)) names.Add(current.Trim());
        names.AddRange(others.Select(o => o.Trim()).Where(o => o.Length > 0));

        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Profiles.Find(list, name) != null) continue;
            bool isCurrent = string.Equals(name, current?.Trim(), StringComparison.OrdinalIgnoreCase) && !list.Any(p => p.IsDefault);
            Add(list, name, isCurrent, separateMemory);
        }
        Remember(list);
        Profiles.Save(list);
        foreach (ProfileEntry p in list) Shortcuts.Create(p, desktopShortcuts);
        return list;
    }

    static ProfileEntry Add(List<ProfileEntry> list, string name, bool useDefault, bool separateMemory)
    {
        string data = useDefault ? Platform.DefaultDataDir : Platform.NewDataDir(name);
        var p = new ProfileEntry
        {
            Name = name,
            DataDir = Platform.Compress(data),
            ConfigDir = separateMemory && !useDefault ? Platform.Compress(Platform.NewConfigDir(name)) : null,
        };
        list.Add(p);
        return p;
    }

    public static ProfileEntry AddOne(string name, bool separateMemory)
    {
        List<ProfileEntry> list = Profiles.LoadOrEmpty();
        if (Profiles.Find(list, name) != null) throw new InvalidOperationException("There's already a profile called " + name + ".");
        ProfileEntry p = Add(list, name, useDefault: !list.Any(x => x.IsDefault), separateMemory);
        Profiles.Save(list);
        Shortcuts.Create(p, list.Any(x => Shortcuts.HasDesktopShortcut(x.Name)));
        return p;
    }

    // Removes the profile from the list and its launchers. Its folders stay
    // (sign-in, history) unless deleteData, which only ever removes folders
    // Claude Switch created (Claude-<Name>, .claude-<name>), never the default.
    public static void Remove(string name, bool deleteData)
    {
        List<ProfileEntry> list = Profiles.LoadOrEmpty();
        ProfileEntry p = Profiles.Find(list, name);
        if (p == null) return;
        Shortcuts.Remove(p.Name);
        list.Remove(p);
        Profiles.Save(list);
        if (!deleteData) return;
        Launcher.Stop(p.LaunchDir);
        foreach (string dir in DeletableDirs(p))
            try { Directory.Delete(dir, true); } catch (Exception ex) { ErrorLog.Write(ex); }
    }

    public static List<string> DeletableDirs(ProfileEntry p)
    {
        var dirs = new List<string>();
        if (Ours(p.Data, Platform.ClaudeDataRoot, "Claude-")) dirs.Add(p.Data);
        if (!string.IsNullOrEmpty(p.ConfigDir) && Ours(p.Config, Platform.Home, ".claude-")) dirs.Add(p.Config);
        return dirs;
    }

    static bool Ours(string dir, string parent, string prefix)
    {
        if (!Directory.Exists(dir)) return false;
        string up = Path.GetDirectoryName(dir), leaf = Path.GetFileName(dir);
        return up != null && Platform.SamePath(up.TrimEnd('\\', '/'), parent.TrimEnd('\\', '/')) &&
               leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && leaf.Length > prefix.Length;
    }

    public static void Remember(List<ProfileEntry> list) =>
        Transfer.Remember(list, list.ToDictionary(p => p.Name, Accounts.Read));

    // Windows uninstaller hook: launchers, link registration, start-at-login.
    public static void UninstallCleanup()
    {
        foreach (ProfileEntry p in Profiles.LoadOrEmpty()) Shortcuts.Remove(p.Name);
        LinkRouter.Unregister();
        Shortcuts.SetStartAtLogin(false);
    }
}
