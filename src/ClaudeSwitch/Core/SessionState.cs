// What was open last time, so starting the computer can offer to put it back.
//
// Every refresh records which profiles have a Claude window open. On the next
// start Claude Switch can reopen exactly those, or start clean, or ask.

using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

public enum StartupMode { Ask, Restore, Fresh }

public class LastSession
{
    public List<string> Profiles = new();
    public DateTime At = DateTime.MinValue;

    public bool Any => Profiles.Count > 0;

    public string Describe => Profiles.Count switch
    {
        0 => "nothing",
        1 => Profiles[0],
        2 => Profiles[0] + " and " + Profiles[1],
        _ => string.Join(", ", Profiles.Take(Profiles.Count - 1)) + " and " + Profiles[^1],
    };
}

public static class SessionState
{
    public static StartupMode Mode
    {
        get => Settings.Startup switch { "restore" => StartupMode.Restore, "fresh" => StartupMode.Fresh, _ => StartupMode.Ask };
        set => Settings.Startup = value switch { StartupMode.Restore => "restore", StartupMode.Fresh => "fresh", _ => "ask" };
    }

    public static LastSession Last()
    {
        var s = new LastSession { At = Json.FromEpochMs(Json.Long(Settings.LastSession(), "at")) };
        foreach (JsonNode n in Json.Arr(Settings.LastSession(), "profiles") ?? new JsonArray())
            if (n is JsonValue v) s.Profiles.Add(v.ToString());
        return s;
    }

    // Called on every refresh. Only remembers a non-empty set, so quitting
    // Claude Switch while everything is closed doesn't erase what to restore.
    public static void Remember(Snapshot snap)
    {
        var open = snap.Profiles.Where(p => snap.Pid(p) != 0).Select(p => p.Name).ToList();
        if (open.Count == 0) return;
        LastSession last = Last();
        if (last.Profiles.Count == open.Count && !open.Except(last.Profiles, StringComparer.OrdinalIgnoreCase).Any()) return;
        Settings.SetLastSession(open);
    }

    // What to do at startup, given what's already running.
    public static LastSession ToRestore(Snapshot snap)
    {
        if (Mode == StartupMode.Fresh) return new LastSession();
        LastSession last = Last();
        // Anything already open doesn't need restoring.
        last.Profiles = last.Profiles
            .Where(n => Profiles.Find(snap.Profiles, n) is ProfileEntry p && snap.Pid(p) == 0)
            .ToList();
        return last;
    }

    public static void Restore(LastSession last, List<ProfileEntry> profiles)
    {
        foreach (string name in last.Profiles)
        {
            ProfileEntry p = Profiles.Find(profiles, name);
            if (p == null) continue;
            Launcher.Spawn(p);
            Thread.Sleep(1200);   // let each window claim its lock before the next
        }
    }
}
