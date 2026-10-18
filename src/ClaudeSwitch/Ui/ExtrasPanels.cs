// Dashboard cards for the terminal, settings (shortcuts, updates, Diagnose)
// and the optional billing relay.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public static class ExtrasPanels
{
    static Border Card(StackPanel s)
    {
        Border c = Neu.Tile(s);
        return c;
    }

    static StackPanel Head(string title, string badge = null, Color? badgeColor = null)
    {
        var s = new StackPanel();
        var head = new DockPanel();
        if (badge != null)
        {
            var b = Neu.T(badge, 12.5, badgeColor ?? Neu.P.Muted, FontWeight.SemiBold);
            b.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(b, Dock.Right);
            head.Children.Add(b);
        }
        head.Children.Add(Neu.Heading(title, 15));
        s.Children.Add(head);
        return s;
    }

    static Control Para(string text, double top = 6, double bottom = 12, double size = 13)
    {
        var p = Neu.Para(text, size);
        p.Margin = new Thickness(0, top, 0, bottom);
        return p;
    }

    static WrapPanel Row(params Control[] items)
    {
        var r = new WrapPanel();
        foreach (Control c in items) { c.Margin = new Thickness(0, 0, 12, 8); r.Children.Add(c); }
        return r;
    }

    static NeuButton Btn(string text, bool primary, Action click, double size = 13.5)
    {
        var b = new NeuButton(text, primary, size);
        b.Click += click;
        return b;
    }

    // ---- Claude Code in a terminal ----------------------------------------------------

    public static Control Terminal_(Snapshot snap, Window owner, Action reload)
    {
        string cli = Terminal.FindCli();
        bool on = Terminal.Installed;
        StackPanel s = Head("Claude Code in a terminal", cli == null ? "Not installed" : on ? "Commands on" : "Commands off",
                            cli == null ? Neu.P.Warn : on ? Neu.P.Good : Neu.P.Muted);
        s.Children.Add(Para(cli == null
            ? "Each profile can run Claude Code in a terminal with its own sign-in and memory, once Claude Code is installed."
            : on
                ? "Type " + string.Join(", ", snap.Profiles.Take(3).Select(Terminal.CommandName)) + (snap.Profiles.Count > 3 ? "…" : "") +
                  " in any new terminal. In a folder with a rule below, plain claude picks the profile by itself."
                : "Run Claude Code as any profile from a terminal: turn on commands like " +
                  string.Join(" and ", snap.Profiles.Take(2).Select(Terminal.CommandName)) + ". Or use More > Open Claude Code on a profile."));

        var rules = Settings.FolderRules();
        if (rules.Count > 0)
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var r in rules)
            {
                var row = new DockPanel();
                string path = r.Path;
                var x = Btn("Remove", false, () => { Terminal.RemoveRule(path); reload(); }, 11.5);
                DockPanel.SetDock(x, Dock.Right);
                row.Children.Add(x);
                var t = Neu.T(Platform.Shorten(r.Path) + "  ->  " + r.Profile, 12.5);
                t.VerticalAlignment = VerticalAlignment.Center;
                ToolTip.SetTip(t, r.Path);
                row.Children.Add(t);
                list.Children.Add(row);
            }
            s.Children.Add(Neu.Inset(list, 14, new Thickness(14, 10)));
        }

        s.Children.Add(Row(
            on ? Btn("Turn commands off", false, () => _ = Toggle(owner, false, reload))
               : Btn("Turn on terminal commands", cli != null, () => _ = Toggle(owner, true, reload)),
            Btn("Add a folder rule…", false, () => _ = AddRule(owner, snap, reload)),
            cli == null ? Btn("How to install", false, () => Links.Open(Terminal.InstallDocs)) : new Panel()));
        if (rules.Count > 0 && !on)
            s.Children.Add(Para("Folder rules take effect once terminal commands are on.", 0, 4, 11.5));
        return Card(s);
    }

    static async Task Toggle(Window owner, bool on, Action reload)
    {
        if (on)
        {
            string where = Platform.IsWindows
                ? "your PowerShell profiles (Windows PowerShell and PowerShell 7) and your user PATH"
                : "~/.bashrc and ~/.zshrc";
            if (await Dialogs.Ask(owner, "Turn on terminal commands?",
                    "This adds one marked block to " + where + ", backing each file up first, so a claude-<profile> command exists for every profile. " +
                    "Turning it off removes exactly that block. Open a new terminal afterwards.", "Turn on", "Cancel") != 0) return;
        }
        List<string> changed = null;
        string problem = null;
        await Task.Run(() =>
        {
            try { changed = on ? Terminal.Install() : Terminal.Uninstall(); }
            catch (Exception ex) { ErrorLog.Write(ex); problem = ex.Message; }
        });
        if (problem != null) await Dialogs.Info(owner, "Couldn't change the terminal setup", problem);
        else await Dialogs.Info(owner, on ? "Terminal commands are on" : "Terminal commands are off",
            (changed.Count > 0 ? "Changed:\n" + string.Join("\n", changed.Select(c => "   - " + c)) : "Nothing needed changing.") +
            (on ? "\n\nOpen a new terminal and try them." : ""));
        reload();
    }

    static async Task AddRule(Window owner, Snapshot snap, Action reload)
    {
        var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder whose Claude Code uses one profile", AllowMultiple = false });
        string folder = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (folder == null) return;
        ProfileEntry p = await Dialogs.Pick(owner, "Which profile for this folder?",
            "Inside " + Platform.Shorten(folder) + " and its subfolders, plain claude will run as the profile you pick.",
            snap.Profiles, snap.Accounts, snap.Running);
        if (p == null) return;
        Terminal.AddRule(folder, p.Name);
        reload();
    }

    // ---- settings: shortcuts, after-update, Claude Switch updates, Diagnose -------------

    public static Control Settings_(Snapshot snap, Window owner, Action reload)
    {
        StackPanel s = Head("Settings");
        s.Children.Add(Para("", 0, 4));

        CheckBox Box(string text, bool value, Action<bool> set, string tip = null, bool enabled = true)
        {
            var c = new CheckBox { Content = text, IsChecked = value, IsEnabled = enabled, Foreground = Neu.B(Neu.P.Text), Margin = new Thickness(0, 2) };
            if (tip != null) ToolTip.SetTip(c, tip);
            c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); reload(); };
            return c;
        }

        s.Children.Add(Box("Keyboard shortcuts: Ctrl+Alt+1 to 9 open a profile, Ctrl+Alt+0 opens Claude Switch", Settings.Hotkeys,
            v => { Settings.Hotkeys = v; Shell.ApplyHotkeys(); },
            Hotkeys.Supported ? (Hotkeys.Taken.Count > 0 ? "Already used by another app: " + string.Join(", ", Hotkeys.Taken) : null) : "Windows only for now.",
            Hotkeys.Supported));
        s.Children.Add(Box("Reopen profiles when a Claude update closes them", Settings.ReopenAfterUpdate, v => Settings.ReopenAfterUpdate = v));
        s.Children.Add(Box("A taskbar button per profile, in its colour (experimental)", Settings.SeparateTaskbar,
            v => { Settings.SeparateTaskbar = v; Shell.ApplyTaskbar(clearing: !v); },
            TaskbarIdentity.Supported ? "Each profile's Claude windows get their own taskbar button and icon. Turn off to group them again." : "Windows only.",
            TaskbarIdentity.Supported));
        var login = new CheckBox { Content = "Start Claude Switch when I sign in", IsChecked = Shortcuts.StartsAtLogin, Foreground = Neu.B(Neu.P.Text), Margin = new Thickness(0, 2) };
        login.IsCheckedChanged += (_, _) => Shortcuts.SetStartAtLogin(login.IsChecked == true);
        s.Children.Add(login);

        s.Children.Add(Neu.Section("Claude Switch updates"));
        ReleaseInfo next = SelfUpdate.Available;
        DateTime checkedAt = Settings.SelfUpdateCheckedAt;
        s.Children.Add(Para(next != null
            ? "Version " + next.Version.ToString(3) + " is available (you have " + SelfUpdate.Current.ToString(3) + ")."
            : "You have " + SelfUpdate.Current.ToString(3) + "." + (checkedAt == DateTime.MinValue ? "" : " Last checked " + Accounts.Ago(checkedAt) + "."), 0, 8, 12.5));
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        foreach ((SelfUpdateMode m, string label, string tip) in new[]
        {
            (SelfUpdateMode.Notify, "Tell me", "Check daily and offer the update."),
            (SelfUpdateMode.Automatic, "Automatic", "Check daily and install signed updates by itself. Your Claude windows stay open."),
            (SelfUpdateMode.Off, "Off", "Never check. Claude Switch then makes no network requests, unless Linked computers is on."),
        })
        {
            SelfUpdateMode mode = m;
            var b = new NeuButton(label, false, 12.5) { Selected = SelfUpdate.Mode == mode };
            ToolTip.SetTip(b, tip);
            b.Click += () => { SelfUpdate.Mode = mode; reload(); };
            modes.Children.Add(b);
        }
        s.Children.Add(modes);
        s.Children.Add(Row(
            next != null ? Btn("Update to " + next.Version.ToString(3), true, () => _ = Shell.InstallSelfUpdate(next))
                         : Btn("Check now", false, () => _ = Shell.CheckSelfUpdate(manual: true)),
            Btn("Diagnose…", false, () => _ = ShowDiagnose(owner))));
        return Card(s);
    }

    public static async Task ShowDiagnose(Window owner)
    {
        Diagnose.Report r = await Task.Run(() => Diagnose.Run(showEmails: false));
        Window w = Dialogs.Frame("Diagnose", 760);
        var body = new StackPanel();
        body.Children.Add(Neu.T(r.Healthy ? "No problems found." : r.Problems.Count + (r.Problems.Count == 1 ? " problem found." : " problems found."), 15,
                                r.Healthy ? Neu.P.Good : Neu.P.Warn, FontWeight.SemiBold));
        var text = new TextBox
        {
            Text = r.Text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 12,
            Height = 420, Margin = new Thickness(0, 12, 0, 0),
        };
        body.Children.Add(text);
        body.Children.Add(Para("Emails are masked so you can paste this into a bug report. Sign-ins are never read.", 10, 0, 11.5));
        var copy = Btn("Copy report", true, async () => { try { await w.Clipboard.SetTextAsync(r.Text); } catch (Exception) { } });
        var close = Btn("Close", false, w.Close);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(copy);
        row.Children.Add(close);
        body.Children.Add(row);
        Dialogs.SetContent(w, Dialogs.Body("Diagnose", body));
        if (owner != null) await w.ShowDialog(owner); else w.Show();
    }

    // ---- claude-account-switcher's relay (only when installed) ---------------------------

    public static Control RelayPanel(Snapshot snap)
    {
        RelayStatus r = snap.Relay;
        if (r == null) return null;
        StackPanel s = Head("Billing relay", r.Fresh ? "Running" : "Not running", r.Fresh ? Neu.P.Good : Neu.P.Warn);
        s.Children.Add(Para("claude-account-switcher is installed. It bills a Code session to another of your subscriptions inside one window. " +
                            "Claude Switch only reads its status file; it never touches its tokens.", 6, 10, 12.5));
        if (r.Accounts.Count == 0) s.Children.Add(Para("No accounts added to it yet.", 0, 8, 12.5));
        foreach (RelayAccount a in r.Accounts)
        {
            var box = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            string exp = a.Expires == DateTime.MinValue ? "" : (a.Expires - DateTime.Now).TotalDays < 14
                ? "   token expires " + a.Expires.ToString("d MMM") + ", renew it" : "";
            box.Children.Add(Neu.T(a.Name + exp, 13, exp.Length > 0 ? Neu.P.Warn : Neu.P.Text, FontWeight.SemiBold));
            if (a.SessionPct != null || a.WeeklyPct != null)
            {
                box.Children.Add(Neu.T("Session " + (a.SessionPct?.ToString() ?? "-") + "%" +
                                       (a.SessionReset == DateTime.MinValue ? "" : " (resets " + a.SessionReset.ToString("ddd h:mm tt") + ")") +
                                       "   Weekly " + (a.WeeklyPct?.ToString() ?? "-") + "%" +
                                       (a.WeeklyReset == DateTime.MinValue ? "" : " (resets " + a.WeeklyReset.ToString("ddd h:mm tt") + ")"), 12, Neu.P.Muted));
                if (a.UsageAt != DateTime.MinValue) box.Children.Add(Neu.T("Live from the API, " + Accounts.Ago(a.UsageAt), 11.5, Neu.P.Muted));
            }
            else box.Children.Add(Neu.T("No usage yet: it appears after this account's first routed message.", 11.5, Neu.P.Muted));
            s.Children.Add(box);
        }
        return Card(s);
    }
}
