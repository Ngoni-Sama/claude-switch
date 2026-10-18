// Linked computers: the dashboard tile, linking by address, sending profiles
// and receiving them.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public static class LinkUi
{
    static NeuButton Btn(string text, bool primary, Action click, double size = 13.5)
    {
        var b = new NeuButton(text, primary, size);
        b.Click += click;
        return b;
    }

    static WrapPanel Row(params Control[] items)
    {
        var r = new WrapPanel();
        foreach (Control c in items) { c.Margin = new Thickness(0, 0, 10, 8); r.Children.Add(c); }
        return r;
    }

    // ---- dashboard tile ------------------------------------------------------------------

    public static Control Panel(Snapshot snap, Window owner, Action reload)
    {
        bool on = Demo.On || Link.On;
        var s = new StackPanel();
        var head = new DockPanel();
        var badge = Neu.T(on ? "On" : "Off", 12.5, on ? Neu.P.Good : Neu.P.Muted, FontWeight.SemiBold);
        badge.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        head.Children.Add(Neu.Heading("Linked computers", 15));
        s.Children.Add(head);

        if (!on)
        {
            var p = Neu.Para("Send profiles to your other computers over the internet, wherever they are: another office, another country. " +
                             "Each computer gets an address; link two once and send any time. It's end-to-end encrypted: the relay only ever " +
                             "holds scrambled data for up to a week, and sign-ins never leave a computer.", 13);
            p.Margin = new Thickness(0, 6, 0, 12);
            s.Children.Add(p);
            s.Children.Add(Row(Btn("Turn on", true, () => _ = TurnOn(owner, reload))));
            return Neu.Tile(s);
        }

        string address = Demo.On ? Demo.LinkAddress : Link.Address;
        var addr = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        addr.Children.Add(Neu.T("This computer's address", 12, Neu.P.Muted));
        var shown = Neu.T(LinkCrypto.Format(address), 13.5, null, FontWeight.SemiBold, wrap: true);
        shown.FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");
        shown.Margin = new Thickness(0, 2, 0, 0);
        addr.Children.Add(shown);
        var copy = Btn("Copy address", false, async () =>
        {
            try { await owner.Clipboard.SetTextAsync(LinkCrypto.Format(address)); } catch (Exception) { }
        }, 12.5);
        var who = Neu.T("Others see it as " + (Demo.On ? "Studio PC" : Link.ComputerName), 12, Neu.P.Muted);
        who.Margin = new Thickness(0, 2, 0, 0);
        addr.Children.Add(who);
        copy.Margin = new Thickness(0, 8, 0, 0);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        addr.Children.Add(copy);
        s.Children.Add(addr);

        List<LinkPeer> peers = Demo.On ? Demo.LinkPeers : Link.Peers();
        var list = new StackPanel { Spacing = 12 };
        if (peers.Count == 0)
            list.Children.Add(Neu.Para("No computers linked yet. On the other computer, turn this on too, then link them with either address.", 12.5));
        foreach (LinkPeer p in peers.OrderBy(p => p.Name))
        {
            LinkPeer peer = p;
            var line = new StackPanel();
            line.Children.Add(Neu.T(peer.Name, 14, null, FontWeight.SemiBold));
            line.Children.Add(Neu.T((peer.Linked ? "Linked" : "Waiting for it to accept") + "  ·  " + LinkCrypto.Format(peer.Address)[..14] + "…", 12,
                                    peer.Linked ? Neu.P.Good : Neu.P.Warn));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            if (peer.Linked) actions.Children.Add(Btn("Send profiles…", true, () => _ = Send(owner, snap, peer, reload), 12.5));
            actions.Children.Add(Btn("Unlink", false, () => _ = Unlink(owner, peer, reload), 12.5));
            line.Children.Add(actions);
            list.Children.Add(line);
        }
        Border inner = Neu.Inset(list, 6, new Thickness(14, 10));
        inner.Margin = new Thickness(0, 12, 0, 10);
        s.Children.Add(inner);

        s.Children.Add(Row(
            Btn("Link a computer…", true, () => _ = LinkComputer(owner, reload)),
            Btn("Check now", false, async () => { await Link.Refresh(); reload(); }),
            Btn("More…", false, () => _ = More(owner, reload), 12.5)));
        DateTime checkedAt = Demo.On ? DateTime.Now.AddMinutes(-2) : Link.CheckedAt;
        string status = Link.LastError ?? (checkedAt == DateTime.MinValue ? "Hasn't checked for transfers yet." : "Checked for transfers " + Accounts.Ago(checkedAt) + ".");
        s.Children.Add(Neu.Para(status + (Link.RelayUrl == Link.DefaultRelay ? "" : " Relay: " + Link.RelayUrl), 11.5,
                                Link.LastError != null ? Neu.P.Warn : Neu.P.Muted));
        return Neu.Tile(s);
    }

    static async Task TurnOn(Window owner, Action reload)
    {
        if (await Dialogs.Ask(owner, "Turn on Linked computers?",
                "This computer gets an address you can give to your other computers. Linked computers can send each other profiles " +
                "(sessions, memory and settings, never sign-ins).\n\n" +
                "Transfers go through a relay at " + new Uri(Link.RelayUrl).Host + ". They're encrypted on this computer, so the relay " +
                "can't read them, and they're deleted once collected or after a week. While it's on, Claude Switch checks the relay " +
                "for transfers every few minutes.",
                "Turn on", "Cancel") != 0) return;
        try
        {
            string address = await Link.TurnOn();
            await Dialogs.Info(owner, "Linked computers is on",
                "This computer's address is\n\n" + LinkCrypto.Format(address) + "\n\nTurn it on on your other computer too, then press " +
                "Link a computer on either one and enter the other's address.");
        }
        catch (LinkException ex) { await Dialogs.Info(owner, "Couldn't turn it on", ex.Message); }
        reload();
    }

    static async Task LinkComputer(Window owner, Action reload)
    {
        string input = await Dialogs.Text(owner, "Link a computer",
            "Enter the other computer's address. It's on its Linked computers card, like CS-XXXXX-XXXXX-XXXXX-XXXXX-XXXX.", "",
            t => LinkCrypto.Normalize(t) == null ? "That isn't a complete address (24 letters and numbers after CS-)."
               : LinkCrypto.Normalize(t) == Link.Address ? "That's this computer's own address." : null);
        if (input == null) return;
        string name = await Dialogs.Text(owner, "Name it", "What should this computer call it? For example Office PC or Laptop in London.", "",
            t => string.IsNullOrWhiteSpace(t) || t.Trim().Length > 40 ? "Use 1 to 40 characters." : null);
        if (name == null) return;
        try
        {
            LinkPeer p = await Link.Request(input, name);
            await Dialogs.Info(owner, "Link request sent",
                p.Linked ? p.Name + " is linked." :
                "Accept it on " + p.Name + ". It appears there within a few minutes (or press Check now on it). Make sure the address it shows is this one:\n\n" +
                LinkCrypto.Format(Link.Address));
        }
        catch (LinkException ex) { await Dialogs.Info(owner, "Couldn't link", ex.Message); }
        reload();
    }

    static async Task Unlink(Window owner, LinkPeer peer, Action reload)
    {
        if (await Dialogs.Ask(owner, "Unlink " + peer.Name + "?", "Neither computer can send to the other until you link them again.", "Unlink", "Cancel") != 0) return;
        await Link.Unlink(peer);
        reload();
    }

    static async Task More(Window owner, Action reload)
    {
        int c = await Dialogs.Ask(owner, "Linked computers",
            "Name others see: " + Link.ComputerName + "\nRelay: " + Link.RelayUrl +
            "\n\nYou can run your own relay (it's in the relay folder of the Claude Switch repo). Turning this off forgets this computer's address and its links.",
            "Rename this computer", "Use another relay", "Turn off", "Close");
        if (c == 0)
        {
            string n = await Dialogs.Text(owner, "Rename this computer", "The name your other computers see for this one.", Link.ComputerName,
                t => string.IsNullOrWhiteSpace(t) || t.Trim().Length > 40 ? "Use 1 to 40 characters." : null);
            if (n != null) Settings.LinkName = n.Trim();
        }
        else if (c == 1)
        {
            string u = await Dialogs.Text(owner, "Use another relay", "The relay's web address. Leave it empty for the default. Every linked computer must use the same relay.",
                Settings.LinkRelay ?? "", t => string.IsNullOrWhiteSpace(t) || Uri.TryCreate(t.Trim(), UriKind.Absolute, out Uri x) && (x.Scheme == "https" || x.IsLoopback)
                    ? null : "Use an https:// address.");
            if (u != null)
            {
                Settings.LinkRelay = string.IsNullOrWhiteSpace(u) ? null : u.Trim();
                if (Link.On) try { await Link.TurnOn(); } catch (LinkException ex) { await Dialogs.Info(owner, "Couldn't register with that relay", ex.Message); }
            }
        }
        else if (c == 2 && await Dialogs.Ask(owner, "Turn off Linked computers?",
                     "This computer's address and all its links are forgotten. Linked computers can't send to it any more.", "Turn off", "Cancel") == 0)
            Link.TurnOff();
        reload();
    }

    // ---- sending ---------------------------------------------------------------------

    public static Task Send(Window owner, Snapshot snap, LinkPeer peer, Action reload)
    {
        Window w = Dialogs.Frame("Send to " + peer.Name, 620);
        var tcs = new TaskCompletionSource();
        w.Closed += (_, _) => tcs.TrySetResult();
        var cts = new CancellationTokenSource();

        var body = new StackPanel();
        body.Children.Add(Neu.Para("Send profiles to " + peer.Name + " over the internet. It's encrypted for " + peer.Name +
                                   " only and waits there for up to a week, so that computer doesn't need to be on right now."));
        body.Children.Add(Neu.Section("Profiles"));
        var checks = new List<(ProfileEntry, CheckBox)>();
        foreach (ProfileEntry p in snap.Profiles)
        {
            AccountInfo a = snap.Accounts[p.Name];
            Control c = ExportWindow.Check(p.Name, (a.SignedIn ? a.Email ?? "signed in" : "not signed in") + "   ·   " + snap.Sessions[p.Name].Count + " sessions", true);
            checks.Add((p, (CheckBox)((Panel)c).Children[0]));
            body.Children.Add(c);
        }
        body.Children.Add(Neu.Section("Include"));
        CheckBox Add(string t, string d, bool on) { Control c = ExportWindow.Check(t, d, on); body.Children.Add(c); return (CheckBox)((Panel)c).Children[0]; }
        var cSessions = Add("Sessions and transcripts", "Code-tab conversations, to carry on there", true);
        var cMemory = Add("Claude Code memory", "The notes Claude keeps per project", true);
        var cConfig = Add("Claude Code settings, skills, agents and hooks", "Never your sign-in", true);
        var cDesktop = Add("Claude Desktop settings and MCP servers", "Can contain API keys; they're encrypted for that computer only", true);

        var status = Neu.T("", 13, Neu.P.Accent, FontWeight.SemiBold);
        status.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(status);
        var send = new NeuButton("Send", primary: true);
        var cancel = new NeuButton("Cancel");
        cancel.Click += () => { cts.Cancel(); w.Close(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(send);
        row.Children.Add(cancel);
        body.Children.Add(row);

        send.Click += async () =>
        {
            var o = new ExportOptions
            {
                Profiles = checks.Where(x => x.Item2.IsChecked == true).Select(x => x.Item1).ToList(),
                Sessions = cSessions.IsChecked == true, Memory = cMemory.IsChecked == true, CodeConfig = cConfig.IsChecked == true, DesktopSettings = cDesktop.IsChecked == true,
            };
            if (o.Profiles.Count == 0) { status.Text = "Choose at least one profile."; return; }
            if (!(o.Sessions || o.Memory || o.CodeConfig || o.DesktopSettings)) { status.Text = "Choose at least one thing to include."; return; }
            send.IsEnabled = false;
            string error = null;
            long size = 0;
            try { size = await Link.SendProfiles(peer, o, m => Dispatcher.UIThread.Post(() => status.Text = m), cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (LinkException ex) { error = ex.Message; }
            catch (Exception ex) { error = ex.Message; ErrorLog.Write(ex); }
            send.IsEnabled = true;
            status.Text = "";
            if (error != null) { await Dialogs.Info(w, "Couldn't send", error); return; }
            await Dialogs.Info(w, "Sent to " + peer.Name,
                (size / 1024.0 / 1024.0).ToString("0.0") + " MB is waiting for " + peer.Name + ". It shows there within a few minutes; it's deleted from the relay once collected, or after a week.");
            w.Close();
        };

        Dialogs.SetContent(w, new ScrollViewer { Content = Dialogs.Body("Send to " + peer.Name, body), MaxHeight = 760 });
        if (owner != null) w.ShowDialog(owner); else w.Show();
        return tcs.Task;
    }

    // ---- receiving ---------------------------------------------------------------------

    public static async Task Accept(Window owner, LinkMessage m, Action reload)
    {
        int c = await Dialogs.Ask(owner, "Link with " + m.Name + "?",
            m.Name + " wants to link with this computer. Its address is\n\n" + LinkCrypto.Format(m.From) +
            "\n\nOnly accept if that's your computer and the address matches what it shows. Linked computers can send each other profiles.",
            "Accept", "Decline", "Later");
        if (c is not (0 or 1)) return;
        try
        {
            if (c == 0) await Link.Accept(m, m.Name);
            else await Link.Decline(m);
        }
        catch (LinkException ex) { await Dialogs.Info(owner, "Couldn't answer", ex.Message); }
        reload();
    }

    public static async Task Receive(Window owner, LinkMessage m, Action reload)
    {
        Window w = Dialogs.Frame("Receiving from " + m.Name, 480);
        var status = Neu.T("Starting…", 14, Neu.P.Accent, FontWeight.SemiBold);
        var cts = new CancellationTokenSource();
        var stop = new NeuButton("Cancel");
        stop.Click += () => { cts.Cancel(); w.Close(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(stop);
        Dialogs.SetContent(w, Dialogs.Body("Receiving from " + m.Name, Neu.Para("Downloading and decrypting " + string.Join(", ", m.Profiles) + "."), status, row));
        w.Show(owner);

        string path = null, error = null;
        try { path = await Link.Receive(m, t => Dispatcher.UIThread.Post(() => status.Text = t), cts.Token); }
        catch (OperationCanceledException) { }
        catch (LinkException ex) { error = ex.Message; }
        catch (Exception ex) { error = ex.Message; ErrorLog.Write(ex); }
        w.Close();
        reload();
        if (error != null) { await Dialogs.Info(owner, "Couldn't receive", error); return; }
        if (path == null) return;

        bool imported = await ImportWindow.Open(owner, path, m.Name);
        if (imported) { try { File.Delete(path); } catch (Exception) { } }
        else await Dialogs.Info(owner, "Saved for later", "It's saved at\n" + path + "\n\nImport it any time with Import… on the Move to a new computer card.");
        reload();
    }
}
