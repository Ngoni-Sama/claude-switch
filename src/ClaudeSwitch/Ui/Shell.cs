// The running app: main window (hidden, not closed, when you close it), tray
// icon + menu, single-instance hand-off, usage-report clipboard capture and
// limit notifications.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClaudeSwitch.Core;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace ClaudeSwitch.Ui;

public static class Shell
{
    static IClassicDesktopStyleApplicationLifetime life;
    static Window main;
    static DashboardView dashboard;
    static TrayIcon tray;
    static WindowNotificationManager toasts;
    static bool exiting;
    static string lastClip;
    static readonly HashSet<string> alerted = new();

    public static void Start(IClassicDesktopStyleApplicationLifetime lifetime, string[] args)
    {
        life = lifetime;
        main = new Window
        {
            Title = "Claude Switch", Width = 1180, Height = 860, MinWidth = 760, MinHeight = 560, Icon = Dialogs.AppIcon,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Neu.B(Neu.P.Bg),
        };
        Mount();
        main.Closing += (_, e) => { if (!exiting) { e.Cancel = true; main.Hide(); } };
        toasts = new WindowNotificationManager(main) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
        Neu.ThemeChanged += () => { main.Background = Neu.B(Neu.P.Bg); Mount(); BuildMenu(); };

        Demo.On = args.Contains("--demo");
        string shots = Arg(args, "--screenshots");
        if (shots != null) { Screenshots(shots, Arg(args, "--size")); return; }
        if (Demo.On)
        {
            // Made-up data in a plain window: no tray, hotkeys or clipboard
            // watching, and closing it quits.
            exiting = true;
            main.Closed += (_, _) => life.Shutdown();
            main.Show();
            return;
        }

        BuildTray();
        SingleInstance.Listen(msg => Dispatcher.UIThread.Post(() => { if (msg == "show") ShowMain(); }));

        var clip = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        clip.Tick += async (_, _) => await PollClipboard();
        clip.Start();
        var limits = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        limits.Tick += (_, _) => CheckLimits();
        limits.Start();

        // Linked computers: check the relay for transfers every few minutes,
        // and say when something new arrives.
        var seenLink = new HashSet<string>(Link.Inbox.Select(m => m.Id));
        Link.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            foreach (LinkMessage m in Link.Inbox.Where(m => seenLink.Add(m.Id)))
                Notify(m.Kind == "link" ? m.Name + " wants to link" : m.Name + " sent you profiles",
                       m.Kind == "link" ? "Open Claude Switch to accept or decline." : string.Join(", ", m.Profiles) + ". Open Claude Switch to receive them.");
            dashboard?.Reload(true);
        });
        // Every 5 seconds while something is moving (uploads in either direction,
        // a transfer waiting to be collected), otherwise every 3 minutes.
        DateTime lastCheck = DateTime.MinValue;
        bool checking = false;
        var linkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        linkTimer.Tick += async (_, _) =>
        {
            if (!Link.On || checking) return;
            if (!Link.Busy && DateTime.Now - lastCheck < TimeSpan.FromMinutes(3)) return;
            checking = true;
            lastCheck = DateTime.Now;
            try { await Link.Refresh(); await Link.CheckOutgoing(); }
            catch (Exception ex) { ErrorLog.Write(ex); }
            finally { checking = false; }
        };
        linkTimer.Start();
        // Resume uploads an earlier run left half done.
        Dispatcher.UIThread.Post(async () =>
        {
            if (!Link.On) return;
            await Link.Refresh();
            _ = Task.Run(Link.ResumeOutgoing);
        }, DispatcherPriority.Background);
        Dispatcher.UIThread.Post(CheckLimits, DispatcherPriority.ApplicationIdle);

        if (!args.Contains("--tray") || Profiles.LoadOrEmpty().Count == 0) ShowMain();
        Dispatcher.UIThread.Post(async () => { await OpenAtStart(); await OfferRestore(); }, DispatcherPriority.Background);

        ApplyHotkeys();
        var watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        watch.Tick += (_, _) => WatchClaudeUpdates();
        watch.Start();
        var taskbar = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        taskbar.Tick += (_, _) => ApplyTaskbar();
        taskbar.Start();
        var front = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        front.Tick += (_, _) => ShowAccountInFront();
        front.Start();
        var daily = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        daily.Tick += async (_, _) => await CheckSelfUpdate(manual: false);
        daily.Start();
        Dispatcher.UIThread.Post(async () => await CheckSelfUpdate(manual: false), DispatcherPriority.ApplicationIdle);
    }

    // ---- profiles marked "Open when Claude Switch starts" -------------------------

    static async Task OpenAtStart()
    {
        try
        {
            List<ProfileEntry> profiles = Profiles.LoadOrEmpty().Where(p => p.OpenAtStart).ToList();
            if (profiles.Count == 0) return;
            await Task.Run(() =>
            {
                Dictionary<string, int> running = ClaudeApp.FindRunning();
                foreach (ProfileEntry p in profiles.Where(p => !running.ContainsKey(p.Key)))
                {
                    Launcher.Spawn(p);
                    Thread.Sleep(1200);
                }
            });
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    // ---- keyboard shortcuts ---------------------------------------------------------

    public static void ApplyHotkeys()
    {
        Hotkeys.Stop();
        if (!Settings.Hotkeys || !Hotkeys.Supported) return;
        Hotkeys.Start(index => Dispatcher.UIThread.Post(() =>
        {
            if (index == 0) { ShowMain(); return; }
            List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
            if (index - 1 >= profiles.Count) return;
            ProfileEntry p = profiles[index - 1];
            ThreadPool.QueueUserWorkItem(_ => Launcher.Activate(p, ClaudeApp.FindMain(p.LaunchDir)));
        }));
        if (Hotkeys.Taken.Count > 0)
            Notify("Some shortcuts are taken", "Another app already uses " + string.Join(", ", Hotkeys.Taken) + ".");
    }

    // ---- reopening profiles a Claude update closed ------------------------------------
    //
    // Every 30 s: note Claude's version and which profiles are open. When the
    // version changes, any profile that was open just before and is closed now
    // was closed by the update, so it's reopened.

    static string seenVersion;
    static List<string> openBefore = new();

    static void WatchClaudeUpdates()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
                string version = Updater.Scan(new List<ProfileEntry>()).Primary?.Version;
                Dictionary<string, int> running = ClaudeApp.FindRunning();
                List<string> open = profiles.Where(p => running.ContainsKey(p.Key)).Select(p => p.Name).ToList();

                seenVersion ??= Settings.SeenClaudeVersion ?? version;
                if (version != null && seenVersion != null && version != seenVersion)
                {
                    List<string> closed = openBefore.Where(n => !open.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (closed.Count > 0 && Settings.ReopenAfterUpdate)
                    {
                        foreach (string n in closed)
                            if (Profiles.Find(profiles, n) is ProfileEntry p) { Launcher.Spawn(p); Thread.Sleep(1500); }
                        Dispatcher.UIThread.Post(() => Notify("Claude updated to " + version,
                            "Reopened " + string.Join(", ", closed) + ", which the update had closed."));
                    }
                    else Dispatcher.UIThread.Post(() => Notify("Claude updated to " + version, "All your profiles now run the new version."));
                }
                if (version != null && version != Settings.SeenClaudeVersion) Settings.SeenClaudeVersion = version;
                seenVersion = version ?? seenVersion;
                openBefore = open;
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        });
    }

    // ---- a taskbar button per profile (experimental, Windows) -----------------------

    public static void ApplyTaskbar(bool clearing = false)
    {
        if (!TaskbarIdentity.Supported || (!Settings.SeparateTaskbar && !clearing)) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
                Dictionary<string, int> running = ClaudeApp.FindRunning();
                foreach (ProfileEntry p in profiles)
                {
                    if (!running.TryGetValue(p.Key, out int pid)) continue;
                    if (clearing) { TaskbarIdentity.Clear(pid); continue; }
                    if (TaskbarIdentity.Read(pid) == TaskbarIdentity.IdFor(p)) continue;   // already set
                    TaskbarIdentity.Apply(p, pid, ProfileIcon.Write(p.Slug, Profiles.ColorOf(profiles, p)));
                }
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        });
    }

    // ---- which account is in front ---------------------------------------------------

    static string inFront;
    static Dictionary<string, int> runningCache = new();
    static DateTime runningAt = DateTime.MinValue;

    static void ShowAccountInFront()
    {
        if (!Platform.IsWindows || tray == null) return;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if ((DateTime.Now - runningAt).TotalSeconds > 20) { runningCache = ClaudeApp.FindRunning(); runningAt = DateTime.Now; }
                List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
                ProfileEntry p = profiles.FirstOrDefault(x => runningCache.TryGetValue(x.Key, out int pid) && pid == (int)fg);
                if (p == null || p.Name == inFront) return;   // another app in front: keep the last account shown
                inFront = p.Name;
                AccountInfo a = Accounts.Read(p);
                Dispatcher.UIThread.Post(() =>
                {
                    string tip = "Claude Switch - " + p.Name + " in front" + (a.Email != null ? " (" + a.Email + ")" : "");
                    tray.ToolTipText = tip.Length > 120 ? tip[..120] : tip;
                });
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        });
    }

    // ---- Claude Switch's own updates --------------------------------------------------

    public static async Task CheckSelfUpdate(bool manual)
    {
        if (!manual && (SelfUpdate.Mode == SelfUpdateMode.Off || (DateTime.Now - Settings.SelfUpdateCheckedAt).TotalHours < 20)) return;
        try
        {
            ReleaseInfo r = await SelfUpdate.CheckAsync();
            SelfUpdate.Available = r;
            if (r == null)
            {
                if (manual) await Dialogs.Info(main.IsVisible ? main : null, "You're up to date", "Claude Switch " + SelfUpdate.Current.ToString(3) + " is the latest version.");
            }
            else if (SelfUpdate.Mode == SelfUpdateMode.Automatic && !manual && SelfUpdate.CanInstallHere(r) && !main.IsVisible)
                await InstallSelfUpdate(r, quiet: true);
            else
                Notify("Claude Switch " + r.Version.ToString(3) + " is available", "Open Claude Switch and choose Update in Settings.");
            dashboard?.Reload(true);
        }
        catch (Exception ex)
        {
            // 404 = no release published yet: normal, not an error, and not worth
            // asking again until the next daily check.
            bool noRelease = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound };
            if (noRelease) Settings.SelfUpdateCheckedAt = DateTime.Now;
            else ErrorLog.Write(ex);
            if (manual) await Dialogs.Info(main.IsVisible ? main : null, "Couldn't check for updates",
                ex is HttpRequestException hr && hr.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "No release has been published yet."
                    : "GitHub couldn't be reached: " + ex.Message);
        }
    }

    public static async Task InstallSelfUpdate(ReleaseInfo r, bool quiet = false)
    {
        if (!quiet && await Dialogs.Ask(main.IsVisible ? main : null, "Update Claude Switch to " + r.Version.ToString(3) + "?",
                (SelfUpdate.CanInstallHere(r)
                    ? "Claude Switch downloads the signed release, checks it, installs it and reopens. Your Claude windows stay open."
                    : "The release page opens so you can download it.") +
                (string.IsNullOrWhiteSpace(r.Notes) ? "" : "\n\nWhat's new:\n" + (r.Notes.Length > 900 ? r.Notes[..900] + "…" : r.Notes)),
                "Update", "Not now") != 0) return;
        string problem = await SelfUpdate.InstallAsync(r, m => Notify("Updating Claude Switch", m));
        if (problem != null)
        {
            if (!quiet) await Dialogs.Info(main.IsVisible ? main : null, "Update not installed", problem);
            else Notify("Update not installed", problem);
        }
    }

    // At startup (so, at sign-in too): put back the windows that were open, or
    // start clean. Asks once unless you've chosen to always do one or the other.
    static async Task OfferRestore()
    {
        try
        {
            Snapshot snap = await Task.Run(() => Snapshot.Load(false));
            LastSession last = SessionState.ToRestore(snap);
            if (!last.Any) return;

            if (SessionState.Mode == StartupMode.Restore)
            {
                Notify("Claude Switch", "Reopening " + last.Describe + "…");
                await Task.Run(() => SessionState.Restore(last, snap.Profiles));
                dashboard.Reload(true);
                return;
            }

            bool always = false;
            Window w = Dialogs.Frame("Welcome back", 520);
            var body = new StackPanel();
            body.Children.Add(Neu.Para("Last time you had " + last.Describe + " open" +
                (last.At != DateTime.MinValue ? " (" + Accounts.Ago(last.At) + ")" : "") +
                ". Open " + (last.Profiles.Count > 1 ? "them" : "it") + " again, or start with nothing open?"));
            var remember = new CheckBox { Content = "Remember what I choose and stop asking", Foreground = Neu.B(Neu.P.Text), Margin = new Thickness(0, 16, 0, 0) };
            remember.IsCheckedChanged += (_, _) => always = remember.IsChecked == true;
            body.Children.Add(remember);

            int choice = -1;
            var restore = new NeuButton("Reopen " + last.Describe, primary: true);
            restore.Click += () => { choice = 0; w.Close(); };
            var fresh = new NeuButton("Start fresh");
            fresh.Click += () => { choice = 1; w.Close(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Margin = new Thickness(0, 18, 0, 0) };
            row.Children.Add(restore);
            row.Children.Add(fresh);
            body.Children.Add(row);
            Dialogs.SetContent(w, Dialogs.Body("Welcome back", body));
            w.Opened += (_, _) => restore.Focus();

            if (main.IsVisible) await w.ShowDialog(main);
            else { w.Topmost = true; w.Show(); w.Activate(); w.Topmost = false; await WaitClosed(w); }

            if (choice == 0)
            {
                if (always) SessionState.Mode = StartupMode.Restore;
                Notify("Claude Switch", "Reopening " + last.Describe + "…");
                await Task.Run(() => SessionState.Restore(last, snap.Profiles));
                dashboard.Reload(true);
            }
            else if (choice == 1 && always) SessionState.Mode = StartupMode.Fresh;
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    static Task WaitClosed(Window w)
    {
        var tcs = new TaskCompletionSource();
        w.Closed += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    static string Arg(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static void Mount()
    {
        dashboard = new DashboardView();
        main.Content = Chrome.Wrap(main, "Claude Switch", dashboard, canMaximize: true);
    }

    public static void ShowMain()
    {
        if (!main.IsVisible) main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
        dashboard.Reload(true);
    }

    static void Exit()
    {
        exiting = true;
        tray?.Dispose();
        life.Shutdown();
    }

    // ---- tray --------------------------------------------------------------------------

    static void BuildTray()
    {
        tray = new TrayIcon { Icon = Dialogs.AppIcon, ToolTipText = "Claude Switch", Menu = new NativeMenu(), IsVisible = true };
        tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(Application.Current, new TrayIcons { tray });
        tray.Menu.NeedsUpdate += (_, _) => BuildMenu();
        BuildMenu();
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        refresh.Tick += (_, _) => BuildMenu();
        refresh.Start();
    }

    static void BuildMenu()
    {
        if (tray == null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Snapshot snap;
            try { snap = Snapshot.Load(false); } catch (Exception ex) { ErrorLog.Write(ex); return; }
            Dispatcher.UIThread.Post(() =>
            {
                var menu = new NativeMenu();
                foreach (ProfileEntry p in snap.Profiles)
                {
                    AccountInfo a = snap.Accounts[p.Name];
                    int pid = snap.Pid(p);
                    var item = new NativeMenuItem((pid != 0 ? "●  " : "○  ") + p.Name + (a.Email != null ? "  —  " + a.Email : a.SignedIn ? "" : "  —  not signed in"));
                    item.Click += (_, _) => ThreadPool.QueueUserWorkItem(__ => Launcher.Activate(p, pid));
                    menu.Items.Add(item);
                }
                if (snap.Profiles.Count > 0) menu.Items.Add(new NativeMenuItemSeparator());
                var open = new NativeMenuItem("Open Claude Switch");
                open.Click += (_, _) => ShowMain();
                menu.Items.Add(open);
                var theme = new NativeMenuItem("Dark theme") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = Neu.P.IsDark };
                theme.Click += (_, _) => Neu.Switch(Neu.P.IsDark ? "light" : "dark");
                menu.Items.Add(theme);
                var support = new NativeMenuItem("Support this project…");
                support.Click += (_, _) => Links.Open(Links.Support);
                menu.Items.Add(support);
                menu.Items.Add(new NativeMenuItemSeparator());
                var quit = new NativeMenuItem("Quit Claude Switch");
                quit.Click += (_, _) => Exit();
                menu.Items.Add(quit);
                tray.Menu = menu;
                int open2 = snap.Profiles.Count(p => snap.Pid(p) != 0);
                tray.ToolTipText = "Claude Switch — " + open2 + " of " + snap.Profiles.Count + " profiles open";
            });
        });
    }

    // ---- usage reports from the clipboard ------------------------------------------

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    // Only text starting with "Claude Code usage report" is acted on. Anything
    // else is compared in memory to detect changes and never stored.
    static async Task PollClipboard()
    {
        string text;
        try { text = await main.Clipboard.GetTextAsync(); } catch (Exception) { return; }
        if (text == null || text == lastClip) return;
        lastClip = text;
        if (!UsageReport.Looks(text)) return;
        UsageReportData report = UsageReport.Parse(text);
        if (report == null) return;

        int fgPid = 0;
        if (Platform.IsWindows) { GetWindowThreadProcessId(GetForegroundWindow(), out uint pid); fgPid = (int)pid; }
        Snapshot snap = await Task.Run(() => Snapshot.Load(false));
        ProfileEntry front = fgPid != 0 ? snap.Profiles.FirstOrDefault(p => snap.Pid(p) == fgPid) : null;
        ProfileEntry target = await Reports.Assign(main.IsVisible ? main : null, report, front, snap);
        if (target == null) return;
        Notify(target.Name + " usage updated", report.Summary);
        dashboard.Reload(true);
        CheckLimits();
    }

    // ---- limits + notifications -------------------------------------------------------

    static void CheckLimits()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Snapshot snap;
            try { snap = Snapshot.Load(false); } catch (Exception) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                foreach (ProfileEntry p in snap.Profiles)
                {
                    AccountInfo a = snap.Accounts[p.Name];
                    if (!a.AtLimit)
                    {
                        if (alerted.Remove(p.Name)) Notify(p.Name + "'s limit has reset", "0% used: the full allowance is available again.");
                        continue;
                    }
                    if (!alerted.Add(p.Name)) continue;
                    Bucket b = a.LimitedBucket;
                    Notify(p.Name + " reached its " + (b == a.Weekly ? "weekly" : "session") + " limit", b.ResetText + ". Open Claude Switch to continue a session in another profile.");
                }
            });
        });
    }

    // macOS / Linux: the system's notifications. Windows: an in-app toast when
    // the window is open, otherwise the tray tooltip.
    public static void Notify(string title, string body)
    {
        try
        {
            if (Platform.IsMac)
                Process.Start("osascript", new[] { "-e", "display notification \"" + Esc(body) + "\" with title \"" + Esc(title) + "\"" });
            else if (Platform.IsLinux)
                Process.Start("notify-send", new[] { "--app-name=Claude Switch", "--icon=claude-switch", title, body });
            else if (main.IsVisible)
                toasts.Show(new Notification(title, body, NotificationType.Information, TimeSpan.FromSeconds(8)));
            else if (tray != null)
                tray.ToolTipText = title.Length > 60 ? title[..60] : title;
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---- developer aid: --screenshots <dir> [--theme dark] ------------------------------

    static void Screenshots(string dir, string size)
    {
        Directory.CreateDirectory(dir);
        main.Width = 1240;
        main.Height = 1640;
        if (size?.Split('x') is [string w, string h] && int.TryParse(w, out int width) && int.TryParse(h, out int height))
        {
            main.Width = width;
            main.Height = height;
        }
        main.ShowActivated = false;
        main.ShowInTaskbar = false;
        main.Position = new PixelPoint(-32000, 0);
        main.Show();
        var onboard = new Window { Width = 760, Height = 980, Background = Neu.B(Neu.P.Bg), ShowActivated = false, ShowInTaskbar = false, Position = new PixelPoint(-32000, 0) };
        onboard.Content = new Border { Background = Neu.B(Neu.P.Bg), Padding = new Thickness(40), Child = new OnboardingView(() => { }) };
        onboard.Show();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Demo.On ? 3 : 16) };   // first transcript scan + re-render
        t.Tick += (_, _) =>
        {
            t.Stop();
            Save(main, Path.Combine(dir, "dashboard-" + Neu.P.Name + ".png"));
            Save(onboard, Path.Combine(dir, "onboarding-" + Neu.P.Name + ".png"));
            SavePage(Path.Combine(dir, "dashboard-" + Neu.P.Name + "-full.png"));
            // The window can't be taller than the screen, so also step down the
            // page a screen at a time (frames for videos), ending at the bottom.
            double step = Math.Max(200, dashboard.ViewHeight * 0.8), end = Math.Max(0, dashboard.PageHeight - dashboard.ViewHeight);
            int frame = 1;
            double y = 0;
            var after = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            after.Tick += (_, _) =>
            {
                if (frame > 1) Save(main, Path.Combine(dir, "dashboard-" + Neu.P.Name + "-" + (frame - 1) + ".png"));
                if (y >= end)
                {
                    after.Stop();
                    Save(main, Path.Combine(dir, "dashboard-" + Neu.P.Name + "-lower.png"));
                    // Then each sidebar page.
                    var pages = new Queue<string>(new[] { "sessions", "linked", "memory", "updates", "backup", "settings" });
                    var next = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    string shown = null;
                    next.Tick += (_, _) =>
                    {
                        if (shown != null) Save(main, Path.Combine(dir, "page-" + shown + "-" + Neu.P.Name + ".png"));
                        if (pages.Count == 0) { next.Stop(); Exit(); return; }
                        shown = pages.Dequeue();
                        dashboard.Show(shown);
                    };
                    next.Start();
                    return;
                }
                y = Math.Min(end, y + step);
                dashboard.ScrollTo(y);
                frame++;
            };
            after.Start();
        };
        t.Start();
    }

    // The whole dashboard at once, however long, on the page colour.
    static void SavePage(string path)
    {
        if (dashboard.Page is not Panel page || page.Bounds.Height < 1) return;
        IBrush before = page.Background;
        page.Background = Neu.B(Neu.P.Bg);
        var size = new PixelSize((int)page.Bounds.Width, (int)page.Bounds.Height);
        using (var rtb = new RenderTargetBitmap(size, new Vector(96, 96)))
        {
            rtb.Render(page);
            rtb.Save(path);
        }
        page.Background = before;
    }

    static void Save(Window w, string path)
    {
        // Render the window's content at 1:1 (rendering the Window itself, or at
        // a different DPI, mixes scales in Avalonia 11).
        var root = (Control)w.Content;
        var size = new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height);
        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        rtb.Render(root);
        rtb.Save(path);
    }
}

// One app per user: a second launch asks the first to show its window.
public static class SingleInstance
{
    static Mutex mutex;
    static string PipeName => "claude-switch-" + Environment.UserName;

    public static bool TryBecomePrimary(string message)
    {
        mutex = new Mutex(true, "Local\\ClaudeSwitch_App", out bool created);
        if (created) return true;
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(2000);
            using var w = new StreamWriter(c);
            w.WriteLine(message);
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
        return false;
    }

    public static void Listen(Action<string> onMessage)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var r = new StreamReader(server);
                    string line = r.ReadLine();
                    if (line != null) onMessage(line.Trim());
                }
                catch (Exception ex) { ErrorLog.Write(ex); Thread.Sleep(1000); }
            }
        }) { IsBackground = true, Name = "single-instance" };
        t.Start();
    }
}
