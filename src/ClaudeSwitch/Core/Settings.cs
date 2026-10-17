// settings.json (next to profiles.json): theme, learned weekly reset times,
// copied usage reports, and one-time notices. Plus error.log.

using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

public static class Settings
{
    static string FilePath => Path.Combine(Platform.AppDir, "settings.json");
    static readonly object gate = new();

    static JsonObject Load() => Json.ReadObject(FilePath) ?? new JsonObject();

    static void Update(Action<JsonObject> change)
    {
        lock (gate)
        {
            JsonObject d = Load();
            change(d);
            try { Json.WriteAtomic(FilePath, d.ToJsonString(Json.Options)); }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }
    }

    // "light" (default) or "dark".
    public static string Theme
    {
        get => Json.Str(Load(), "theme") == "dark" ? "dark" : "light";
        set => Update(d => d["theme"] = value);
    }

    public static bool HandoffNoticeShown
    {
        get => Json.Bool(Load(), "handoffNoticeShown");
        set => Update(d => d["handoffNoticeShown"] = value);
    }

    public static bool OnboardingDone
    {
        get => Json.Bool(Load(), "onboardingDone");
        set => Update(d => d["onboardingDone"] = value);
    }

    // Last seen weekly reset per account. The weekly limit resets at the same
    // time every 7 days, so one is enough to know all of them.
    public static DateTime WeeklyAnchor(string accountUuid) =>
        Json.FromEpochMs(Json.Long(Json.Obj(Load(), "weeklyAnchors"), accountUuid));

    public static void SetWeeklyAnchor(string accountUuid, DateTime anchor)
    {
        long ms = Json.ToEpochMs(anchor);
        if (Json.Long(Json.Obj(Load(), "weeklyAnchors"), accountUuid) == ms) return;   // unchanged: don't rewrite
        Update(d =>
        {
            var anchors = Json.Obj(d, "weeklyAnchors") ?? new JsonObject();
            anchors[accountUuid] = ms;
            d["weeklyAnchors"] = anchors;
        });
    }

    // ---- unify mode --------------------------------------------------------

    // Profiles that share one memory and one set of conversations.
    public static JsonArray UnifyMembers() => Json.Arr(Load(), "unify") ?? new JsonArray();

    public static void SetUnifyMembers(List<string> names) => Update(d =>
    {
        var arr = new JsonArray();
        foreach (string n in names) arr.Add(n);
        d["unify"] = arr;
    });

    // ---- keeping Claude up to date -----------------------------------------

    // "notify" (default), "auto" or "off".
    public static string UpdateMode
    {
        get => Json.Str(Load(), "updateMode") is string s && s is "auto" or "off" ? s : "notify";
        set => Update(d => d["updateMode"] = value);
    }

    public static DateTime UpdateCheckedAt
    {
        get => Json.FromEpochMs(Json.Long(Load(), "updateCheckedAt"));
        set => Update(d => d["updateCheckedAt"] = Json.ToEpochMs(value));
    }

    // ---- what to do at startup ---------------------------------------------

    public static string Startup
    {
        get => Json.Str(Load(), "startup") is string s && s is "restore" or "fresh" ? s : "ask";
        set => Update(d => d["startup"] = value);
    }

    public static JsonObject LastSession() => Json.Obj(Load(), "lastSession") ?? new JsonObject();

    public static void SetLastSession(List<string> profiles) => Update(d =>
    {
        var arr = new JsonArray();
        foreach (string n in profiles) arr.Add(n);
        d["lastSession"] = new JsonObject { ["at"] = Json.ToEpochMs(DateTime.Now), ["profiles"] = arr };
    });

    // ---- switches with a default -------------------------------------------

    static bool Flag(string key, bool fallback) =>
        Load().TryGetPropertyValue(key, out JsonNode n) && n is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    static void SetFlag(string key, bool value) => Update(d => d[key] = value);

    // Ctrl+Alt+1..9 jump to a profile; Ctrl+Alt+0 opens Claude Switch. Off by default.
    public static bool Hotkeys { get => Flag("hotkeys", false); set => SetFlag("hotkeys", value); }

    // Reopen the profiles a Claude update closed. On by default.
    public static bool ReopenAfterUpdate { get => Flag("reopenAfterUpdate", true); set => SetFlag("reopenAfterUpdate", value); }

    // Experimental: each profile's windows get their own taskbar button and icon (Windows).
    public static bool SeparateTaskbar { get => Flag("separateTaskbar", false); set => SetFlag("separateTaskbar", value); }

    // Terminal commands (claude-work, ...) installed in the shell profiles.
    public static bool TerminalCommands { get => Flag("terminalCommands", false); set => SetFlag("terminalCommands", value); }

    // ---- Claude Switch's own updates ---------------------------------------

    // "notify" (default), "auto" or "off".
    public static string SelfUpdate
    {
        get => Json.Str(Load(), "selfUpdate") is string s && s is "auto" or "off" ? s : "notify";
        set => Update(d => d["selfUpdate"] = value);
    }

    public static DateTime SelfUpdateCheckedAt
    {
        get => Json.FromEpochMs(Json.Long(Load(), "selfUpdateCheckedAt"));
        set => Update(d => d["selfUpdateCheckedAt"] = Json.ToEpochMs(value));
    }

    // ---- reopening after a Claude update -----------------------------------

    public static string SeenClaudeVersion
    {
        get => Json.Str(Load(), "seenClaudeVersion");
        set => Update(d => d["seenClaudeVersion"] = value);
    }

    // ---- folders that pick a profile for Claude Code in a terminal ----------

    // [{ "path": "C:\\code\\client-a", "profile": "Client A" }, ...]
    public static List<(string Path, string Profile)> FolderRules() =>
        (Json.Arr(Load(), "folderRules") ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(o => (Json.Str(o, "path"), Json.Str(o, "profile")))
            .Where(r => !string.IsNullOrEmpty(r.Item1) && !string.IsNullOrEmpty(r.Item2))
            .ToList();

    public static void SetFolderRules(IEnumerable<(string Path, string Profile)> rules) => Update(d =>
    {
        var arr = new JsonArray();
        foreach (var r in rules) arr.Add(new JsonObject { ["path"] = r.Path, ["profile"] = r.Profile });
        d["folderRules"] = arr;
    });

    // ---- Claude-* folders the person chose not to add ------------------------

    public static List<string> DismissedFolders() =>
        (Json.Arr(Load(), "dismissedFolders") ?? new JsonArray()).OfType<JsonValue>().Select(v => v.ToString()).ToList();

    public static void AddDismissedFolder(string dir) => Update(d =>
    {
        var arr = Json.Arr(d, "dismissedFolders") ?? new JsonArray();
        if (!arr.OfType<JsonValue>().Any(v => string.Equals(v.ToString(), dir, StringComparison.OrdinalIgnoreCase))) arr.Add(dir);
        d["dismissedFolders"] = arr;
    });

    public static JsonObject Report(string accountUuid) => Json.Obj(Json.Obj(Load(), "reports"), accountUuid);

    public static void SetReport(string accountUuid, JsonObject report) => Update(d =>
    {
        var reports = Json.Obj(d, "reports") ?? new JsonObject();
        reports[accountUuid] = report;
        d["reports"] = reports;
    });
}

// Unexpected errors go to error.log instead of silently killing the app.
public static class ErrorLog
{
    public static string FilePath => Path.Combine(Platform.AppDir, "error.log");

    public static void Write(Exception ex)
    {
        if (ex == null) return;
        try
        {
            File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception) { }
    }
}
