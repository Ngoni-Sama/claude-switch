// Interop with claude-account-switcher (github.com/SmelterLabs/claude-account-switcher),
// a separate tool that bills a Code session to another of your subscriptions
// through a local relay.
//
// Claude Switch doesn't run, install or configure it, and never reads its
// token store. It only reads the relay's heartbeat file, relay.alive, which by
// that project's design holds no tokens: the account names, when each token
// expires, and each account's usage as the API last reported it. That usage is
// live (from the API's own rate-limit headers), so it's shown when present.

using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

public class RelayAccount
{
    public string Name;
    public DateTime Expires = DateTime.MinValue;
    public int? SessionPct, WeeklyPct;          // 0..100
    public DateTime SessionReset = DateTime.MinValue, WeeklyReset = DateTime.MinValue, UsageAt = DateTime.MinValue;
}

public class RelayStatus
{
    public DateTime At;
    public int Port;
    public List<RelayAccount> Accounts = new();
    // The relay rewrites its heartbeat every 10 seconds.
    public bool Fresh => (DateTime.Now - At).TotalSeconds < 30;
}

public static class Relay
{
    public static string Folder => Path.Combine(Platform.Home, ".claude", "account-switcher");
    static string Heartbeat => Path.Combine(Folder, "relay.alive");

    /// <summary>Null when the relay isn't installed.</summary>
    public static RelayStatus Read() => Read(Heartbeat);

    internal static RelayStatus Read(string heartbeat)
    {
        if (!File.Exists(heartbeat)) return null;
        JsonObject o = Json.ReadObject(heartbeat);
        if (o == null) return new RelayStatus { At = File.GetLastWriteTime(heartbeat) };

        var s = new RelayStatus { At = Seconds(o, "at"), Port = (int)Json.Long(o, "port") };
        JsonObject expires = Json.Obj(o, "expires"), usage = Json.Obj(o, "usage");
        foreach (JsonValue n in (Json.Arr(o, "accounts") ?? new JsonArray()).OfType<JsonValue>())
        {
            string name = n.ToString();
            var a = new RelayAccount { Name = name };
            if (Json.Str(expires, name) is string iso && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime e))
                a.Expires = e.ToLocalTime();
            if (Json.Obj(usage, name) is JsonObject u)
            {
                a.SessionPct = Pct(u, "h5");
                a.WeeklyPct = Pct(u, "d7");
                a.SessionReset = Seconds(u, "h5_reset");
                a.WeeklyReset = Seconds(u, "d7_reset");
                a.UsageAt = Seconds(u, "at");
            }
            s.Accounts.Add(a);
        }
        return s;
    }

    static DateTime Seconds(JsonObject o, string key)
    {
        if (o == null || !o.TryGetPropertyValue(key, out JsonNode n) || n is not JsonValue v) return DateTime.MinValue;
        double sec = v.TryGetValue(out double d) ? d : v.TryGetValue(out long l) ? l : 0;
        return sec <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds((long)(sec * 1000)).LocalDateTime;
    }

    // The API reports utilization as a fraction; tolerate a percentage too.
    static int? Pct(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out JsonNode n) || n is not JsonValue v || !v.TryGetValue(out double d)) return null;
        return (int)Math.Round(d <= 1.0 ? d * 100 : d);
    }
}
