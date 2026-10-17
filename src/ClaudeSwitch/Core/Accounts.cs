// What each profile is signed into, its usage, and its Claude Code sessions.
// Read-only views over Claude Desktop's own files (same names on every OS):
//
//   <data>/config.json                     lastKnownAccountUuid, oauth:* (signed in)
//   <data>/plan-usage-history.json         occasional samples of 5-hour (fh) / weekly (sd) %
//   <data>/claude-code-sessions/<account>/<org>/local_<id>.json   Code-tab session records
//   <config>/.claude.json                  oauthAccount: email, organisation (Claude Code)
//   <config>/projects/<encoded cwd>/<cliSessionId>.jsonl          transcripts

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeSwitch.Core;

public class AccountInfo
{
    public string Uuid, Email, OrgUuid, OrgName, DisplayName;
    public bool SignedIn;
    public int FiveHour = -1, SevenDay = -1;   // last logged reading, -1 = none
    public DateTime UsageAt = DateTime.MinValue;

    // Filled by Limits.Apply: what to show, with reset times.
    public Bucket Session, Weekly;
    public List<KeyValuePair<string, Bucket>> Extra = new();   // e.g. "Weekly · Opus" from a usage report
    public bool FromReport;

    public bool AtLimit => SignedIn && ((Session?.AtLimit ?? false) || (Weekly?.AtLimit ?? false));
    public Bucket LimitedBucket => Weekly is { AtLimit: true } ? Weekly : Session is { AtLimit: true } ? Session : null;
}

public class SessionInfo
{
    public ProfileEntry Profile;
    public string File, SessionId, CliSessionId, Title, Cwd, AccountUuid, OrgUuid;
    public DateTime LastActivity;
    public bool Archived;
}

public static class Accounts
{
    static readonly Regex EmailRx = new(@"[A-Za-z0-9._%+-]{2,}@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}");
    static readonly ConcurrentDictionary<string, string> emailCache = new();

    public static AccountInfo Read(ProfileEntry p)
    {
        var a = new AccountInfo();
        string data;
        try { data = p.Data; } catch (Exception) { return a; }

        JsonObject cfg = Json.ReadObject(Path.Combine(data, "config.json"));
        a.Uuid = Json.Str(cfg, "lastKnownAccountUuid");
        a.SignedIn = !string.IsNullOrEmpty(Json.Str(cfg, "oauth:tokenCacheV2")) || !string.IsNullOrEmpty(Json.Str(cfg, "oauth:tokenCache"));

        // Claude Code records the account it runs as. Only trust it for the
        // same account (two profiles can share one Claude Code store).
        JsonObject oa = Json.Obj(Json.ReadObject(Path.Combine(p.Config, ".claude.json")), "oauthAccount");
        string ou = Json.Str(oa, "accountUuid");
        if (oa != null && (a.Uuid == null || string.Equals(ou, a.Uuid, StringComparison.OrdinalIgnoreCase)))
        {
            a.Uuid ??= ou;
            a.Email = Json.Str(oa, "emailAddress");
            a.OrgUuid = Json.Str(oa, "organizationUuid");
            a.OrgName = Json.Str(oa, "organizationName");
            a.DisplayName = Json.Str(oa, "displayName");
        }
        if (a.Email == null && p.Email != null && a.Uuid != null && string.Equals(p.AccountUuid, a.Uuid, StringComparison.OrdinalIgnoreCase))
            a.Email = p.Email;
        if (a.Email == null && a.SignedIn) a.Email = LocalStorageEmail(data);

        ReadUsage(data, a);
        return a;
    }

