// The "Claude version" card: what's installed, what's behind, and the buttons
// to put it right.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public static class UpdatePanel
{
    public static Control Build(Snapshot snap, Window owner, Action reload, Action<Action, int> later)
    {
        UpdateStatus u = snap.Updates;
        var s = new StackPanel();

        var head = new DockPanel();
        string state = u.Installs.Count == 0 ? "Not installed" : u.AnythingBehind ? "Something's behind" : "Up to date";
        var badge = Neu.T(state, 12.5, u.Installs.Count == 0 ? Neu.P.Muted : u.AnythingBehind ? Neu.P.Warn : Neu.P.Good, FontWeight.SemiBold);
        badge.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        head.Children.Add(Neu.Heading("Claude version", 15));
        s.Children.Add(head);

        // What's installed.
        var lines = new StackPanel { Spacing = 4, Margin = new Thickness(0, 10, 0, 0) };
        if (u.Installs.Count == 0)
            lines.Children.Add(Neu.Para("Claude Desktop isn't installed yet.", 13));
        List<ClaudeInstall> old = u.OldCopies;
        foreach (ClaudeInstall i in u.Installs)
        {
            bool stale = old.Contains(i);
            var row = new DockPanel();
            var tag = Neu.T(i.IsPrimary ? "in use" : stale ? "out of date" : "also installed", 11.5,
                            stale ? Neu.P.Warn : i.IsPrimary ? Neu.P.Good : Neu.P.Muted, FontWeight.SemiBold);
            tag.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tag, Dock.Right);
            row.Children.Add(tag);
            var text = Neu.T(i.Describe, 13, stale ? Neu.P.Warn : Neu.P.Text);
            ToolTip.SetTip(text, i.Path + (i.Updated == DateTime.MinValue ? "" : "\nUpdated " + i.Updated.ToString("d MMM yyyy")) +
                                 (i.UpdaterMissing ? "\nIts updater (Update.exe) is missing, so it can't update or uninstall itself." : ""));
            row.Children.Add(text);
            lines.Children.Add(row);
        }
        s.Children.Add(Neu.Inset(lines, 14, new Thickness(16, 12)));

        // The duplicate-install trap, spelled out.
        if (old.Count > 0)
        {
            string which = string.Join(", ", old.Select(o => o.Version + " (" + (o.Updated == DateTime.MinValue ? "old" : o.Updated.ToString("MMM yyyy")) + ")"));
            var warn = Neu.Para((old.Count == 1 ? "An old copy of Claude is" : old.Count + " old copies of Claude are") + " still installed: " + which +
                                ". Each has its own updater, and they've stopped, so opening one gives you an old Claude. Removing them leaves one app " +
                                "that stays current. Your chats, sign-in and settings live in your profile folder and aren't touched.", 12.5, Neu.P.Warn);
            warn.Margin = new Thickness(0, 12, 0, 0);
            s.Children.Add(warn);
        }

        // A window open on the old copy. This is the one people actually hit.
        if (u.OnOldApp.Count > 0)
        {
            var stuck = Neu.Para(Join(u.OnOldApp) + (u.OnOldApp.Count == 1 ? " is" : " are") + " open on the old Claude right now. " +
                "There's nothing to download: both copies use the same profile folder, so reopening " +
                (u.OnOldApp.Count == 1 ? "it" : "them") + " on the current Claude brings every chat and the sign-in across.", 12.5, Neu.P.Warn);
            stuck.Margin = new Thickness(0, 12, 0, 0);
            s.Children.Add(stuck);
        }

        // Profiles whose bundled Claude Code is behind.
        if (u.ProfilesBehind.Count > 0)
        {
            var behind = Neu.Para("Claude Code is older in " + Join(u.ProfilesBehind) + " (" +
                                  string.Join(", ", u.ProfilesBehind.Select(n => n + " " + (u.CodeVersions[n] ?? "?"))) +
                                  " versus " + u.NewestCode + "). A profile only updates while it's open.", 12.5);
            behind.Margin = new Thickness(0, 12, 0, 0);
            s.Children.Add(behind);
        }

        var row2 = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        void Add(Control c) { c.Margin = new Thickness(0, 0, 12, 8); row2.Children.Add(c); }

        // Moving a stuck window onto the current app beats sending someone to
        // the Store, so it leads when it applies.
        if (u.OnOldApp.Count > 0)
        {
            var one = new NeuButton("Move " + Join(u.OnOldApp) + " to the current Claude", primary: true);
            ToolTip.SetTip(one, "Closes that Claude window and opens the profile again on the current app. Same folder, same chats, same sign-in.");
            one.Click += () => _ = Reopen(u.OnOldApp, snap, owner, later);
            Add(one);
        }
        if (snap.Profiles.Count > 1)
        {
            var all = new NeuButton("Restart all profiles", primary: u.OnOldApp.Count == 0 && u.AnythingBehind);
            ToolTip.SetTip(all, "Closes and reopens every profile so they all run the current app and refresh their Claude Code.");
            all.Click += () => _ = Reopen(snap.Profiles.Select(p => p.Name).ToList(), snap, owner, later);
            Add(all);
        }

        var update = new NeuButton("Check for a newer Claude", primary: u.AnythingBehind && u.OnOldApp.Count == 0 && snap.Profiles.Count <= 1);
        ToolTip.SetTip(update, Updater.UpdateHow);
        update.Click += () => _ = UpdateClaude(owner);
        Add(update);

        if (old.Count > 0 && Platform.IsWindows)
        {
            var remove = new NeuButton(old.Count == 1 ? "Remove the old copy" : "Remove " + old.Count + " old copies", primary: u.OnOldApp.Count == 0);
            remove.Click += () => _ = RemoveOld(old, owner, reload);
            Add(remove);
        }
        if (u.ProfilesBehind.Count > 0)
        {
            var refresh = new NeuButton("Open " + Join(u.ProfilesBehind) + " to update");
            refresh.Click += () => later(() =>
            {
                foreach (string n in u.ProfilesBehind)
                    if (Profiles.Find(snap.Profiles, n) is ProfileEntry p) { Updater.RefreshProfile(p); Thread.Sleep(2000); }
            }, 8000);
            Add(refresh);
        }
        s.Children.Add(row2);

        // How eager to be about all this.
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        foreach ((UpdateMode mode, string label, string tip) in new[]
        {
            (UpdateMode.Notify, "Tell me", "Check daily and show a banner when Claude or a profile falls behind. Nothing is opened or installed without you."),
            (UpdateMode.Automatic, "Automatic", "Also open any profile that's behind so Claude's own updater runs in its window. The app itself still updates through your system."),
            (UpdateMode.Off, "Off", "Only show versions here; never check or prompt."),
        })
        {
            UpdateMode m = mode;
            var b = new NeuButton(label, false, 12.5) { Selected = Updater.Mode == m };
            ToolTip.SetTip(b, tip);
            b.Click += () => { Updater.Mode = m; reload(); };
            modes.Children.Add(b);
        }
        s.Children.Add(modes);

        var note = Neu.Para(Updater.UpdateHow + " Claude Switch never installs an app update by itself.", 11.5);
        note.Margin = new Thickness(0, 10, 0, 0);
        s.Children.Add(note);

        Border card = Neu.Tile(s);
        return card;
    }

    static string Join(List<string> names) => names.Count switch
    {
        0 => "no profile",
        1 => names[0],
        2 => names[0] + " and " + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    // Close those profiles' windows and open them again on the current app.
    static async Task Reopen(List<string> names, Snapshot snap, Window owner, Action<Action, int> later)
    {
        var profiles = names.Select(n => Profiles.Find(snap.Profiles, n)).Where(p => p != null).ToList();
        if (profiles.Count == 0) return;
        bool anyOpen = profiles.Any(p => snap.Pid(p) != 0);
        if (await Dialogs.Ask(owner, "Reopen " + Join(names) + " on the current Claude?",
                (anyOpen ? "Each window closes and opens again straight away. Anything running in " +
                           (profiles.Count == 1 ? "it" : "them") + " stops, so finish what you're mid-way through first.\n\n" : "") +
                "Nothing is downloaded and nothing is lost: the old and current Claude read the same profile folder, so your chats, " +
                "sign-in and settings come across untouched. Claude Switch always opens the newest copy installed.",
                "Reopen " + (profiles.Count == 1 ? "it" : "them"), "Cancel") != 0) return;
        later(() => Updater.ReopenAll(profiles), 4000 + profiles.Count * 2500);
    }

    static async Task UpdateClaude(Window owner)
    {
        if (Platform.IsLinux)
        {
            try { await (owner?.Clipboard?.SetTextAsync(Updater.LinuxCommand) ?? Task.CompletedTask); } catch (Exception) { }
            await Dialogs.Info(owner, "Update Claude",
                "Claude updates with your system packages. This command is on your clipboard:\n\n" + Updater.LinuxCommand +
                "\n\nRun it in a terminal; it needs your password.");
            return;
        }
        Updater.OpenUpdater();
        await Dialogs.Info(owner, "Update Claude", Updater.UpdateHow +
            "\n\nOnce it finishes, come back and press Refresh to see the new version.");
    }

    // Dry run first, so the confirmation lists exactly what will go; then the
    // real thing, and a report of what actually went.
    static async Task RemoveOld(List<ClaudeInstall> copies, Window owner, Action reload)
    {
        if (!OperatingSystem.IsWindows()) return;
        var previews = new List<(ClaudeInstall copy, RemovalResult plan)>();
        await Task.Run(() => { foreach (ClaudeInstall c in copies) previews.Add((c, Updater.RemoveOldCopy(c, dryRun: true))); });

        var refused = previews.Where(p => !p.plan.Ok).ToList();
        if (refused.Count == previews.Count)
        {
            await Dialogs.Info(owner, "Can't remove it", string.Join("\n\n", refused.Select(p => p.plan.Problem).Distinct()));
            return;
        }
        var go = previews.Where(p => p.plan.Ok).ToList();
        long total = go.Sum(p => p.plan.Bytes);
        string body = string.Join("\n\n", go.Select(p =>
            "Claude " + p.copy.Version + "  (" + Platform.Shorten(p.copy.Path) + ")\n" +
            string.Join("\n", p.plan.Removed.Select(x => "   - " + x))));
        string sizeText = new RemovalResult { Bytes = total }.Size;

        if (await Dialogs.Ask(owner, go.Count == 1 ? "Remove the old Claude?" : "Remove " + go.Count + " old copies of Claude?",
                "This removes:\n\n" + body + "\n\nFrees about " + sizeText + ". The Claude you're using stays, and so do your chats, " +
                "sign-in and settings: they're in your profile folder, which the current Claude uses too.",
                "Remove", "Cancel") != 0) return;

        var done = new List<(ClaudeInstall copy, RemovalResult result)>();
        await Task.Run(() => { foreach (var p in go) done.Add((p.copy, Updater.RemoveOldCopy(p.copy, dryRun: false))); });

        long freed = done.Sum(d => d.result.Bytes);
        var problems = done.Where(d => !d.result.Ok).Select(d => "Claude " + d.copy.Version + ": " + d.result.Problem).ToList();
        var skipped = done.SelectMany(d => d.result.Skipped).ToList();
        string report = "Freed " + new RemovalResult { Bytes = freed }.Size + ".";
        if (problems.Count > 0) report += "\n\n" + string.Join("\n", problems);
        if (skipped.Count > 0) report += "\n\nLeft in place:\n" + string.Join("\n", skipped.Select(s => "   - " + s));
        if (problems.Count == 0)
            report += "\n\nOne Claude, one updater. Open your profiles from Claude Switch or their “Claude (name)” shortcuts.";
        await Dialogs.Info(owner, problems.Count == 0 ? "Old Claude removed" : "Partly removed", report);
        reload();
    }
}
