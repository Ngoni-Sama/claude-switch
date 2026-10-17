// Saving a copied usage report to the right profile: the Claude window it was
// copied from (Windows can tell), cross-checked with the weekly reset time;
// asks when they disagree or nothing identifies it.

using Avalonia.Controls;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public static class Reports
{
    public static async Task<ProfileEntry> Assign(Window owner, UsageReportData r, ProfileEntry hint, Snapshot snap)
    {
        ProfileEntry byWeekly = UsageReport.ByWeeklyReset(r, snap.Profiles, snap.Accounts);
        ProfileEntry target;
        if (hint != null && (byWeekly == null || byWeekly == hint)) target = hint;
        else if (hint == null && byWeekly != null) target = byWeekly;
        else if (hint != null)
        {
            int c = await Dialogs.Ask(owner, "This looks like " + byWeekly.Name + "'s report",
                "Its weekly reset matches " + byWeekly.Name + ", not " + hint.Name + ". Which profile is it for?", byWeekly.Name, hint.Name, "Cancel");
            target = c == 0 ? byWeekly : c == 1 ? hint : null;
        }
        else
            target = await Dialogs.Pick(owner, "Whose usage report is this?",
                "You copied a Claude usage report. Pick the profile it came from so its card shows these numbers.",
                snap.Profiles, snap.Accounts, snap.Running);

        if (target == null) return null;
        AccountInfo a = snap.Accounts[target.Name];
        if (a.Uuid == null) { await Dialogs.Info(owner, "Not signed in", target.Name + " isn't signed in yet."); return null; }
        UsageReport.Save(a.Uuid, r);
        return target;
    }
}
