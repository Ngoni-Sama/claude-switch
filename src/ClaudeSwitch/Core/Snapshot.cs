// Everything the UI shows, read off the UI thread (process list + file reads).

using System.Text;

namespace ClaudeSwitch.Core;

public class Snapshot
{
    public List<ProfileEntry> Profiles;
    public Dictionary<string, AccountInfo> Accounts = new();
    public Dictionary<string, int> Running;
    public Dictionary<string, List<SessionInfo>> Sessions = new();
    public List<RecoveryItem> Recovery = new();
    public UnifyStatus Unify = new();
    public UpdateStatus Updates = new();
    public RelayStatus Relay;                                  // null unless claude-account-switcher is installed
    public List<ProfileOps.Found> Unlisted = new();           // Claude-* folders no profile uses yet
    public bool LinkActive, LinkRegistered, ClaudeInstalled;
    public string Signature;

    // limitsReady: called once if the first (slow) transcript scan had to start
    // in the background, so the caller can load again with it.
    public static Snapshot Load(bool withSessions, Action limitsReady = null)
    {
        if (Demo.On) return Demo.Snapshot();
        var s = new Snapshot { Profiles = Core.Profiles.LoadOrEmpty() };
        foreach (ProfileEntry p in s.Profiles) s.Accounts[p.Name] = Core.Accounts.Read(p);
        if (Transfer.Remember(s.Profiles, s.Accounts))
        {
            try { Core.Profiles.Save(s.Profiles); } catch (Exception ex) { ErrorLog.Write(ex); }
        }
        s.Running = ClaudeApp.FindRunning();
        s.ClaudeInstalled = ClaudeApp.ResolveExe() != null;

        Dictionary<string, List<LimitEvent>> events = Limits.CollectIfReady(s.Profiles, limitsReady);
        foreach (ProfileEntry p in s.Profiles)
        {
            AccountInfo a = s.Accounts[p.Name];
            Limits.Apply(a, a.Uuid != null && events.TryGetValue(a.Uuid, out var mine) ? mine : null);
        }

        var sig = new StringBuilder();
        foreach (ProfileEntry p in s.Profiles)
        {
            AccountInfo a = s.Accounts[p.Name];
            List<SessionInfo> list = withSessions && a.Uuid != null ? Core.Accounts.Sessions(p, a.Uuid) : new List<SessionInfo>();
            s.Sessions[p.Name] = list;
            sig.Append(p.Name).Append('|').Append(p.DataDir).Append('|').Append(p.AccountUuid).Append('|').Append(a.Uuid).Append('|')
               .Append(a.Email).Append('|').Append(a.SignedIn).Append('|').Append(a.Session?.Shown).Append(a.Session?.ResetText).Append('|')
               .Append(a.Weekly?.Shown).Append(a.Weekly?.ResetText).Append('|').Append(a.Extra.Count).Append(a.FromReport).Append(a.UsageAt.Ticks)
               .Append('|').Append(s.Pid(p) != 0).Append('|').Append(list.Count).Append('|').Append(list.Count > 0 ? list[0].LastActivity.Ticks : 0).Append(';');
        }
        if (withSessions) s.Recovery = Transfer.FindRecoverable(s.Profiles, s.Accounts);
        s.Unify = ClaudeSwitch.Core.Unify.Status(s.Profiles);
        s.Updates = Updater.Scan(s.Profiles);
        try { s.Relay = ClaudeSwitch.Core.Relay.Read(); } catch (Exception ex) { ErrorLog.Write(ex); }
        try { s.Unlisted = ProfileOps.FindUnlisted(s.Profiles); } catch (Exception ex) { ErrorLog.Write(ex); }
        s.LinkActive = LinkRouter.IsActive;
        s.LinkRegistered = LinkRouter.IsRegistered;
        sig.Append(s.Recovery.Count).Append(s.LinkActive).Append(s.LinkRegistered).Append(s.ClaudeInstalled)
           .Append(string.Join(",", s.Unify.Members)).Append(s.Unify.Healthy).Append(s.Unify.Transcripts).Append(s.Unify.MemoryNotes)
           .Append(string.Join(",", s.Updates.Installs.Select(i => i.Kind + i.Version)))
           .Append(string.Join(",", s.Updates.ProfilesBehind)).Append(Updater.Mode)
           .Append(s.Relay?.Fresh).Append(string.Join(",", s.Relay?.Accounts.Select(a => a.Name + a.SessionPct + a.WeeklyPct) ?? Array.Empty<string>()))
           .Append(string.Join(",", s.Unlisted.Select(u => u.DataDir)))
           .Append(string.Join(",", s.Profiles.Select(p => p.Name + p.Color + p.OpenAtStart)));
        s.Signature = sig.ToString();
        SessionState.Remember(s);
        return s;
    }

    public int Pid(ProfileEntry p)
    {
        try { return Running.TryGetValue(p.Key, out int pid) ? pid : 0; }
        catch (Exception) { return 0; }
    }

    public ProfileEntry DefaultProfile => Profiles.FirstOrDefault(p => p.IsDefault);
}
