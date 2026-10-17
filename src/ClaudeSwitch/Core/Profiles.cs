// profiles.json: the list of profiles (name -> Claude data folder + optional
// Claude Code store), plus the account last seen in each so a sign-in that
// lands in the wrong window can be spotted.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSwitch.Core;

public class ProfileEntry
{
    public string Name { get; set; }
    public string DataDir { get; set; }
    public string ConfigDir { get; set; }
    public string AccountUuid { get; set; }
    public string Email { get; set; }
    // "#RRGGBB" so Work and Personal look different everywhere: card, pill,
    // launcher icon. Empty = one is picked from the palette by position.
    public string Color { get; set; }
    // Opened every time Claude Switch starts, whatever was open last time.
    public bool OpenAtStart { get; set; }

    // Characters safe for a shell command name or a file name: "Client A" -> "client-a".
    [JsonIgnore] public string Slug => Platform.Slug(Name, keepCase: false);

    // Where this profile's data really is. On Windows the Store build of
    // Claude has its writes redirected, so this isn't always DataDir itself.
    [JsonIgnore] public string Data => Platform.ResolveData(DataDir);

    // What to pass Claude as --user-data-dir: always the plain path, because
    // the redirect is something Windows does to the app, not a path it takes.
    [JsonIgnore] public string LaunchDir => Platform.Normal(DataDir);
    [JsonIgnore] public string Config => string.IsNullOrEmpty(ConfigDir) ? Platform.DefaultConfigDir : Platform.Normal(ConfigDir);
    [JsonIgnore] public bool IsDefault => Platform.SamePath(Data, Platform.DefaultDataDir);
    [JsonIgnore] public string Key => Platform.Key(LaunchDir);   // matches Claude's command line
}

public static class Profiles
{
    public static string FilePath => Path.Combine(Platform.AppDir, "profiles.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static List<ProfileEntry> Load()
    {
        Migrate();
        if (!File.Exists(FilePath)) return new List<ProfileEntry>();
        return JsonSerializer.Deserialize<List<ProfileEntry>>(File.ReadAllText(FilePath), Opts) ?? new List<ProfileEntry>();
    }

    public static List<ProfileEntry> LoadOrEmpty()
    {
        try { return Load(); }
        catch (Exception ex) { ErrorLog.Write(ex); return new List<ProfileEntry>(); }
    }

    public static void Save(List<ProfileEntry> list) => Json.WriteAtomic(FilePath, JsonSerializer.Serialize(list, Opts));

    public static ProfileEntry Find(List<ProfileEntry> list, string name) =>
        list.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // Either path matches: Claude is launched with LaunchDir, but on Windows its
    // data may really live in the redirected Data folder.
    public static ProfileEntry FindByDataDir(List<ProfileEntry> list, string dataDir) =>
        list.FirstOrDefault(p => Platform.SamePath(p.LaunchDir, Platform.Normal(dataDir)) || Platform.SamePath(p.Data, Platform.Normal(dataDir)));

    // ---- colours -----------------------------------------------------------------

    // Distinct, readable on both themes, and none of them the warning/danger hues.
    public static readonly string[] Palette =
        { "#D97757", "#3B82F6", "#16A34A", "#9333EA", "#DB2777", "#0891B2", "#CA8A04", "#64748B" };

    public static string ColorOf(List<ProfileEntry> list, ProfileEntry p)
    {
        if (!string.IsNullOrEmpty(p.Color) && IsHex(p.Color)) return p.Color.ToUpperInvariant();
        int i = Math.Max(0, list.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase)));
        return Palette[i % Palette.Length];
    }

    public static bool IsHex(string s) => s != null && System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$");

    // Claude Profiles 1.x (Windows) kept profiles.json + settings.json next to
    // its exe. Copy them over once; the old install keeps working until removed.
    static bool migrated;

    static void Migrate()
    {
        if (migrated) return;
        migrated = true;
        string legacy = Platform.LegacyAppDir;
        if (legacy == null || File.Exists(FilePath)) return;
        try
        {
            foreach (string f in new[] { "profiles.json", "settings.json" })
            {
                string src = Path.Combine(legacy, f), dst = Path.Combine(Platform.AppDir, f);
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
            }
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }
}
