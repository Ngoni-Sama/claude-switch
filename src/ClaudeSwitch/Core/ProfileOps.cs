// Everyday changes to a profile: rename, recolour, duplicate, copy MCP servers
// from another profile, and adopting profile folders made outside the app.
//
// The ground rule from the rest of the app holds here too: sign-ins are never
// read, copied or moved. A duplicated profile signs in again once.

using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

public static class ProfileOps
{
    // ---- rename -------------------------------------------------------------------

    // Renames the profile everywhere Claude Switch knows it: the list, its
    // launchers, unify membership and the "reopen last time" memory. Folders
    // stay where they are, so Claude's own saved paths keep working.
    public static string Rename(string from, string to)
    {
        to = to?.Trim();
        if (!Platform.ValidProfileName(to)) return "Use letters, numbers, spaces, - _ or . (up to 40 characters).";
        List<ProfileEntry> list = Profiles.LoadOrEmpty();
        ProfileEntry p = Profiles.Find(list, from);
        if (p == null) return "There's no profile called " + from + ".";
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && Profiles.Find(list, to) != null)
            return "There's already a profile called " + to + ".";

        bool desktop = Shortcuts.HasDesktopShortcut(p.Name);
        Shortcuts.Remove(p.Name);
        p.Name = to;
        Profiles.Save(list);
        Shortcuts.Create(p, desktop);

