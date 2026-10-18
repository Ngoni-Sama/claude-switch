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

    // Copies an address the way people paste it (CS-XXXXX-…).
    public static async Task<bool> Copy(Window owner, string address)
    {
        try { await owner.Clipboard.SetTextAsync(LinkCrypto.Format(address)); return true; }
        catch (Exception ex) { ErrorLog.Write(ex); return false; }
    }

    // A copy button that says "Copied" for a moment.
    static NeuButton CopyButton(Window owner, string address, string label = "Copy address")
    {
        NeuButton b = null;
        b = Btn(label, false, async () =>
        {
            if (!await Copy(owner, address)) return;
            b.Text = "Copied";
            await Task.Delay(1800);
            b.Text = label;
        }, 12.5);
        ToolTip.SetTip(b, LinkCrypto.Format(address));
        return b;
    }

    // An address you can click to copy.
    static Control AddressText(Window owner, string address, double size)
    {
        var t = Neu.T(LinkCrypto.Format(address), size, null, FontWeight.SemiBold, wrap: true);
        t.FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");
        var host = new Border { Child = t, Background = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        ToolTip.SetTip(host, "Click to copy");
        Avalonia.Automation.AutomationProperties.SetName(host, "Address " + LinkCrypto.Format(address) + ", click to copy");
        host.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            if (!await Copy(owner, address)) return;
            string was = t.Text;
            t.Text = "Copied to the clipboard";
            await Task.Delay(1500);
            t.Text = was;
        };
        return host;
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
        Control shown = AddressText(owner, address, 13.5);
        shown.Margin = new Thickness(0, 2, 0, 0);
        addr.Children.Add(shown);
        NeuButton copy = CopyButton(owner, address);
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
            var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            void Act(Control c) { c.Margin = new Thickness(0, 0, 6, 6); actions.Children.Add(c); }
            if (peer.Linked) Act(Btn("Send profiles…", true, () => _ = Send(owner, snap, peer, reload), 12.5));
            Act(CopyButton(owner, peer.Address, "Copy its address"));
            Act(Btn("Unlink", false, () => _ = Unlink(owner, peer, reload), 12.5));
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
            if (await Dialogs.Ask(owner, "Linked computers is on",
                    "This computer's address is\n\n" + LinkCrypto.Format(address) + "\n\nTurn it on on your other computer too, then press " +
                    "Link a computer on either one and paste the other's address.", "Copy address", "Done") == 0)
                await Copy(owner, address);
        }
        catch (LinkException ex) { await Dialogs.Info(owner, "Couldn't turn it on", ex.Message); }
        reload();
    }

    // One box: the address (filled in when one is already on the clipboard).
    // The other computer's own name arrives with its answer.
    static async Task LinkComputer(Window owner, Action reload)
    {
        string pasted = "";
        try
        {
            string clip = await owner.Clipboard.GetTextAsync();
            if (LinkCrypto.Normalize(clip) is string a && a != Link.Address) pasted = LinkCrypto.Format(a);
        }
        catch (Exception) { }
        string input = await Dialogs.Text(owner, "Link a computer",
            (pasted.Length > 0 ? "Filled in from what you copied. " : "") +
            "Paste the other computer's address (Copy address on its Linked computers page).", pasted,
            t => LinkCrypto.Normalize(t) == null ? "That isn't a complete address: CS- and 24 letters and numbers."
               : LinkCrypto.Normalize(t) == Link.Address ? "That's this computer's own address." : null);
        if (input == null) return;
        try
        {
            LinkPeer p = await Link.Request(input, null);
            Shell.Notify(p.Linked ? "Linked" : "Link request sent",
                p.Linked ? p.Name + " is linked." : "Accept it on the other computer. It appears there within a few minutes.");
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

    // ---- progress -------------------------------------------------------------------

    // A gradient bar with "12 of 85 MB · 1.2 MB/s · about 1 min left" under it.
    sealed class ProgressView : StackPanel
    {
        readonly Border fill = new() { CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        readonly Grid track = new() { Height = 10 };
        readonly TextBlock text = Neu.T("", 13, Neu.P.Muted);
        readonly TextBlock title = Neu.T("", 14.5, null, FontWeight.SemiBold);
        double fraction;

        public ProgressView(bool compact = false)
        {
            Spacing = compact ? 5 : 8;
            if (compact) { title.FontSize = 12.5; track.Height = 7; text.FontSize = 12; }
            fill.Background = Neu.AccentBrush;
            track.Children.Add(new Border { Background = Neu.B(Neu.P.Inner), CornerRadius = new CornerRadius(5) });
            track.Children.Add(fill);
            track.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * fraction;
            Children.Add(title);
            Children.Add(track);
            Children.Add(text);
            Avalonia.Automation.AutomationProperties.SetName(this, "Progress");
        }

        public void Set(LinkProgress p)
        {
            title.Text = p.Text.Split('…')[0] + (p.Total > 0 && p.Done < p.Total ? " " + (int)(p.Fraction * 100) + "%" : "");
            fraction = p.Total > 0 ? p.Fraction : 0;
            fill.Width = track.Bounds.Width * fraction;
            var bits = new List<string>();
            if (p.Total > 0) bits.Add(Mb(p.Done) + " of " + Mb(p.Total));
            else bits.Add(p.Text);
            if (p.BytesPerSecond > 0) bits.Add(Mb((long)p.BytesPerSecond) + "/s");
            if (p.Left is TimeSpan left) bits.Add(left.TotalSeconds < 60 ? "a few seconds left" : "about " + Math.Ceiling(left.TotalMinutes) + " min left");
            text.Text = string.Join("  ·  ", bits);
            Avalonia.Automation.AutomationProperties.SetName(this, title.Text + ". " + text.Text);
        }

        public void Full()
        {
            fraction = 1;
            fill.Width = track.Bounds.Width;
        }

        public void Message(string heading, string detail, Color? color = null)
        {
            title.Text = heading;
            title.Foreground = Neu.B(color ?? Neu.P.Text);
            text.Text = detail;
        }

        static string Mb(long b) => (b / 1024.0 / 1024.0).ToString(b < 10 * 1024 * 1024 ? "0.0" : "0") + " MB";
    }

    // ---- transfers list -----------------------------------------------------------

    // Everything coming and going, with its #ID and live progress.
    public static Control Transfers(Window owner, Action reload)
    {
        var incoming = (Demo.On ? Demo.LinkInbox : Link.Inbox).Where(m => m.Kind == "transfer").ToList();
        var outgoing = Demo.On ? new List<Outgoing>() : Link.Outgoings();
        var s = new StackPanel { Spacing = 14 };
        var head = new DockPanel();
        var count = Neu.T(incoming.Count + outgoing.Count == 0 ? "None yet" : (incoming.Count + outgoing.Count) + " in the last week", 12.5, Neu.P.Muted);
        count.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(count, Dock.Right);
        head.Children.Add(count);
        head.Children.Add(Neu.Heading("Transfers", 15));
        s.Children.Add(head);
        if (incoming.Count + outgoing.Count == 0)
            s.Children.Add(Neu.Para("Transfers you send or receive show here with their #ID, live speed and time left. Interrupted ones resume where they stopped.", 13));

        foreach (LinkMessage m in incoming)
        {
            LinkMessage msg = m;
            var row = Row(m.ShortId, "From " + m.Name + "  ·  " + string.Join(", ", m.Profiles), out StackPanel actions, out ProgressView bar);
            if (Link.IsActive(m.Id) || Link.LiveProgress(m.Id) is not null) Track(bar, m.Id);
            else if (!m.Complete)
            {
                double frac = m.Parts > 0 ? (double)m.Received / m.Parts : 0;
                bar.Set(new LinkProgress("Uploading from " + m.Name + "…", (long)(frac * m.Size), m.Size, m.UploadRate,
                    m.UploadRate > 0 ? TimeSpan.FromSeconds((m.Size - frac * m.Size) / m.UploadRate) : null));
            }
            else bar.Message("Ready to receive", Mb(m.Size) + "  ·  sent " + Accounts.Ago(m.Created) + "  ·  waits until " + m.Expires.ToString("ddd d MMM"));
            if (m.Complete && !Link.IsActive(m.Id)) actions.Children.Add(Btn("Receive", true, () => _ = Receive(owner, msg, reload), 12.5));
            if (!Link.IsActive(m.Id)) actions.Children.Add(Btn("Decline", false, async () => { try { await Link.Decline(msg); } catch (LinkException) { } reload(); }, 12.5));
            s.Children.Add(row);
        }

        foreach (Outgoing o in outgoing)
        {
            Outgoing rec = o;
            var row = Row(o.ShortId, "To " + o.ToName + "  ·  " + string.Join(", ", o.Profiles), out StackPanel actions, out ProgressView bar);
            switch (o.State)
            {
                case "uploading":
                    if (Link.IsActive(o.Id)) Track(bar, o.Id);
                    else
                    {
                        bar.Set(Link.LiveProgress(o.Id) ?? new LinkProgress("Paused", 0, o.Size));
                        bar.Message("Paused", (o.Error ?? "Waiting to resume.") + " It carries on from where it stopped.", Neu.P.Warn);
                        actions.Children.Add(Btn("Resume", true, async () =>
                        {
                            try { await Link.ResumeOutgoing(); } catch (Exception ex) { await Dialogs.Info(owner, "Couldn't resume", ex.Message); }
                            reload();
                        }, 12.5));
                    }
                    actions.Children.Add(Btn("Cancel", false, async () => { await Link.CancelOutgoing(rec); reload(); }, 12.5));
                    break;
                case "waiting":
                    bar.Message("Waiting for " + o.ToName + " to collect it", Mb(o.Size) + "  ·  sent " + Accounts.Ago(o.Finished == DateTime.MinValue ? o.Created : o.Finished));
                    bar.Full();
                    actions.Children.Add(Btn("Cancel", false, async () => { await Link.CancelOutgoing(rec); reload(); }, 12.5));
                    break;
                default:
                    bar.Message(o.State == "collected" ? "Collected by " + o.ToName : o.State == "expired" ? "Expired before it was collected" : "Failed",
                                Mb(o.Size) + (o.Error != null ? "  ·  " + o.Error : ""), o.State == "collected" ? Neu.P.Good : Neu.P.Muted);
                    if (o.State == "collected") bar.Full();
                    actions.Children.Add(Btn("Clear", false, () => { Link.ClearOutgoing(rec); reload(); }, 12.5));
                    break;
            }
            s.Children.Add(row);
        }
        return Neu.Tile(s);
    }

    static Control Row(string id, string what, out StackPanel actions, out ProgressView bar)
    {
        var row = new StackPanel { Spacing = 6 };
        var top = new DockPanel();
        actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        var idPill = new Border
        {
            Background = Neu.B(Neu.P.AccentSoft), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2), Margin = new Thickness(0, 0, 8, 0),
            Child = Neu.T(id, 12, Neu.P.Accent, FontWeight.SemiBold), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(idPill, "Transfer ID, the same on both computers");
        DockPanel.SetDock(idPill, Dock.Left);
        top.Children.Add(idPill);
        var label = Neu.T(what, 13.5, null, FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(label);
        row.Children.Add(top);
        bar = new ProgressView(compact: true);
        row.Children.Add(bar);
        return Neu.Inset(row, 12, new Thickness(14, 10));
    }

    // Live updates for one transfer while this view is on screen.
    static void Track(ProgressView bar, string id)
    {
        if (Link.LiveProgress(id) is LinkProgress now) bar.Set(now);
        Action<string, LinkProgress> on = (tid, p) => { if (tid == id) Dispatcher.UIThread.Post(() => bar.Set(p)); };
        bar.AttachedToVisualTree += (_, _) => { Link.Progress -= on; Link.Progress += on; };
        bar.DetachedFromVisualTree += (_, _) => Link.Progress -= on;
        Link.Progress += on;
    }

    static string Mb(long b) => (b / 1024.0 / 1024.0).ToString(b < 10 * 1024 * 1024 ? "0.0" : "0") + " MB";

    // ---- sending ---------------------------------------------------------------------

    // Everything selected by default: one click on Send. Progress shows in place.
    public static Task Send(Window owner, Snapshot snap, LinkPeer peer, Action reload)
    {
        Window w = Dialogs.Frame("Send to " + peer.Name, 600);
        var tcs = new TaskCompletionSource();
        // Closing the window doesn't stop the upload: it carries on in the
        // background (see Transfers on the Linked computers page).
        w.Closed += (_, _) => tcs.TrySetResult();

        var body = new StackPanel();
        body.Children.Add(Neu.Para("Encrypted for " + peer.Name + " only. It waits there for up to a week, so that computer doesn't need to be on. " +
                                   "If the connection drops it resumes where it stopped, and sending the same profiles again replaces the copy still waiting.", 13.5));
        var options = new StackPanel();
        options.Children.Add(Neu.Section("Profiles"));
        var checks = new List<(ProfileEntry, CheckBox)>();
        foreach (ProfileEntry p in snap.Profiles)
        {
            AccountInfo a = snap.Accounts[p.Name];
            Control c = ExportWindow.Check(p.Name, (a.SignedIn ? a.Email ?? "signed in" : "not signed in") + "   ·   " + snap.Sessions[p.Name].Count + " sessions", true);
            checks.Add((p, (CheckBox)((Panel)c).Children[0]));
            options.Children.Add(c);
        }
        options.Children.Add(Neu.Section("Include"));
        CheckBox Add(string t, string d, bool on) { Control c = ExportWindow.Check(t, d, on); options.Children.Add(c); return (CheckBox)((Panel)c).Children[0]; }
        var cSessions = Add("Sessions and transcripts", "Code-tab conversations, to carry on there", true);
        var cMemory = Add("Claude Code memory", "The notes Claude keeps per project", true);
        var cConfig = Add("Claude Code settings, skills, agents and hooks", "Never your sign-in", true);
        var cDesktop = Add("Claude Desktop settings and MCP servers", "Can contain API keys; they're encrypted for that computer only", true);
        body.Children.Add(options);

        var progress = new ProgressView { IsVisible = false, Margin = new Thickness(0, 18, 0, 0) };
        body.Children.Add(progress);
        var status = Neu.T("", 13, Neu.P.Bad, FontWeight.SemiBold);
        status.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(status);
        var send = new NeuButton("Send", primary: true) { MinWidth = 110 };
        var cancel = new NeuButton("Cancel");
        cancel.Click += w.Close;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 18, 0, 0) };
        row.Children.Add(cancel);
        row.Children.Add(send);
        body.Children.Add(row);

        bool done = false;
        send.Click += async () =>
        {
            if (done) { w.Close(); return; }
            var o = new ExportOptions
            {
                Profiles = checks.Where(x => x.Item2.IsChecked == true).Select(x => x.Item1).ToList(),
                Sessions = cSessions.IsChecked == true, Memory = cMemory.IsChecked == true, CodeConfig = cConfig.IsChecked == true, DesktopSettings = cDesktop.IsChecked == true,
            };
            if (o.Profiles.Count == 0) { status.Text = "Choose at least one profile."; return; }
            if (!(o.Sessions || o.Memory || o.CodeConfig || o.DesktopSettings)) { status.Text = "Choose at least one thing to include."; return; }
            status.Text = "";
            options.IsEnabled = false;
            options.Opacity = 0.5;
            send.IsEnabled = false;
            cancel.Text = "Hide (keeps sending)";
            progress.IsVisible = true;
            try
            {
                Outgoing sent = await Link.SendProfiles(peer, o, pr => Dispatcher.UIThread.Post(() => progress.Set(pr)));
                progress.Message("Sent " + sent.ShortId + " to " + peer.Name, (sent.Size / 1024.0 / 1024.0).ToString("0.0") +
                    " MB is waiting there. It appears within a few minutes, and is deleted from the relay once it's collected.", Neu.P.Good);
                done = true;
                send.Text = "Done";
                send.IsEnabled = true;
                cancel.IsVisible = false;
                reload();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ex is not LinkException) ErrorLog.Write(ex);
                progress.IsVisible = false;
                status.Text = "Paused: " + ex.Message + " Press Resume to carry on from where it stopped.";
                options.IsEnabled = true;
                options.Opacity = 1;
                send.IsEnabled = true;
                send.Text = "Resume";
                cancel.Text = "Close";
                reload();
            }
        };

        Dialogs.SetContent(w, new ScrollViewer { Content = Dialogs.Body("Send to " + peer.Name, body), MaxHeight = 760 });
        if (owner != null) w.ShowDialog(owner); else w.Show();
        return tcs.Task;
    }

    // ---- receiving ---------------------------------------------------------------------

    // One click: accept a link request (the address is shown in the banner).
    public static async Task AcceptNow(Window owner, LinkMessage m, Action reload)
    {
        try
        {
            await Link.Accept(m, m.Name);
            Shell.Notify("Linked with " + m.Name, "You can now send profiles to each other.");
        }
        catch (LinkException ex) { await Dialogs.Info(owner, "Couldn't accept", ex.Message); }
        reload();
    }

    // Kept for callers that want to review first.
    public static Task Accept(Window owner, LinkMessage m, Action reload) => AcceptNow(owner, m, reload);

    // One click: download with a progress bar, then import it straight away
    // (same choices Import suggests; nothing on this computer is overwritten).
    public static async Task Receive(Window owner, LinkMessage m, Action reload)
    {
        Window w = Dialogs.Frame("From " + m.Name, 520);
        var cts = new CancellationTokenSource();
        var progress = new ProgressView();
        progress.Set(new LinkProgress("Receiving…", 0, m.Size));
        var close = new NeuButton("Cancel");
        bool finished = false;
        close.Click += () => { if (!finished) cts.Cancel(); w.Close(); };
        w.Closed += (_, _) => { if (!finished) cts.Cancel(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        row.Children.Add(close);
        Dialogs.SetContent(w, Dialogs.Body("From " + m.Name, Neu.Para(string.Join(", ", m.Profiles) + ", end-to-end encrypted.", 13.5), progress, row));
        w.Show(owner);

        string path = null;
        try
        {
            path = await Link.Receive(m, pr => Dispatcher.UIThread.Post(() => progress.Set(pr)), cts.Token);
            progress.Message("Importing…", "Adding sessions, memory and settings. Nothing here is overwritten.");
            ImportResult r = await Task.Run(() =>
            {
                using ArchiveInfo info = Backup.Open(path, null);
                List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
                var accounts = profiles.ToDictionary(p => p.Name, Accounts.Read);
                return Backup.Import(info, info.Profiles.Select(ap => Backup.Suggest(ap, profiles, accounts)).ToList(), null);
            });
            try { File.Delete(path); } catch (Exception) { }
            string detail = r.Files + " files added" + (r.Skipped > 0 ? ", " + r.Skipped + " already here" : "") + "." +
                            (r.Notes.Count > 0 ? " " + string.Join(" ", r.Notes) : "") +
                            " Restart a profile's Claude window to see its new sessions.";
            progress.Message("Received from " + m.Name, detail, Neu.P.Good);
        }
        catch (OperationCanceledException) { w.Close(); }
        catch (Exception ex)
        {
            if (ex is not LinkException) ErrorLog.Write(ex);
            progress.Message("Couldn't receive", ex.Message + (path != null ? " The download is saved at " + path + "; import it with Import… on Backup & move." : ""), Neu.P.Bad);
        }
        finished = true;
        close.Text = "Done";
        reload();
    }
}