    // Last resort: the signed-in email appears in the app's Local Storage.
    // Only used when exactly one distinct address is found.
    static string LocalStorageEmail(string data)
    {
        string dir = Path.Combine(data, "Local Storage", "leveldb");
        if (!Directory.Exists(dir)) return null;
        return emailCache.GetOrAdd(dir, d =>
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string f in Directory.GetFiles(d))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if ((ext != ".ldb" && ext != ".log") || new FileInfo(f).Length > 8 * 1024 * 1024) continue;
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    foreach (Match m in EmailRx.Matches(Encoding.ASCII.GetString(ms.ToArray()))) found.Add(m.Value);
                }
            }
            catch (Exception) { }
            return found.Count == 1 ? found.First() : null;
        });
    }

    static void ReadUsage(string data, AccountInfo a)
    {
        JsonArray samples = Json.Arr(Json.ReadObject(Path.Combine(data, "plan-usage-history.json")), "samples");
        if (samples == null) return;
        JsonObject best = null;
        for (int i = samples.Count - 1; i >= 0; i--)
        {
            if (samples[i] is not JsonObject s) continue;
            best ??= s;   // newest overall as a fallback
            if (a.OrgUuid == null || string.Equals(Json.Str(s, "org"), a.OrgUuid, StringComparison.OrdinalIgnoreCase)) { best = s; break; }
        }
        if (best == null) return;
        a.OrgUuid ??= Json.Str(best, "org");
        JsonObject u = Json.Obj(best, "u");
        if (u != null)
        {
            if (u.ContainsKey("fh")) a.FiveHour = (int)Json.Long(u, "fh");
            if (u.ContainsKey("sd")) a.SevenDay = (int)Json.Long(u, "sd");
        }
        a.UsageAt = Json.FromEpochMs(Json.Long(best, "t"));
    }

    // Org folder to file a session under: the account's org, else the most
    // recently used org folder it already has.
    public static string OrgFolder(ProfileEntry p, AccountInfo a)
    {
        if (a.Uuid == null) return null;
        if (a.OrgUuid != null) return a.OrgUuid;
        string acctDir = Path.Combine(p.Data, "claude-code-sessions", a.Uuid);
        if (!Directory.Exists(acctDir)) return null;
        return Directory.GetDirectories(acctDir).OrderByDescending(Directory.GetLastWriteTime).Select(Path.GetFileName).FirstOrDefault();
    }

    // Code-tab sessions stored in a profile. accountUuid = null for all accounts.
    public static List<SessionInfo> Sessions(ProfileEntry p, string accountUuid)
    {
        var list = new List<SessionInfo>();
        string root;
        try { root = Path.Combine(p.Data, "claude-code-sessions"); } catch (Exception) { return list; }
        if (!Directory.Exists(root)) return list;

        foreach (string acctDir in Directory.GetDirectories(root))
        {
            string acct = Path.GetFileName(acctDir);
            if (accountUuid != null && !string.Equals(acct, accountUuid, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string orgDir in Directory.GetDirectories(acctDir))
            {
                foreach (string f in Directory.GetFiles(orgDir, "local_*.json"))
                {
                    JsonObject d = Json.ReadObject(f);
                    if (d == null) continue;
                    long last = Json.Long(d, "lastActivityAt");
                    if (last == 0) last = Json.Long(d, "createdAt");
                    list.Add(new SessionInfo
                    {
                        Profile = p, File = f, AccountUuid = acct, OrgUuid = Path.GetFileName(orgDir),
                        SessionId = Json.Str(d, "sessionId") ?? Path.GetFileNameWithoutExtension(f),
                        CliSessionId = Json.Str(d, "cliSessionId"),
                        Title = Json.Str(d, "title") ?? "(untitled session)",
                        Cwd = Json.Str(d, "cwd"),
                        LastActivity = Json.FromEpochMs(last),
                        Archived = Json.Bool(d, "isArchived"),
                    });
                }
            }
        }
        list.Sort((x, y) => y.LastActivity.CompareTo(x.LastActivity));
        return list;
    }

    public static string FindTranscript(string configDir, string cliSessionId)
    {
        if (string.IsNullOrEmpty(cliSessionId)) return null;
        string projects = Path.Combine(configDir, "projects");
        if (!Directory.Exists(projects)) return null;
        foreach (string d in Directory.GetDirectories(projects))
        {
            string f = Path.Combine(d, cliSessionId + ".jsonl");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    public static string Ago(DateTime t)
    {
        if (t == DateTime.MinValue) return "";
        TimeSpan d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
        if (d.TotalHours < 24) return (int)d.TotalHours + " h ago";
        if (d.TotalDays < 30) return (int)d.TotalDays + " d ago";
        return t.ToString("d MMM yyyy");
    }
}