        var members = Unify.Members();
        if (members.RemoveAll(m => string.Equals(m, from, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            members.Add(to);
            Settings.SetUnifyMembers(members);
        }
        LastSession last = SessionState.Last();
        if (last.Profiles.RemoveAll(n => string.Equals(n, from, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            last.Profiles.Add(to);
            Settings.SetLastSession(last.Profiles);
        }
        Terminal.RefreshIfInstalled();
        return null;
    }

    public static void SetColor(string name, string hex)
    {
        List<ProfileEntry> list = Profiles.LoadOrEmpty();
        ProfileEntry p = Profiles.Find(list, name);
        if (p == null || !Profiles.IsHex(hex)) return;
        p.Color = hex.ToUpperInvariant();
        Profiles.Save(list);
        Shortcuts.Create(p, Shortcuts.HasDesktopShortcut(p.Name));   // launcher icons wear the colour
    }

    public static void SetOpenAtStart(string name, bool on)
    {
        List<ProfileEntry> list = Profiles.LoadOrEmpty();
        ProfileEntry p = Profiles.Find(list, name);
        if (p == null) return;
        p.OpenAtStart = on;
        Profiles.Save(list);
    }

    // ---- duplicate ----------------------------------------------------------------

    // What a duplicate leaves behind: sign-ins, and caches Claude rebuilds itself.
    static readonly string[] NotCopied =
    {
        "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache",
        "Crashpad", "blob_storage", "Local Storage", "Session Storage", "IndexedDB", "WebStorage", "Service Worker",
        "Network", "Cookies", "Cookies-journal", "Login Data", "Login Data-journal", "SingletonLock", "SingletonCookie", "SingletonSocket",
        "lockfile", "vm_bundles",
    };

    public class DuplicateResult
    {
        public ProfileEntry Profile;
        public long Bytes;
        public string Problem;
    }

    // Copies a profile's chats, Code sessions, settings and Claude Code memory
    // into a new profile. The new one signs in on first open: sign-ins aren't
    // duplicated. The source must be closed so its files are consistent.
    public static DuplicateResult Duplicate(string sourceName, string newName, Action<string> progress)
    {
        var r = new DuplicateResult();
        newName = newName?.Trim();
        List<ProfileEntry> list = Profiles.LoadOrEmpty();
        ProfileEntry src = Profiles.Find(list, sourceName);
        if (src == null) { r.Problem = "There's no profile called " + sourceName + "."; return r; }
        if (!Platform.ValidProfileName(newName)) { r.Problem = "Use letters, numbers, spaces, - _ or . (up to 40 characters)."; return r; }
        if (Profiles.Find(list, newName) != null) { r.Problem = "There's already a profile called " + newName + "."; return r; }
        if (ClaudeApp.FindMain(src.LaunchDir) != 0) { r.Problem = "Close " + src.Name + "'s Claude window first, so its files are complete."; return r; }

        string launchDir = Platform.NewDataDir(newName);
        if (Directory.Exists(launchDir) && Directory.EnumerateFileSystemEntries(launchDir).Any())
        {
            r.Problem = "A folder called " + Path.GetFileName(launchDir) + " already exists. Pick another name, or add that folder as a profile instead.";
            return r;
        }
        // Copy next to wherever the source really lives, so a Store-build
        // Claude finds the copy where it looks for its data.
        string target = launchDir;
        if (!Platform.SamePath(src.Data, src.LaunchDir))
            target = Path.Combine(Path.GetDirectoryName(src.Data)!, Path.GetFileName(launchDir));

        try
        {
            progress?.Invoke("Copying " + src.Name + "'s data…");
            r.Bytes += CopyFiltered(src.Data, target);
            StripSignIn(Path.Combine(target, "config.json"));

            string configDir = null;
            if (!string.IsNullOrEmpty(src.ConfigDir) || src.IsDefault)
            {
                configDir = Platform.NewConfigDir(newName);
                progress?.Invoke("Copying Claude Code memory and settings…");
                r.Bytes += CopyClaudeCode(src.Config, configDir);
            }

            var p = new ProfileEntry
            {
                Name = newName,
                DataDir = Platform.Compress(launchDir),
                ConfigDir = configDir != null ? Platform.Compress(configDir) : null,
            };
            list.Add(p);
            Profiles.Save(list);
            Shortcuts.Create(p, list.Any(x => Shortcuts.HasDesktopShortcut(x.Name)));
            r.Profile = p;
        }
        catch (Exception ex) { ErrorLog.Write(ex); r.Problem = ex.Message; }
        return r;
    }

    internal static long CopyFiltered(string from, string to)
    {
        long bytes = 0;
        Directory.CreateDirectory(to);
        foreach (string entry in Directory.EnumerateFileSystemEntries(from))
        {
            string name = Path.GetFileName(entry);
            if (NotCopied.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            string dst = Path.Combine(to, name);
            if (Directory.Exists(entry))
            {
                if (new DirectoryInfo(entry).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                bytes += CopyFiltered(entry, dst);
            }
            else
            {
                try { File.Copy(entry, dst, overwrite: false); bytes += new FileInfo(entry).Length; }
                catch (IOException ex) { ErrorLog.Write(ex); }   // a file Claude holds open: skip it
            }
        }
        return bytes;
    }

    // Claude Code's store minus its credentials and per-machine state.
    static long CopyClaudeCode(string from, string to)
    {
        long bytes = 0;
        Directory.CreateDirectory(to);
        foreach (string name in new[] { "CLAUDE.md", "settings.json", "projects", "agents", "skills", "commands", "hooks", "plugins" })
        {
            string src = Path.Combine(from, name);
            if (Directory.Exists(src))
            {
                if (new DirectoryInfo(src).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;   // a unify link: don't copy shared data
                FsLink.MergeInto(src, Path.Combine(to, name));
                bytes += Updater.SizeOf(Path.Combine(to, name));
            }
            else if (File.Exists(src)) { File.Copy(src, Path.Combine(to, name), overwrite: false); bytes += new FileInfo(src).Length; }
        }
        // MCP servers, but not the account or credentials in .claude.json.
        JsonObject cfg = Json.ReadObject(Path.Combine(from, ".claude.json"));
        if (Json.Obj(cfg, "mcpServers") is JsonObject servers)
            Json.WriteAtomic(Path.Combine(to, ".claude.json"), new JsonObject { ["mcpServers"] = servers.DeepClone() }.ToJsonString(Json.Options));
        return bytes;
    }

    // Drops the cached sign-in from a copied config.json; the copy signs in itself.
    internal static void StripSignIn(string configJson)
    {
        JsonObject cfg = Json.ReadObject(configJson);
        if (cfg == null) return;
        foreach (string key in cfg.Select(kv => kv.Key).Where(k => k.StartsWith("oauth", StringComparison.OrdinalIgnoreCase) || k == "lastKnownAccountUuid").ToList())
            cfg.Remove(key);
        Json.WriteAtomic(configJson, cfg.ToJsonString(Json.Options));
    }

    // ---- MCP servers --------------------------------------------------------------

    public class McpCopyResult
    {
        public List<string> Desktop = new(), Code = new();
        public string Backup, Problem;
        public bool Any => Desktop.Count + Code.Count > 0;
    }

    public static (List<string> desktop, List<string> code) McpServersOf(ProfileEntry p) =>
        (Names(Json.Obj(Json.ReadObject(Path.Combine(p.Data, "claude_desktop_config.json")), "mcpServers")),
         Names(Json.Obj(Json.ReadObject(Path.Combine(p.Config, ".claude.json")), "mcpServers")));

    static List<string> Names(JsonObject o) => o?.Select(kv => kv.Key).OrderBy(k => k).ToList() ?? new List<string>();

    // Copies the source's MCP servers into the target, replacing same-named
    // ones. Both the desktop app's servers and Claude Code's user-level ones.
    // Each target file is backed up first; nothing else in it changes.
    public static McpCopyResult CopyMcpServers(ProfileEntry from, ProfileEntry to)
    {
        var r = new McpCopyResult();
        if (from == null || to == null || from == to) { r.Problem = "Pick two different profiles."; return r; }
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try
        {
            r.Desktop = Merge(Path.Combine(from.Data, "claude_desktop_config.json"), Path.Combine(to.Data, "claude_desktop_config.json"), stamp, ref r.Backup);
            if (!Platform.SamePath(from.Config, to.Config))
                r.Code = Merge(Path.Combine(from.Config, ".claude.json"), Path.Combine(to.Config, ".claude.json"), stamp, ref r.Backup);
        }
        catch (Exception ex) { ErrorLog.Write(ex); r.Problem = ex.Message; }
        return r;
    }

    static List<string> Merge(string srcFile, string dstFile, string stamp, ref string backup)
    {
        JsonObject servers = Json.Obj(Json.ReadObject(srcFile), "mcpServers");
        if (servers == null || servers.Count == 0) return new List<string>();
        JsonObject dst = Json.ReadObject(dstFile) ?? new JsonObject();
        if (File.Exists(dstFile))
        {
            backup = dstFile + ".before-mcp-copy-" + stamp;
            File.Copy(dstFile, backup, overwrite: true);
        }
        JsonObject target = Json.Obj(dst, "mcpServers") ?? new JsonObject();
        foreach (var kv in servers) target[kv.Key] = kv.Value?.DeepClone();
        dst["mcpServers"] = target;
        Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
        Json.WriteAtomic(dstFile, dst.ToJsonString(Json.Options));
        return servers.Select(kv => kv.Key).ToList();
    }

    // ---- adopting folders made elsewhere -------------------------------------------

    public class Found
    {
        public string Name, DataDir, ConfigDir, Source;
    }

    // Claude-<Name> folders (from aliases, scripts, or the old launcher) that no
    // profile uses yet. Dismissed ones stay hidden.
    public static List<Found> FindUnlisted(List<ProfileEntry> list)
    {
        var found = new List<Found>();
        var hidden = new HashSet<string>(Settings.DismissedFolders(), StringComparer.OrdinalIgnoreCase);
        var roots = new List<string> { Platform.ClaudeDataRoot };
        if (Platform.IsWindows && Platform.PackageRoaming() is string pr) roots.Add(pr);
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string dir in Directory.EnumerateDirectories(root, "Claude-*"))
            {
                string leaf = Path.GetFileName(dir), name = leaf["Claude-".Length..].Replace('-', ' ').Trim();
                string launch = Path.Combine(Platform.ClaudeDataRoot, leaf);
                if (name.Length == 0 || hidden.Contains(Platform.Compress(launch))) continue;
                if (list.Any(p => Platform.SamePath(p.LaunchDir, launch) || Platform.SamePath(p.Data, dir))) continue;
                if (found.Any(f => Platform.SamePath(Platform.Normal(f.DataDir), launch))) continue;
                if (!LooksLikeProfile(dir)) continue;
                string config = Platform.NewConfigDir(name);
                found.Add(new Found
                {
                    Name = Platform.ValidProfileName(name) ? name : "Imported " + (found.Count + 1),
                    DataDir = Platform.Compress(launch),
                    ConfigDir = Directory.Exists(config) ? Platform.Compress(config) : null,
                    Source = Platform.Shorten(dir),
                });
            }
        }
        return found;
    }

    // A folder Claude has actually run in: Electron writes "Local State" and
    // "Preferences" on first start, and Claude its own config.json. A folder
    // holding only claude_desktop_config.json (e.g. left by a third-party
    // provider setup) isn't a profile.
    static bool LooksLikeProfile(string dir) =>
        new[] { "Local State", "Preferences", "config.json", "claude-code-sessions" }.Any(n => File.Exists(Path.Combine(dir, n)) || Directory.Exists(Path.Combine(dir, n)));

    public static ProfileEntry Adopt(Found f)
    {
        List<ProfileEntry> list = Profiles.LoadOrEmpty();
        string name = f.Name;
        for (int i = 2; Profiles.Find(list, name) != null; i++) name = f.Name + " " + i;
        var p = new ProfileEntry { Name = name, DataDir = f.DataDir, ConfigDir = f.ConfigDir };
        list.Add(p);
        Profiles.Save(list);
        Shortcuts.Create(p, list.Any(x => Shortcuts.HasDesktopShortcut(x.Name)));
        ProfileSetup.Remember(list);
        return p;
    }

    public static void Dismiss(Found f) => Settings.AddDismissedFolder(f.DataDir);
}
