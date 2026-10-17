// A plain-text health report: what's installed, where each profile's data
// really lives, what's open on which copy of Claude, and anything that looks
// wrong, each with its fix. Good for a bug report: emails are masked unless
// asked for, and sign-ins are never read (only whether one exists).

using System.Text;

namespace ClaudeSwitch.Core;

public static class Diagnose
{
    public class Report
    {
        public string Text;
        public List<string> Problems = new();
        public bool Healthy => Problems.Count == 0;
    }

    public static Report Run(bool showEmails)
    {
        var r = new Report();
        var t = new StringBuilder();
        string Mask(string email) => email == null ? "-" : showEmails ? email : MaskEmail(email);

        t.AppendLine("Claude Switch " + typeof(Diagnose).Assembly.GetName().Version?.ToString(3) + " on " + Platform.Name + " " +
                     Environment.OSVersion.Version + " (" + Platform.Arch + ")");
        t.AppendLine("Report made " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        t.AppendLine();

        List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
        UpdateStatus u = Updater.Scan(profiles);

        t.AppendLine("CLAUDE DESKTOP");
        if (u.Installs.Count == 0) { t.AppendLine("  not installed"); r.Problems.Add("Claude Desktop isn't installed. Get it from claude.ai/download."); }
        foreach (ClaudeInstall i in u.Installs)
            t.AppendLine("  " + (i.IsPrimary ? "* " : "  ") + i.Describe + "  " + Platform.Shorten(i.Path) +
                         (i.Updated == DateTime.MinValue ? "" : "  (changed " + i.Updated.ToString("yyyy-MM-dd") + ")") +
                         (i.UpdaterMissing ? "  [its updater is missing]" : ""));
        if (u.Installs.Count > 0) t.AppendLine("  (* = the copy Claude Switch opens)");
        foreach (ClaudeInstall old in u.OldCopies)
            r.Problems.Add("An old copy of Claude (" + old.Version + ") is still installed at " + Platform.Shorten(old.Path) + ". Fix: Remove the old copies, on the Claude version card.");
        t.AppendLine();

        t.AppendLine("PROFILES");
        var unify = Unify.Status(profiles);
        foreach (ProfileEntry p in profiles)
        {
            AccountInfo a = Accounts.Read(p);
            t.AppendLine("  " + p.Name + (p.IsDefault ? "  (Claude's own folder)" : ""));
            t.AppendLine("    account      " + (a.SignedIn ? Mask(a.Email) + (a.OrgName != null ? ", " + a.OrgName : "") : "not signed in"));
            t.AppendLine("    data         " + Platform.Shorten(p.LaunchDir));
            if (!Platform.SamePath(p.Data, p.LaunchDir))
                t.AppendLine("    really in    " + Platform.Shorten(p.Data) + "  (Windows redirects the Store build's writes here)");
            t.AppendLine("    claude code  " + Platform.Shorten(p.Config) + (Directory.Exists(p.Config) ? "" : "  (not created yet)"));
            string code = u.CodeVersions.TryGetValue(p.Name, out string v) ? v : null;
            t.AppendLine("    bundled code " + (code ?? "-") + (code != null && u.NewestCode != null && code != u.NewestCode ? "  (newest is " + u.NewestCode + ")" : ""));
            int pid = ClaudeApp.FindMain(p.LaunchDir);
            ClaudeInstall on = u.RunningOn.TryGetValue(p.Name, out ClaudeInstall ci) ? ci : null;
            t.AppendLine("    running      " + (pid == 0 ? "no" : "yes, pid " + pid + (on != null ? " on " + on.Describe : "")));
            t.AppendLine("    sessions     " + (a.Uuid == null ? "-" : Accounts.Sessions(p, a.Uuid).Count.ToString()));
            if (unify.Members.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                t.AppendLine("    shared mem   " + (unify.Linked.TryGetValue(p.Name, out bool ok) && ok ? "linked" : "NOT linked"));
            t.AppendLine("    launcher     " + (Shortcuts.HasLauncher(p.Name) ? "yes" : "missing"));

            if (!Directory.Exists(p.Data)) r.Problems.Add(p.Name + "'s data folder doesn't exist yet. Fix: open it once and sign in.");
            else if (!a.SignedIn) r.Problems.Add(p.Name + " isn't signed in. Fix: open it and sign in.");
            if (pid != 0 && on != null && !on.IsPrimary) r.Problems.Add(p.Name + " is open on an old copy of Claude (" + on.Version + "). Fix: Move to the current Claude.");
            if (!Shortcuts.HasLauncher(p.Name)) r.Problems.Add(p.Name + " has no launcher. Fix: Claude Switch recreates it when you rename or recolour the profile.");
            if (Transfer.AccountChanged(p, a)) r.Problems.Add(p.Name + " is signed in as a different account than before. Fix: see the banner on the dashboard.");
        }
        if (unify.On && !unify.Healthy) r.Problems.Add("Shared memory isn't linked for " + string.Join(", ", unify.Broken) + ". Fix: Repair sharing.");
        var dupes = profiles.GroupBy(p => p.Key).Where(g => g.Count() > 1).ToList();
        foreach (var g in dupes) r.Problems.Add("Profiles " + string.Join(" and ", g.Select(p => p.Name)) + " share one data folder, so they'd fight over one window.");
        t.AppendLine();

        t.AppendLine("CLAUDE CODE IN A TERMINAL");
        string cli = Terminal.FindCli();
        t.AppendLine("  cli          " + (cli != null ? Platform.Shorten(cli) : "not found on PATH"));
        t.AppendLine("  commands     " + (Terminal.Installed ? "on (" + string.Join(", ", profiles.Select(Terminal.CommandName)) + ")" : "off"));
        foreach (var rule in Settings.FolderRules()) t.AppendLine("  folder rule  " + Platform.Shorten(rule.Path) + " -> " + rule.Profile);
        t.AppendLine();

        t.AppendLine("LINKS AND EXTRAS");
        t.AppendLine("  claude://    " + (LinkRouter.Supported ? (LinkRouter.IsActive ? "routed by Claude Switch" : "handled by Claude") : "n/a"));
        RelayStatus relay = Relay.Read();
        t.AppendLine("  billing relay " + (relay == null ? "not installed" : relay.Fresh ? "running, accounts: " + string.Join(", ", relay.Accounts.Select(x => x.Name)) : "installed, not running"));
        t.AppendLine("  hotkeys      " + (Settings.Hotkeys ? "on" : "off"));
        t.AppendLine();

        t.AppendLine(r.Healthy ? "NO PROBLEMS FOUND" : "PROBLEMS (" + r.Problems.Count + ")");
        foreach (string p in r.Problems) t.AppendLine("  - " + p);
        r.Text = t.ToString();
        return r;
    }

    public static string MaskEmail(string email)
    {
        int at = email.IndexOf('@');
        if (at <= 0) return "***";
        if (at == 1) return "*" + email[at..];   // a one-letter name would show in full
        return email[0] + new string('*', Math.Max(1, at - 1)) + email[at..];
    }
}
