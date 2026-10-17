// The "Shared memory" card: turn unify mode on or off, see what's shared.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public static class UnifyPanel
{
    public static Control Build(Snapshot snap, Window owner, Action reload, Action<Action, int> later)
    {
        UnifyStatus u = snap.Unify;
        var s = new StackPanel();

        var head = new DockPanel();
        var badge = Neu.T(u.On ? (u.Healthy ? "On" : "Needs attention") : "Off", 12.5,
            u.On ? (u.Healthy ? Neu.P.Good : Neu.P.Warn) : Neu.P.Muted, FontWeight.SemiBold);
        badge.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        head.Children.Add(Neu.Heading("Shared memory", 15));
        s.Children.Add(head);

        string explain = u.On
            ? u.Healthy
                ? "These profiles share one memory and one set of conversations: " + Join(u.Members) +
                  ". When one account runs out of usage, open another and carry on from exactly where you left off."
                : "Sharing is set up but not active for " + Join(u.Broken) + ". Switch it on again to repair it."
            : "Let two accounts share one memory and one set of conversations. When one runs out of usage, the other carries on from where you left off. " +
              "Each account keeps its own sign-in; only memory and conversations are shared.";
        var p = Neu.Para(explain, 13);
        p.Margin = new Thickness(0, 6, 0, 12);
        s.Children.Add(p);

        if (u.On && u.Healthy)
        {
            var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22, Margin = new Thickness(0, 0, 0, 14) };
            void Stat(int n, string label)
            {
                var box = new StackPanel();
                box.Children.Add(Neu.T(n.ToString(), 20, Neu.P.Accent, FontWeight.SemiBold));
                box.Children.Add(Neu.T(label, 11.5, Neu.P.Muted));
                stats.Children.Add(box);
            }
            Stat(u.Projects, u.Projects == 1 ? "project" : "projects");
            Stat(u.MemoryNotes, u.MemoryNotes == 1 ? "memory note" : "memory notes");
            Stat(u.Transcripts, u.Transcripts == 1 ? "conversation" : "conversations");
            s.Children.Add(Neu.Inset(stats, 14, new Thickness(16, 12)));
        }

        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        void Add(Control c) { c.Margin = new Thickness(0, 0, 12, 8); row.Children.Add(c); }

        if (!u.On || !u.Healthy)
        {
            var turnOn = new NeuButton(u.On ? "Repair sharing" : "Turn on sharing…", primary: true);
            turnOn.Click += () => _ = TurnOn(snap, owner, reload, later);
            Add(turnOn);
        }
        else
        {
            var turnOff = new NeuButton("Turn off");
            turnOff.Click += () => _ = TurnOff(snap, owner, reload, later);
            Add(turnOff);
            var change = new NeuButton("Change profiles…");
            change.Click += () => _ = TurnOn(snap, owner, reload, later);
            Add(change);
        }
        s.Children.Add(row);

        if (u.On && u.Healthy)
        {
            var note = Neu.Para("Shared folder: " + Platform.Shorten(u.SharedDir) + ". Turning sharing off gives each profile its own copy back; nothing is deleted.", 11.5);
            note.Margin = new Thickness(0, 4, 0, 0);
            s.Children.Add(note);
        }

        Border card = Neu.Tile(s);
        return card;
    }

    static string Join(List<string> names) => names.Count switch
    {
        0 => "no profiles",
        1 => names[0],
        2 => names[0] + " and " + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    // Pick which profiles share, then link them.
    static async Task TurnOn(Snapshot snap, Window owner, Action reload, Action<Action, int> later)
    {
        if (snap.Profiles.Count < 2)
        {
            await Dialogs.Info(owner, "Two profiles needed", "Add a second profile first, then you can let them share one memory.");
            return;
        }

        Window w = Dialogs.Frame("Share memory between accounts", 580);
        var picked = new List<(ProfileEntry, CheckBox)>();
        var body = new StackPanel();
        body.Children.Add(Neu.Para(
            "Choose the accounts that should work as one. They'll share every project's memory, instructions and conversations, " +
            "so when one runs out of usage the other carries on from exactly where you left off."));
        body.Children.Add(Neu.Section("Profiles"));
        foreach (ProfileEntry p in snap.Profiles)
        {
            AccountInfo a = snap.Accounts[p.Name];
            bool on = snap.Unify.Members.Contains(p.Name, StringComparer.OrdinalIgnoreCase) || snap.Unify.Members.Count == 0;
            string detail = (a.SignedIn ? a.Email ?? "signed in" : "not signed in") +
                            (string.IsNullOrEmpty(p.ConfigDir) ? "   ·   uses Claude Code's main memory" : "");
            Control row = ExportWindow.Check(p.Name, detail, on);
            picked.Add((p, (CheckBox)((Panel)row).Children[0]));
            body.Children.Add(row);
        }

        var warn = Neu.Para("", 12.5, Neu.P.Warn);
        warn.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(warn);
        var note = Neu.Para(
            "Sign-ins are never shared. Each profile's current memory is merged into the shared one, and a copy of what it had is kept. " +
            "You can turn this off at any time and each profile gets its own copy back.", 12.5);
        note.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(note);
        var status = Neu.T("", 13, Neu.P.Bad, FontWeight.SemiBold);
        status.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(status);

        var go = new NeuButton("Share memory", primary: true);
        var cancel = new NeuButton("Cancel");
        cancel.Click += w.Close;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(go);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        void Recheck()
        {
            var chosen = picked.Where(x => x.Item2.IsChecked == true).Select(x => x.Item1).ToList();
            List<string> open = Unify.Running(chosen);
            warn.Text = open.Count > 0
                ? "Close the Claude window of " + Join(open) + " before sharing: Claude keeps those files open."
                : "";
        }
        foreach (var (_, box) in picked) box.IsCheckedChanged += (_, _) => Recheck();
        Recheck();

        go.Click += async () =>
        {
            var chosen = picked.Where(x => x.Item2.IsChecked == true).Select(x => x.Item1).ToList();
            if (chosen.Count < 2) { status.Text = "Pick at least two profiles."; return; }
            List<string> open = Unify.Running(chosen);
            if (open.Count > 0) { status.Text = "Close " + Join(open) + " first."; return; }

            go.IsEnabled = cancel.IsEnabled = false;
            status.Foreground = Neu.B(Neu.P.Accent);
            status.Text = "Sharing…";
            UnifyResult r = null;
            await Task.Run(() => r = Unify.Link(chosen));
            go.IsEnabled = cancel.IsEnabled = true;
            status.Foreground = Neu.B(Neu.P.Bad);
            status.Text = "";
            if (!r.Ok)
            {
                await Dialogs.Info(w, "Couldn't share memory", string.Join("\n", r.Errors));
                return;
            }
            w.Close();
            string msg = Join(chosen.Select(c => c.Name).ToList()) + " now share one memory and one set of conversations.";
            if (r.Merged > 0) msg += "\n\n" + r.Merged + " existing files were merged into the shared folder.";
            if (r.Notes.Count > 0) msg += "\n\n" + string.Join("\n", r.Notes);
            msg += "\n\nOpen a profile and its Code tab lists the shared conversations.";
            await Dialogs.Info(owner, "Sharing is on", msg);
            later(() => Unify.Mirror(Profiles.LoadOrEmpty(), snap.Accounts), 300);
        };

        Dialogs.SetContent(w, new ScrollViewer { Content = Dialogs.Body("Share memory between accounts", body), MaxHeight = 720 });
        if (owner != null) await w.ShowDialog(owner); else w.Show();
        reload();
    }

    static async Task TurnOff(Snapshot snap, Window owner, Action reload, Action<Action, int> later)
    {
        List<ProfileEntry> members = snap.Unify.Members.Select(n => Profiles.Find(snap.Profiles, n)).Where(p => p != null).ToList();
        List<string> open = Unify.Running(members);
        if (open.Count > 0)
        {
            await Dialogs.Info(owner, "Close those windows first", "Turning sharing off moves files, so close the Claude window of " + Join(open) + " first.");
            return;
        }
        if (await Dialogs.Ask(owner, "Turn off shared memory?",
                "Each of " + Join(snap.Unify.Members) + " gets its own copy of the shared memory and conversations. Nothing is deleted, and you can share again later.",
                "Turn off", "Cancel") != 0) return;
        UnifyResult r = null;
        await Task.Run(() => r = Unify.UnlinkAll(snap.Profiles));
        await Dialogs.Info(owner, r.Ok ? "Sharing is off" : "Partly done",
            (r.Notes.Count > 0 ? string.Join("\n", r.Notes) : "Each profile has its own memory again.") +
            (r.Errors.Count > 0 ? "\n\nProblems:\n" + string.Join("\n", r.Errors) : ""));
        reload();
    }
}
