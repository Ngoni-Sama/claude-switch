// The main view: profile cards with usage + resets, banners (wrong-window
// sign-ins, recoverable history, limits), Code-tab sessions with "Continue in",
// backup, sign-in links, support.

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeSwitch.Core;
using System.Globalization;

namespace ClaudeSwitch.Ui;

public class DashboardView : Border
{
    readonly ScrollViewer scroller;
    readonly StackPanel content;
    readonly DispatcherTimer timer;
    const int PageSize = 8;            // sessions shown at once, newest first
    Snapshot snap;
    string selected;
    string page = "overview";
    readonly Border sidebar, headerBar;
    readonly StackPanel nav, headTools;
    readonly TextBlock headTitle;
    bool collapsed;    // sidebar shows icons only
    int sessionPage;
    bool loading;

    public event Action Loaded2;   // first data shown (used by screenshots)
    public Snapshot Data => snap;

    public DashboardView()
    {
        Background = Neu.PageBrush;
        content = new StackPanel { MaxWidth = 1240, HorizontalAlignment = HorizontalAlignment.Stretch };
        scroller = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        nav = new StackPanel { Spacing = 2 };
        sidebar = new Border { Child = nav, ClipToBounds = false };
        headTitle = new TextBlock { FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        headTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var headGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headGrid.Children.Add(headTitle);
        Grid.SetColumn(headTools, 1);
        headGrid.Children.Add(headTools);
        headerBar = new Border { Child = headGrid };
        var main = new DockPanel();
        DockPanel.SetDock(headerBar, Dock.Top);
        main.Children.Add(headerBar);
        main.Children.Add(scroller);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(main, 1);
        layout.Children.Add(main);
        layout.Children.Add(sidebar);   // last, so the open pill's curves draw over the page edge
        Child = layout;
        ApplyDensity();
        // Laptops and windows that aren't maximised: tighter spacing, then an icon-only sidebar.
        SizeChanged += (_, _) => { if (ApplyDensity() && snap != null) Build(); };
        content.Children.Add(Neu.T("Loading…", 14, Neu.P.Muted));
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) => Reload(false);
        AttachedToVisualTree += (_, _) => { Reload(true); timer.Start(); };
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }

    Window Owner => TopLevel.GetTopLevel(this) as Window;

    // True when the layout mode changed and the page needs rebuilding.
    bool ApplyDensity()
    {
        double w = Bounds.Width, h = Bounds.Height;
        bool dense = w > 0 && (w < 1180 || h < 780);
        bool narrow = w > 0 && w < 900;
        bool changed = dense != Neu.Dense || narrow != collapsed;
        Neu.Dense = dense;
        collapsed = narrow;
        content.Margin = dense ? new Thickness(20, 16, 20, 22) : new Thickness(32, 24, 32, 32);
        content.Spacing = dense ? 12 : 18;
        sidebar.Width = collapsed ? 72 : dense ? 222 : 240;
        sidebar.Padding = collapsed ? new Thickness(12, 16, 0, 12) : new Thickness(14, dense ? 14 : 20, 0, 14);
        headerBar.Height = dense ? 52 : 64;
        headerBar.Padding = new Thickness(dense ? 20 : 32, 0, dense ? 16 : 28, 0);
        headTitle.FontSize = dense ? 18 : 21;
        return changed;
    }

    public void Reload(bool force)
    {
        if (loading) return;
        loading = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Snapshot s = null;
            try { s = Snapshot.Load(true, () => Dispatcher.UIThread.Post(() => Reload(true))); }
            catch (Exception ex) { ErrorLog.Write(ex); }
            Dispatcher.UIThread.Post(() =>
            {
                loading = false;
                if (s == null) return;
                if (force || snap == null || s.Signature != snap.Signature)
                {
                    bool first = snap == null;
                    snap = s;
                    Build();
                    if (first) Loaded2?.Invoke();
                }
                // Unified profiles: keep every account's session list in step.
                if (Demo.On) return;   // made-up data: never act on it
                if (s.Unify.Healthy) Unify.MirrorInBackground(s.Profiles, s.Accounts, _ => Dispatcher.UIThread.Post(() => Reload(true)));
                // Automatic mode: open profiles that are behind so Claude's own
                // updater runs in them. Once an hour at most.
                if (Updater.Mode == UpdateMode.Automatic && s.Updates.ProfilesBehind.Count > 0 &&
                    (DateTime.Now - Settings.UpdateCheckedAt).TotalHours > 1)
                {
                    Settings.UpdateCheckedAt = DateTime.Now;
                    ThreadPool.QueueUserWorkItem(_ => { try { Updater.AutoRefresh(s.Updates, s.Profiles); } catch (Exception ex) { ErrorLog.Write(ex); } });
                }
            });
        });
    }

    void Later(Action work, int refreshAfterMs)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { work(); } catch (Exception ex) { ErrorLog.Write(ex); }
            if (refreshAfterMs > 0) Thread.Sleep(refreshAfterMs);
            Dispatcher.UIThread.Post(() => Reload(true));
        });
    }

    static string Describe(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => "no other profile",
            1 => list[0],
            2 => list[0] + " and " + list[1],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    static NeuButton Btn(string text, bool primary, Action click, double size = 13.5)
    {
        var b = new NeuButton(text, primary, size);
        b.Click += click;
        return b;
    }

    // ---- layout ---------------------------------------------------------------------

    void Build()
    {
        Background = Neu.PageBrush;
        Vector offset = scroller.Offset;
        content.Children.Clear();
        bool onboarding = snap.Profiles.Count == 0;
        sidebar.IsVisible = headerBar.IsVisible = !onboarding;
        headerBar.Background = Neu.B(Neu.P.Card);
        headerBar.BoxShadow = Neu.P.IsDark ? default : BoxShadows.Parse("0 1 0 0 #14000000");
        headTools.Children.Clear();
        if (onboarding) { content.Children.Add(new OnboardingView(() => Reload(true))); return; }
        BuildNav();

        switch (page)
        {
            case "sessions":
                PageHeader("Sessions", "Code-tab conversations of each signed-in account. Continue one in another profile to finish it there; the original stays where it is.");
                BuildSessions();
                break;
            case "linked":
                PageHeader("Linked computers", "Send profiles to your other computers over the internet, wherever they are. End-to-end encrypted; sign-ins never travel.");
                BuildLinkBanners(transfers: false);   // the Transfers card below lists those
                var linkGrid = new Masonry { MinColumnWidth = 400, MaxColumns = 2 };
                linkGrid.Children.Add(LinkUi.Transfers(Owner, () => Reload(true)));
                linkGrid.Children.Add(LinkUi.Panel(snap, Owner, () => Reload(true)));
                content.Children.Add(linkGrid);
                break;
            case "memory":
                PageHeader("Shared memory", "Let accounts share one memory and one set of conversations, so one can carry on where another ran out.");
                content.Children.Add(UnifyPanel.Build(snap, Owner, () => Reload(true), Later));
                break;
            case "updates":
                PageHeader("Updates", "Keep Claude current in every profile, and clear out old copies that stopped updating.");
                content.Children.Add(UpdatePanel.Build(snap, Owner, () => Reload(true), Later));
                break;
            case "backup":
                PageHeader("Backup & move", "Export profiles to a file for a new computer, or send them straight to a linked one.");
                BuildBackupPage();
                break;
            case "terminal":
                PageHeader("Terminal", "Run Claude Code as any profile from a terminal.");
                content.Children.Add(ExtrasPanels.Terminal_(snap, Owner, () => Reload(true)));
                break;
            case "settings":
                PageHeader("Settings", null);
                content.Children.Add(ExtrasPanels.Settings_(snap, Owner, () => Reload(true)));
                if (ExtrasPanels.RelayPanel(snap) is Control relay) content.Children.Add(relay);
                break;
            case "support":
                PageHeader("Support", null);
                BuildSupportPage();
                break;
            default:
                PageHeader("Overview", "Each profile is its own Claude window and account. When one runs out of usage, hand a session to another and keep going.",
                    Btn("+ Add profile", true, () => _ = AddProfile()), Btn("Refresh", false, () => Reload(true)));
                BuildSummary();
                if (!snap.ClaudeInstalled) content.Children.Add(Banner(Neu.P.Warn, "Claude Desktop isn't installed", InstallHint(),
                    Btn("Get Claude", true, () => Links.Open(Platform.IsLinux ? Links.ClaudeLinux : Links.ClaudeDownload))));
                BuildBanners();
                BuildCards();
                break;
        }
        Dispatcher.UIThread.Post(() => scroller.Offset = offset, DispatcherPriority.Loaded);
    }

    public void Show(string name)
    {
        if (page == name) return;
        page = name;
        scroller.Offset = default;
        if (snap != null) Build();
    }

    // ---- sidebar ------------------------------------------------------------------------

    static readonly (string key, string label, string glyph)[] Pages =
    {
        ("overview", "Overview", "M2,2 h5 v5 h-5 z M9,2 h5 v5 h-5 z M2,9 h5 v5 h-5 z M9,9 h5 v5 h-5 z"),
        ("sessions", "Sessions", "M2,3 h12 v8 h-6 l-3,3 v-3 h-3 z"),
        ("linked", "Linked computers", "M3,3.5 h10 v7 h-10 z M1,13 h14"),
        ("memory", "Shared memory", "M8,2 l6,3 l-6,3 l-6,-3 z M2,8 l6,3 l6,-3 M2,11 l6,3 l6,-3"),
        ("updates", "Updates", "M13,8 a5,5 0 1 1 -1.6,-3.7 M13,2 v3 h-3"),
        ("backup", "Backup & move", "M2,3 h12 v3 h-12 z M3,6 v8 h10 v-8 M6,9 h4"),
        ("terminal", "Terminal", "M2,3 h12 v10 h-12 z M5,6.5 l2,1.5 l-2,1.5 M8.5,10 h3"),
        ("settings", "Settings", "M8,5.5 a2.5,2.5 0 1 0 0.01,0 M8,1 v2 M8,13 v2 M1,8 h2 M13,8 h2 M3,3 l1.4,1.4 M11.6,11.6 l1.4,1.4 M3,13 l1.4,-1.4 M11.6,4.4 l1.4,-1.4"),
        ("support", "Support", "M8,13.5 l-5.2,-5.2 a3,3 0 0 1 5.2,-4 a3,3 0 0 1 5.2,4 z"),
    };

    static Color White(byte alpha) => Color.FromArgb(alpha, 255, 255, 255);

    void BuildNav()
    {
        sidebar.Background = Neu.SidebarBrush;
        nav.Children.Clear();

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(collapsed ? 6 : 6, 0, 14, Neu.Dense ? 14 : 22) };
        double logo = collapsed ? 34 : 30;
        brand.Children.Add(new Border
        {
            Background = Neu.B(White(0xF2)), CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Width = logo + 6, Height = logo + 6,
            Child = new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClaudeSwitch/Assets/icon.png"))) },
        });
        if (!collapsed)
        {
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Neu.T("Claude Switch", 15, Neu.P.NavText, FontWeight.SemiBold));
            names.Children.Add(Neu.T(snap.Profiles.Count + (snap.Profiles.Count == 1 ? " profile" : " profiles"), 11.5, White(0xB8)));
            brand.Children.Add(names);
        }
        nav.Children.Add(brand);

        int inbox = (Demo.On ? Demo.LinkInbox : Link.Inbox).Count;
        foreach (var (key, label, glyph) in Pages)
        {
            string badge = key switch
            {
                "linked" when inbox > 0 => inbox.ToString(),
                "updates" when Updater.Mode != UpdateMode.Off && snap.Updates.AnythingBehind => "1",
                "overview" when snap.Profiles.Any(p => snap.Accounts[p.Name].AtLimit) => "!",
                _ => null,
            };
            nav.Children.Add(NavItem(key, label, glyph, badge));
        }

        if (collapsed) return;
        var ver = Neu.T("Version " + typeof(DashboardView).Assembly.GetName().Version.ToString(3) + " · by Ngoni Maphosa. Unofficial; not affiliated with Anthropic.", 11, White(0x9C), wrap: true);
        ver.Margin = new Thickness(8, 22, 18, 0);
        nav.Children.Add(ver);
    }

    // The current page is an open pill: it runs to the sidebar's edge in the page's own
    // colour, with curves above and below so it flows into the page.
    Control NavItem(string key, string label, string glyph, string badge)
    {
        bool on = page == key;
        Color fg = on ? Neu.P.Accent : Neu.P.NavText;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(glyph), Stroke = Neu.B(on ? Neu.P.Accent : White(0xD8)), StrokeThickness = on ? 1.7 : 1.4,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 16, Height = 16, Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(collapsed ? "*" : "Auto,*,Auto") };
        row.Children.Add(icon);
        if (!collapsed)
        {
            var text = Neu.T(label, Neu.Dense ? 13 : 13.5, fg, on ? FontWeight.SemiBold : FontWeight.Normal);
            text.Margin = new Thickness(12, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
        }
        else ToolTip.SetTip(row, label);
        if (badge != null)
        {
            var pill = new Border
            {
                Background = Neu.B(on ? Neu.P.Accent : Color.FromRgb(0xFF, 0x5C, 0x8A)), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(collapsed ? 4 : 7, 1), MinWidth = collapsed ? 14 : 20, VerticalAlignment = VerticalAlignment.Center,
                Child = Neu.T(badge, collapsed ? 9.5 : 11, Colors.White, FontWeight.SemiBold),
            };
            ((TextBlock)pill.Child).HorizontalAlignment = HorizontalAlignment.Center;
            if (collapsed)
            {
                pill.HorizontalAlignment = HorizontalAlignment.Right;
                pill.VerticalAlignment = VerticalAlignment.Top;
                pill.Margin = new Thickness(0, -6, 2, 0);
            }
            else Grid.SetColumn(pill, 2);
            row.Children.Add(pill);
        }
        double padY = Neu.Dense ? 9 : 11;
        var item = new Border
        {
            Child = row, Padding = new Thickness(collapsed ? 0 : 14, padY, collapsed ? 12 : 16, padY), Focusable = true,
            CornerRadius = on ? new CornerRadius(22, 0, 0, 22) : new CornerRadius(12),
            Margin = on ? default : new Thickness(0, 0, collapsed ? 12 : 14, 0),
            Background = on ? Neu.B(Neu.P.Bg) : Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        AutomationProperties.SetName(item, label + (on ? ", current page" : "") + (badge != null ? ", " + badge + " new" : ""));
        if (!on)
        {
            item.PointerEntered += (_, _) => item.Background = Neu.B(White(0x1F));
            item.PointerExited += (_, _) => item.Background = Brushes.Transparent;
        }
        item.PointerPressed += (_, e) => { e.Handled = true; Show(key); };
        item.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; Show(key); } };
        if (!on) return item;

        // Concave corners in the page colour, just outside the pill's top and bottom right.
        const double r = 20;
        IBrush pageBg = Neu.B(Neu.P.Bg);
        var above = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M20,0 L20,20 L0,20 A20,20 0 0 0 20,0 Z"), Fill = pageBg, Width = r, Height = r,
            HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false,
        };
        var below = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M20,0 L0,0 A20,20 0 0 1 20,20 Z"), Fill = pageBg, Width = r, Height = r,
            HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false,
        };
        var open = new Grid { RowDefinitions = new RowDefinitions("20,Auto,20"), Margin = new Thickness(0, -r, 0, -r), ZIndex = 1 };
        open.Children.Add(above);
        Grid.SetRow(item, 1);
        open.Children.Add(item);
        Grid.SetRow(below, 2);
        open.Children.Add(below);
        return open;
    }

    // Page title and its buttons go in the white bar along the top; the description
    // stays on the page.
    void PageHeader(string title, string subtitle, params Control[] tools)
    {
        headTitle.Text = title;
        headTitle.Foreground = Neu.B(Neu.P.Text);
        foreach (Control t in tools) headTools.Children.Add(t);

        var themes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(tools.Length > 0 ? 8 : 0, 0, 0, 0) };
        foreach (string theme in new[] { "Light", "Dark" })
        {
            string n = theme.ToLowerInvariant();
            var t = new NeuButton(theme, false, 12) { Selected = Neu.P.Name == n, MinWidth = Neu.Dense ? 56 : 66 };
            AutomationProperties.SetName(t, theme + " theme");
            t.Click += () => { if (Neu.P.Name != n) Neu.Switch(n); };
            themes.Children.Add(t);
        }
        headTools.Children.Add(themes);

        if (subtitle == null) return;
        var sub = Neu.Para(subtitle, Neu.Dense ? 13 : 14);
        sub.Margin = new Thickness(2, 0, 24, Neu.Dense ? 0 : 2);
        content.Children.Add(sub);
    }

    static string InstallHint() => Platform.IsLinux
        ? "Claude Switch opens Claude Desktop with a separate folder per profile. On Ubuntu/Debian install it with: sudo apt install claude-desktop (after adding Anthropic's apt repository)."
        : "Claude Switch opens Claude Desktop with a separate folder per profile. Install Claude from claude.ai/download, then come back.";

    static readonly (Color tint, string glyph)[] StatIcons =
    {
        (Color.FromRgb(0xFF, 0x6B, 0x9A), "M8,7 a2.6,2.6 0 1 0 0.01,0 M3,14 a5,4 0 0 1 10,0"),   // pink: profiles
        (Color.FromRgb(0x8B, 0x5C, 0xF6), "M2,10 a6,6 0 0 1 12,0 M8,10 l2.5,-3"),             // violet: room left
        (Color.FromRgb(0xF5, 0xB0, 0x0B), "M2,3 h12 v8 h-6 l-3,3 v-3 h-3 z"),                  // amber: sessions
        (Color.FromRgb(0x22, 0xC5, 0x5E), "M3,8.5 l3,3 l7,-7"),                                // green: Claude
    };

    // Strip of big numbers: profiles, room left, sessions, Claude.
    void BuildSummary()
    {
        var strip = new Masonry { Rows = true, MinColumnWidth = Neu.Dense ? 168 : 210, MaxColumns = 4 };
        int n = 0;
        Control Tile(string label, Control stat, string note, Color? noteColor = null)
        {
            // Coloured icon square beside each number.
            var (tint, glyph) = StatIcons[n++ % StatIcons.Length];
            double box = Neu.Dense ? 40 : 48;
            var icon = new Border
            {
                Width = box, Height = box, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top,
                Background = Neu.Gradient(tint, Neu.Mix(tint, Colors.Black, 0.12)),
                BoxShadow = Neu.P.IsDark ? default : BoxShadows.Parse("0 4 10 0 #26000000"),
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(glyph), Stroke = Brushes.White, StrokeThickness = 1.6, Width = 16, Height = 16,
                    StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = new ScaleTransform(Neu.Dense ? 1.15 : 1.35, Neu.Dense ? 1.15 : 1.35),
                },
            };
            var s = new StackPanel { Spacing = 3, Margin = new Thickness(14, 0, 0, 0) };
            s.Children.Add(Neu.T(label, 12.5, Neu.P.Muted, FontWeight.SemiBold));
            s.Children.Add(stat);
            if (note != null) s.Children.Add(Neu.T(note, 12, noteColor ?? Neu.P.Muted, wrap: true));
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            g.Children.Add(icon);
            Grid.SetColumn(s, 1);
            g.Children.Add(s);
            return Neu.Tile(g, Neu.Dense ? new Thickness(14, 12, 14, 12) : new Thickness(18, 16, 18, 16));
        }

        int open = snap.Profiles.Count(p => snap.Pid(p) != 0);
        int signedOut = snap.Profiles.Count(p => !snap.Accounts[p.Name].SignedIn);
        strip.Children.Add(Tile("Profiles", Neu.Stat(snap.Profiles.Count.ToString(), snap.Profiles.Count == 1 ? "profile" : "profiles"),
            open + " open" + (signedOut > 0 ? ", " + signedOut + " not signed in" : "")));

        // The account with the most session left is where to go next.
        var best = snap.Profiles.Where(p => snap.Accounts[p.Name].SignedIn && (snap.Accounts[p.Name].Session?.Shown ?? -1) >= 0)
            .OrderBy(p => snap.Accounts[p.Name].Session.Shown).FirstOrDefault();
        if (best != null)
        {
            int left = 100 - snap.Accounts[best.Name].Session.Shown;
            strip.Children.Add(Tile("Most room", Neu.Stat(left + "%", "left in " + best.Name, Neu.UsageColor(100 - left)),
                snap.Profiles.Count(p => snap.Accounts[p.Name].AtLimit) is int lim && lim > 0 ? lim + " at the limit" : "No account at its limit",
                snap.Profiles.Any(p => snap.Accounts[p.Name].AtLimit) ? Neu.P.Bad : Neu.P.Good));
        }
        else strip.Children.Add(Tile("Most room", Neu.Stat("–", "no readings yet"), "Copy a usage report to fill this in"));

        int sessions = snap.Sessions.Values.Sum(l => l.Count(x => !x.Archived));
        strip.Children.Add(Tile("Sessions", Neu.Stat(sessions.ToString(), sessions == 1 ? "Code session" : "Code sessions"),
            snap.Unify.Healthy ? "Shared by " + Describe(snap.Unify.Members) : "across " + snap.Profiles.Count(p => snap.Sessions[p.Name].Count > 0) + " accounts"));

        UpdateStatus u = snap.Updates;
        strip.Children.Add(Tile("Claude", Neu.Stat(u.Primary?.Version ?? "–", u.Primary == null ? "not installed" : "installed"),
            u.Installs.Count == 0 ? "Get it from claude.ai/download" : u.AnythingBehind ? "Something's behind" : "OK, up to date",
            u.Installs.Count == 0 ? Neu.P.Muted : u.AnythingBehind ? Neu.P.Warn : Neu.P.Good));
        content.Children.Add(strip);
    }

    Control Banner(Color stripe, string title, string text, params Control[] buttons)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("5,*") };
        g.Children.Add(new Border { Background = Neu.B(stripe), CornerRadius = new CornerRadius(3) });
        var body = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        body.Children.Add(Neu.T(title, 15, null, FontWeight.SemiBold, wrap: true));
        var p = Neu.Para(text);
        p.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(p);
        if (buttons.Length > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (Control b in buttons) { b.Margin = new Thickness(0, 4, 14, 0); row.Children.Add(b); }
            body.Children.Add(row);
        }
        Grid.SetColumn(body, 1);
        g.Children.Add(body);
        return Neu.Tile(g, new Thickness(16, 14, 20, 14));
    }

    void BuildBanners()
    {
        BuildAccountBanners();
        BuildLinkBanners();
        BuildOtherBanners();
    }

    // Profiles with no account, and two profiles on one account: each needs its own.
    void BuildAccountBanners()
    {
        foreach (ProfileEntry p in snap.Profiles.Where(p => !snap.Accounts[p.Name].SignedIn))
            content.Children.Add(Banner(Neu.P.Warn, p.Name + " has no account yet",
                "Every profile needs its own Claude account. Sign in now, or remove it if you don't need it.",
                Btn("Sign in to " + p.Name, true, () => _ = SignInWindow.Show(Owner, p, isNew: false, () => Reload(true))),
                Btn("Remove", false, () => _ = RemoveProfile(p))));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProfileEntry p in snap.Profiles)
        {
            AccountInfo a = snap.Accounts[p.Name];
            if (!a.SignedIn || a.Uuid == null || !seen.Add(a.Uuid)) continue;
            var same = snap.Profiles.Where(q => q != p && snap.Accounts[q.Name].SignedIn &&
                                                string.Equals(snap.Accounts[q.Name].Uuid, a.Uuid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (same.Count == 0) continue;
            ProfileEntry q = same[^1];
            content.Children.Add(Banner(Neu.P.Bad, p.Name + " and " + Describe(same.Select(x => x.Name)) + " use the same account",
                (a.Email ?? "One account") + " is signed in to both, so they share one usage limit. Sign " + q.Name + " in to a different account.",
                Btn("Sign " + q.Name + " in again", true, () => _ = SignInWindow.Show(Owner, q, isNew: false, () => Reload(true)))));
        }
    }

    void BuildLinkBanners(bool transfers = true)
    {
        // Linked computers: transfers waiting here, and requests to link.
        foreach (LinkMessage m in Demo.On ? Demo.LinkInbox : Link.Inbox)
        {
            LinkMessage msg = m;
            if (m.Kind != "link" && !transfers) continue;
            if (m.Kind != "link" && !m.Complete)
            {
                int pct = m.Parts > 0 ? m.Received * 100 / m.Parts : 0;
                content.Children.Add(Banner(Neu.P.Accent, m.Name + " is sending you " + Describe(m.Profiles) + "  " + m.ShortId,
                    "Uploading: " + pct + "% of " + (m.Size / 1024.0 / 1024.0).ToString("0.0") + " MB" +
                    (m.UploadRate > 0 ? " at " + (m.UploadRate / 1024 / 1024).ToString("0.00") + " MB/s" : "") + ". You can receive it as soon as it's all there.",
                    Btn("See transfers", false, () => Show("linked"), 12.5)));
                continue;
            }
            if (m.Kind == "link")
                content.Children.Add(Banner(Neu.P.Accent, m.Name + " wants to link with this computer",
                    "Its address is " + LinkCrypto.Format(m.From) + ". Accept if that's your computer and the address matches what it shows; then either one can send the other profiles.",
                    Btn("Accept", true, () => _ = LinkUi.AcceptNow(Owner, msg, () => Reload(true))),
                    Btn("Decline", false, async () => { try { await Link.Decline(msg); } catch (LinkException) { } Reload(true); }),
                    Btn("Copy its address", false, () => _ = LinkUi.Copy(Owner, msg.From), 12.5)));
            else
                content.Children.Add(Banner(Neu.P.Accent, m.Name + " sent you " + Describe(m.Profiles) + (m.Profiles.Count == 1 ? "" : " profiles") + "  " + m.ShortId,
                    (m.Size / 1024.0 / 1024.0).ToString("0.0") + " MB, sent " + Accounts.Ago(m.Created) + ", end-to-end encrypted. Receive imports it straight away; " +
                    "nothing on this computer is overwritten. It waits until " + m.Expires.ToString("ddd d MMM", CultureInfo.InvariantCulture) + ".",
                    Btn(m.Complete ? "Receive" : "Still uploading…", true, () => { if (msg.Complete) _ = LinkUi.Receive(Owner, msg, () => Reload(true)); }),
                    Btn("Decline", false, async () => { try { await Link.Decline(msg); } catch (LinkException) { } Reload(true); })));
        }
    }

    void BuildOtherBanners()
    {
        // Claude out of date, or a second forgotten copy of it.
        if (Updater.Mode != UpdateMode.Off && snap.Updates.AnythingBehind)
        {
            UpdateStatus u = snap.Updates;
            string title, what;
            Control fix;
            if (u.OnOldApp.Count > 0)
            {
                // The common one: a window open on a second, stale copy of Claude.
                title = Describe(u.OnOldApp) + (u.OnOldApp.Count == 1 ? " is" : " are") + " running an old Claude";
                what = "That window came from " + (u.RunningOn[u.OnOldApp[0]]?.Describe ?? "an older copy") + ", while the current one is " +
                       (u.Primary?.Version ?? "newer") + ". Current models need the current app. Nothing needs downloading: both read the same " +
                       "profile folder, so reopening brings every chat and the sign-in across.";
                fix = Btn("Move to the current Claude", true, () => _ = ShowUpdates());
            }
            else if (u.OldCopies.Count > 0 && Platform.IsWindows)
            {
                var old = u.OldCopies;
                title = old.Count == 1 ? "An old copy of Claude is still installed" : old.Count + " old copies of Claude are still installed";
                what = "Besides the current " + (u.Primary?.Version ?? "Claude") + ", you have " + string.Join(" and ", old.Select(o => o.Version)) +
                       (old.Count == 1
                           ? ". It's stopped updating" + (old[0].UpdaterMissing ? " (it has lost its updater entirely)" : "") + ", so opening it by mistake gives you an old Claude."
                           : ". They've stopped updating" + (old.Any(o => o.UpdaterMissing) ? " (one has lost its updater entirely)" : "") + ", so opening one by mistake gives you an old Claude.");
                fix = Btn(old.Count == 1 ? "Remove the old copy" : "Remove the old copies", true, () => _ = ShowUpdates());
            }
            else
            {
                title = "Claude Code is behind in " + Describe(u.ProfilesBehind);
                what = "A profile only refreshes its Claude Code while its window is open.";
                fix = Btn("Open " + Describe(u.ProfilesBehind) + " to update", true, () => Later(() =>
                {
                    foreach (string n in u.ProfilesBehind)
                        if (Profiles.Find(snap.Profiles, n) is ProfileEntry p) { Updater.RefreshProfile(p); Thread.Sleep(2000); }
                }, 8000));
            }
            content.Children.Add(Banner(Neu.P.Warn, title, what, fix));
        }

        // Claude-* folders made outside Claude Switch (aliases, scripts, the old launcher).
        foreach (ProfileOps.Found f in snap.Unlisted)
        {
            ProfileOps.Found found = f;
            content.Children.Add(Banner(Neu.P.Accent, "Found a Claude profile folder that isn't listed: " + Path.GetFileName(Platform.Normal(f.DataDir)),
                "It's at " + f.Source + (f.ConfigDir != null ? ", with Claude Code data in " + Platform.Shorten(Platform.Normal(f.ConfigDir)) : "") +
                ". Add it to manage it here; nothing in it changes.",
                Btn("Add it as " + f.Name, true, () => { ProfileOps.Adopt(found); Reload(true); }),
                Btn("Ignore", false, () => { ProfileOps.Dismiss(found); Reload(true); })));
        }

        var handled = new HashSet<ProfileEntry>();
        foreach (ProfileEntry p in snap.Profiles)
        {
            AccountInfo a = snap.Accounts[p.Name];
            if (handled.Contains(p) || !Transfer.AccountChanged(p, a)) continue;
            ProfileEntry q = Transfer.SwapPartner(p, snap.Profiles, snap.Accounts);
            string now = a.Email ?? "a different account", was = p.Email ?? "another account";
            if (q != null)
            {
                handled.Add(q);
                content.Children.Add(Banner(Neu.P.Warn, p.Name + " and " + q.Name + " have swapped accounts",
                    p.Name + " is signed in as " + now + " and " + q.Name + " as " + (snap.Accounts[q.Name].Email ?? "the other account") +
                    ". This happens when a sign-in link opens in the wrong window. Swap names makes each name point at its account again; nobody has to sign in again.",
                    Btn("Swap names", true, () => _ = SwapNames(p, q))));
            }
            else
            {
                content.Children.Add(Banner(Neu.P.Warn, p.Name + " is now signed in as " + now,
                    "It used to be " + was + ". If a sign-in link opened in the wrong window, sign out in " + p.Name + " and sign in again with the right account.",
                    Btn("That's correct now", false, () => { Transfer.Accept(p, snap.Accounts[p.Name]); Save(); Reload(true); })));
            }
        }
        foreach (RecoveryItem item in snap.Recovery)
            content.Children.Add(Banner(Neu.P.Accent, item.Sessions.Count + " sessions of " + (item.Email ?? item.To.Name) + " don't show in " + item.To.Name,
                "They're stored in " + item.From.Name + "'s folder (that window used to be signed in to this account). Copy them to " + item.To.Name +
                " so its Code tab lists them again. Nothing is moved or deleted.",
                Btn("Copy to " + item.To.Name, true, () => _ = Recover(item))));
        foreach (ProfileEntry p in snap.Profiles)
        {
            AccountInfo a = snap.Accounts[p.Name];
            if (!a.AtLimit) continue;
            Bucket b = a.LimitedBucket;
            string limitName = p.Name + " has used " + b.Pct + "% of its " + (b == a.Weekly ? "weekly" : "session") + " limit";

            // Unified profiles share memory, so the other account can just carry on.
            ProfileEntry shared = snap.Unify.Healthy && snap.Unify.Members.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                ? Unify.BestAlternative(p, snap.Profiles, snap.Accounts) : null;
            if (shared != null)
            {
                content.Children.Add(Banner(Neu.P.Bad, limitName,
                    b.ResetText + ". " + shared.Name + " shares this memory, so you can carry on there from where you left off: your projects, " +
                    "notes and conversations are already the same.",
                    Btn("Carry on in " + shared.Name + "  →", true, () => _ = CarryOn(p, shared))));
                continue;
            }
            ProfileEntry other = snap.Profiles.FirstOrDefault(q => q != p && snap.Accounts[q.Name].SignedIn && !snap.Accounts[q.Name].AtLimit);
            content.Children.Add(Banner(Neu.P.Bad, limitName,
                b.ResetText + ". " + (other != null ? "To keep working now, pick the session below and choose Continue in " + other.Name + ", or turn on shared memory so both accounts work as one."
                                                    : "Sign in to another profile to hand sessions over when this happens.")));
        }
    }

    void BuildCards()
    {
        var grid = new Masonry { Rows = true, MinColumnWidth = 340, MaxColumns = 4 };
        foreach (ProfileEntry p in snap.Profiles) grid.Children.Add(Card(p));
        content.Children.Add(grid);
    }

    Control Card(ProfileEntry p)
    {
        AccountInfo a = snap.Accounts[p.Name];
        int pid = snap.Pid(p);
        var s = new StackPanel();

        // The profile's colour, as a tab across the top of its tile.
        string hex = Profiles.ColorOf(snap.Profiles, p);
        var tab = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Neu.B(Color.Parse(hex)), Margin = new Thickness(0, 0, 0, 14) };
        ToolTip.SetTip(tab, p.Name + "'s colour (change it under More)");
        s.Children.Add(tab);

        var top = new DockPanel();
        string state = !a.SignedIn ? "Not signed in" : pid != 0 ? "Open" : "Closed";
        Color stateColor = !a.SignedIn ? Neu.P.Warn : pid != 0 ? Neu.P.Good : Neu.P.Muted;
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(Neu.Dot(stateColor, state));
        status.Children.Add(Neu.T(state, 13, stateColor, FontWeight.SemiBold));
        DockPanel.SetDock(status, Dock.Right);
        top.Children.Add(status);
        // The name, with a pencil to rename it (double-clicking the name works too).
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var heading = Neu.Heading(p.Name, Neu.Dense ? 17 : 19);
        heading.TextWrapping = TextWrapping.NoWrap;
        heading.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.DoubleTapped += (_, _) => _ = ProfileActions.Rename(Owner, snap, p, () => Reload(true));
        nameRow.Children.Add(heading);
        var pencil = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Focusable = true,
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M2,12 L2,14 L4,14 L12.5,5.5 L10.5,3.5 Z M11.5,2.5 L13.5,4.5"), Stroke = Neu.B(Neu.P.Muted), StrokeThickness = 1.4,
                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 16, Height = 16, Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(pencil, "Rename " + p.Name);
        AutomationProperties.SetName(pencil, "Rename " + p.Name);
        pencil.PointerEntered += (_, _) => pencil.Background = Neu.B(Neu.P.Hover);
        pencil.PointerExited += (_, _) => pencil.Background = Brushes.Transparent;
        pencil.PointerPressed += (_, e) => { e.Handled = true; _ = ProfileActions.Rename(Owner, snap, p, () => Reload(true)); };
        pencil.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; _ = ProfileActions.Rename(Owner, snap, p, () => Reload(true)); } };
        nameRow.Children.Add(pencil);
        top.Children.Add(nameRow);
        s.Children.Add(top);

        var who = Neu.Field("Account", a.SignedIn ? a.Email ?? "Signed in" : "Open it and sign in", a.SignedIn ? null : Neu.P.Muted);
        who.Margin = new Thickness(0, 10, 0, 0);
        s.Children.Add(who);
        var org = Neu.Field(a.OrgName != null ? "Organisation" : "Folder", a.OrgName ?? Platform.Shorten(p.Data), null, 13.5, p.Data);
        org.Margin = new Thickness(0, 8, 0, 12);
        s.Children.Add(org);

        if (snap.Unify.Members.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool linked = snap.Unify.Linked.TryGetValue(p.Name, out bool ok) && ok;
            var tag = Neu.T((linked ? "Shares memory with " : "Sharing needs repair: ") +
                            (linked ? Describe(snap.Unify.Members.Where(m => !string.Equals(m, p.Name, StringComparison.OrdinalIgnoreCase))) : ""),
                            12, linked ? Neu.P.Accent : Neu.P.Warn, FontWeight.SemiBold);
            tag.Margin = new Thickness(0, -4, 0, 12);
            ToolTip.SetTip(tag, "Memory, project notes and conversations are shared with " +
                                Describe(snap.Unify.Members.Where(m => !string.Equals(m, p.Name, StringComparison.OrdinalIgnoreCase))) + ".");
            s.Children.Add(tag);
        }

        // Usage: two rings side by side in an inner panel, extras as thin bars.
        var usage = new StackPanel();
        usage.Children.Add(Neu.T("USAGE", 12, Neu.P.Muted, FontWeight.SemiBold));
        var rings = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 10, 0, 0) };
        Control session = RingCell("Session", a.Session);
        Control weekly = RingCell("Weekly", a.Weekly);
        Grid.SetColumn(weekly, 1);
        rings.Children.Add(session);
        rings.Children.Add(weekly);
        usage.Children.Add(rings);
        foreach (var extra in a.Extra) usage.Children.Add(BucketRow(extra.Key, extra.Value));
        var legend = Neu.Para("0% = all available · 100% = used up", 11.5);
        legend.Margin = new Thickness(0, 10, 0, 0);
        legend.TextAlignment = TextAlignment.Center;
        usage.Children.Add(legend);
        s.Children.Add(Neu.Inset(usage, 6, new Thickness(16, 12, 16, 12)));

        string clock = a.UsageAt == DateTime.MinValue ? "" : " (" + a.UsageAt.ToString("h:mm tt", CultureInfo.InvariantCulture) + ")";
        var source = Neu.Para(a.FromReport ? "From Claude's usage report, " + Accounts.Ago(a.UsageAt) + clock + "."
            : (a.UsageAt == DateTime.MinValue ? "No usage numbers yet." : "From Claude's usage log, " + Accounts.Ago(a.UsageAt) + clock + ".") +
              " For live numbers, copy the usage report in this profile's Claude window.", 11.5);
        source.Margin = new Thickness(0, 10, 0, 14);
        s.Children.Add(source);

        var row = new WrapPanel();
        void Add(Control c) { c.Margin = new Thickness(0, 0, 8, 8); row.Children.Add(c); }
        if (!a.SignedIn) Add(Btn("Sign in", true, () => _ = SignInWindow.Show(Owner, p, isNew: false, () => Reload(true))));
        else Add(Btn(pid != 0 ? "Switch to" : "Open", true, () => { Launcher.Activate(p, snap.Pid(p)); Later(() => { }, 5000); }));
        var paste = Btn("Paste report", false, () => _ = PasteReport(p));
        ToolTip.SetTip(paste, "Copy the usage report in this profile's Claude window, then click here.");
        Add(paste);
        if (pid != 0) Add(Btn("Restart", false, async () =>
        {
            if (await Dialogs.Ask(Owner, "Restart " + p.Name + "?", "Its window closes and reopens. Anything running in it stops.", "Restart", "Cancel") == 0)
                Later(() => Launcher.Restart(p), 6000);
        }));
        NeuButton more = null;
        more = Btn("More…", false, () => ShowMore(p, more), 12.5);
        Avalonia.Automation.AutomationProperties.SetName(more, "More actions for " + p.Name);
        Add(more);

        // Buttons sit at the bottom so a row of tiles lines up.
        var dock = new DockPanel();
        DockPanel.SetDock(row, Dock.Bottom);
        dock.Children.Add(row);
        dock.Children.Add(s);
        return Neu.Tile(dock, new Thickness(20, 16, 20, 10));
    }

    // One ring with its label and reset line under it.
    static Control RingCell(string label, Bucket b)
    {
        int shown = b?.Shown ?? -1;
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, Spacing = 2 };
        var ring = new Ring(shown, Neu.Dense ? 80 : 100, 5, null, label) { HorizontalAlignment = HorizontalAlignment.Center };
        s.Children.Add(ring);
        string reset = b?.ResetText ?? "";
        if (reset.Length > 0)
        {
            var r = Neu.Para(reset, 11.5, b.ResetSince ? Neu.P.Good : Neu.P.Muted);
            r.TextAlignment = TextAlignment.Center;
            r.Margin = new Thickness(4, 6, 4, 0);
            s.Children.Add(r);
        }
        AutomationProperties.SetName(s, label + ", " + (shown < 0 ? "no reading" : shown + " percent used") + ". " + reset);
        return s;
    }

    // An extra bucket from a usage report (e.g. Weekly · Opus): label + %, thin bar, reset line.
    static Control BucketRow(string label, Bucket b)
    {
        var s = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        int shown = b?.Shown ?? -1;
        var head = new DockPanel();
        var v = Neu.T(shown < 0 ? "–" : shown + "%", 13, shown < 0 ? Neu.P.Muted : Neu.UsageColor(shown), FontWeight.SemiBold);
        DockPanel.SetDock(v, Dock.Right);
        head.Children.Add(v);
        head.Children.Add(Neu.T(label, 12.5, null, FontWeight.SemiBold));
        s.Children.Add(head);
        Control bar = Neu.Bar(shown, 10);
        bar.Margin = new Thickness(0, 6, 0, 4);
        s.Children.Add(bar);
        string reset = b?.ResetText ?? "";
        if (reset.Length > 0) s.Children.Add(Neu.Para(reset, 11.5, b.ResetSince ? Neu.P.Good : Neu.P.Muted));
        AutomationProperties.SetName(s, label + ", " + (shown < 0 ? "no reading" : shown + " percent used") + ". " + reset);
        return s;
    }

    void BuildSessions()
    {
        var tile = new StackPanel();

        if (selected == null || Profiles.Find(snap.Profiles, selected) == null)
        {
            selected = snap.Profiles.Where(p => snap.Sessions[p.Name].Count > 0).OrderByDescending(p => snap.Accounts[p.Name].AtLimit).Select(p => p.Name).FirstOrDefault()
                       ?? snap.Profiles.First().Name;
        }
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
        foreach (ProfileEntry p in snap.Profiles)
        {
            string name = p.Name;
            var b = new UnderlineTab(name + "  " + snap.Sessions[name].Count(x => !x.Archived), string.Equals(name, selected, StringComparison.OrdinalIgnoreCase))
            { Margin = new Thickness(0, 0, 24, 0) };
            b.Click += () => { selected = name; sessionPage = 0; Build(); };   // a new profile starts at its newest
            tabs.Children.Add(b);
        }
        tile.Children.Add(tabs);

        ProfileEntry from = Profiles.Find(snap.Profiles, selected);
        var sessions = (from == null ? new List<SessionInfo>() : snap.Sessions[from.Name]).Where(s => !s.Archived).ToList();

        // Newest first, a page at a time. Clamp in case the list shrank since
        // the last refresh (a session archived, or the profile switched).
        int pages = Math.Max(1, (sessions.Count + PageSize - 1) / PageSize);
        sessionPage = Math.Clamp(sessionPage, 0, pages - 1);
        int first = sessionPage * PageSize;
        var page = sessions.Skip(first).Take(PageSize).ToList();

        var list = new StackPanel();
        foreach (SessionInfo s in page)
        {
            if (list.Children.Count > 0) list.Children.Add(Neu.Line());
            list.Children.Add(SessionRow(s));
        }
        if (sessions.Count == 0)
            list.Children.Add(Neu.Para(from != null && !snap.Accounts[from.Name].SignedIn ? from.Name + " isn't signed in yet." : "No Code sessions in this profile yet."));
        if (sessions.Count > PageSize) list.Children.Add(Pager(sessions.Count, pages, first, page.Count));

        Border well = Neu.Inset(list, 6, new Thickness(18, 10));
        tile.Children.Add(well);
        content.Children.Add(Neu.Tile(tile, new Thickness(20, 18, 20, 20)));
    }

    // "Showing 1-8 of 23" with the page buttons, only when there's more than one page.
    Control Pager(int total, int pages, int first, int shown)
    {
        var bar = new DockPanel { Margin = new Thickness(0, 16, 0, 2) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var prev = new NeuButton("Newer", false, 12.5) { IsEnabled = sessionPage > 0 };
        prev.Click += () => { sessionPage--; Build(); };
        Avalonia.Automation.AutomationProperties.SetName(prev, "Newer sessions");
        buttons.Children.Add(prev);

        // Numbered pages, windowed to five so a long list doesn't sprawl.
        int firstShown = Math.Max(0, Math.Min(sessionPage - 2, pages - 5));
        int lastShown = Math.Min(pages - 1, Math.Max(sessionPage + 2, 4));
        if (firstShown > 0) buttons.Children.Add(Ellipsis());
        for (int i = firstShown; i <= lastShown; i++)
        {
            int target = i;
            var b = new NeuButton((i + 1).ToString(), false, 12.5) { Selected = i == sessionPage, MinWidth = 38 };
            Avalonia.Automation.AutomationProperties.SetName(b, "Page " + (i + 1) + " of " + pages);
            b.Click += () => { sessionPage = target; Build(); };
            buttons.Children.Add(b);
        }
        if (lastShown < pages - 1) buttons.Children.Add(Ellipsis());

        var next = new NeuButton("Older", false, 12.5) { IsEnabled = sessionPage < pages - 1 };
        next.Click += () => { sessionPage++; Build(); };
        Avalonia.Automation.AutomationProperties.SetName(next, "Older sessions");
        buttons.Children.Add(next);

        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);

        var count = Neu.T("Showing " + (first + 1) + "-" + (first + shown) + " of " + total, 12.5, Neu.P.Muted);
        count.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(count);
        return bar;
    }

    static Control Ellipsis()
    {
        var t = Neu.T("…", 13, Neu.P.Muted);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(2, 0);
        return t;
    }

    Control SessionRow(SessionInfo s)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Neu.T(s.Title, 14.5, null, FontWeight.Normal));
        var meta = Neu.T(Platform.Shorten(s.Cwd) + "   ·   " + Accounts.Ago(s.LastActivity), 12.5, Neu.P.Muted);
        meta.Margin = new Thickness(0, 3, 16, 0);
        text.Children.Add(meta);
        g.Children.Add(text);
        var buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (ProfileEntry q in snap.Profiles)
        {
            if (q == s.Profile || !snap.Accounts[q.Name].SignedIn || string.Equals(snap.Accounts[q.Name].Uuid, s.AccountUuid, StringComparison.OrdinalIgnoreCase)) continue;
            var b = Btn("Continue in " + q.Name + "  →", false, () => _ = Continue(s, q), 12.5);
            b.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(b);
        }
        Grid.SetColumn(buttons, 1);
        g.Children.Add(buttons);
        return g;
    }

    Control Panel(string title, string text, params Control[] buttons)
    {
        var s = new StackPanel();
        s.Children.Add(Neu.Heading(title, 15));
        var p = Neu.Para(text, 13);
        p.Margin = new Thickness(0, 6, 0, 14);
        s.Children.Add(p);
        var r = new WrapPanel();
        foreach (Control b in buttons) { b.Margin = new Thickness(0, 0, 12, 8); r.Children.Add(b); }
        s.Children.Add(r);
        return Neu.Tile(s);
    }

    void BuildBackupPage()
    {
        var grid = new Masonry { MinColumnWidth = 380, MaxColumns = 2 };
        grid.Children.Add(Panel("Move to a new computer",
            "Export profiles (sessions, memory, settings) to one file and import them on another computer, e.g. after a crash. Sign-ins stay on this computer.",
            Btn("Export…", true, () => _ = ExportWindow.Show(Owner, snap)), Btn("Import…", false, async () => { if (await ImportWindow.Run(Owner)) Reload(true); })));
        grid.Children.Add(Panel("Send to a linked computer",
            "Already linked your other computer? Send profiles to it over the internet instead, no file to carry.",
            Btn("Open Linked computers", false, () => Show("linked"))));
        if (LinkRouter.Supported)
        {
            string defName = snap.DefaultProfile?.Name ?? "default";
            string state = snap.LinkActive ? "On. Sign-in links go to the window that's waiting for them."
                : snap.LinkRegistered ? "Almost: pick Claude Switch for CLAUDE links in Windows Settings."
                : "Off. Email and browser sign-in links always open in your " + defName + " window.";
            grid.Children.Add(Panel("Sign-in links", state, snap.LinkActive
                ? Btn("Turn off", false, async () => { LinkRouter.Unregister(); await Dialogs.Info(Owner, "Sign-in links turned off", "If Windows asks how to open a Claude link, pick Claude."); Reload(true); })
                : Btn(snap.LinkRegistered ? "Open Settings" : "Set up", true, async () =>
                {
                    LinkRouter.Register();
                    await Dialogs.Info(Owner, "Send sign-in links to the right profile",
                        "Windows Settings opens next.\n\n1.  Under “Choose defaults by link type”, search for “claude”.\n2.  Pick Claude Switch for the CLAUDE link type.\n\nFrom then on a sign-in link goes to the window waiting for it.");
                    LinkRouter.OpenSettings();
                    Reload(true);
                })));
        }
        content.Children.Add(grid);
    }

    void BuildSupportPage()
    {
        var grid = new Masonry { MinColumnWidth = 380, MaxColumns = 2 };
        grid.Children.Add(Panel("Support this project",
            "I'd like to dedicate more of my time building new tools and working on open source projects. Your donation will give me time to focus on developing new tools for the open-source community.",
            Btn("Buy me a coffee", true, () => Links.Open(Links.Support)), Btn("Report an issue", false, () => Links.Open(Links.Issues))));
        grid.Children.Add(Panel("About",
            "Claude Switch " + typeof(DashboardView).Assembly.GetName().Version.ToString(3) + " by Ngoni Maphosa. Free and open source (MIT). Unofficial; not affiliated with Anthropic.",
            Btn("Website", false, () => Links.Open(Links.Site)), Btn("Source code", false, () => Links.Open(Links.Repo))));
        content.Children.Add(grid);
    }

    // ---- actions -------------------------------------------------------------------------

    void Save()
    {
        try { Profiles.Save(snap.Profiles); } catch (Exception ex) { ErrorLog.Write(ex); }
    }

    async Task AddProfile()
    {
        string name = await Dialogs.Text(Owner, "Add a profile", "Name the account this profile is for, e.g. Work, Personal or Client A. You'll sign in to it the first time it opens.", "",
            n => !Platform.ValidProfileName(n) ? "Use letters, numbers, spaces, - _ or . (up to 40 characters)."
               : Profiles.Find(snap.Profiles, n.Trim()) != null ? "There's already a profile with that name." : null);
        if (name == null) return;
        ProfileEntry added;
        try { added = ProfileSetup.AddOne(name.Trim(), separateMemory: true); }
        catch (Exception ex) { await Dialogs.Info(Owner, "Couldn't add the profile", ex.Message); return; }
        Reload(true);
        // Straight into its own Claude window to sign in: no profile without an account.
        await SignInWindow.Show(Owner, added, isNew: true, () => Reload(true));
    }

    // The "More…" menu on a profile card.
    void ShowMore(ProfileEntry p, Control anchor)
    {
        var menu = new MenuFlyout();
        MenuItem Item(string header, Action click)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => click();
            return m;
        }

        menu.Items.Add(Item("Open Claude Code in a terminal", () => _ = ProfileActions.OpenTerminal(Owner, snap, p, pickFolder: false)));
        menu.Items.Add(Item("Open Claude Code in a folder…", () => _ = ProfileActions.OpenTerminal(Owner, snap, p, pickFolder: true)));
        menu.Items.Add(Item("Copy the terminal command", () => _ = ProfileActions.CopyCommand(Owner, p)));
        menu.Items.Add(new Separator());

        var others = snap.Profiles.Where(q => q != p).ToList();
        if (others.Count > 0)
        {
            var mcp = new MenuItem { Header = "Copy MCP servers from" };
            foreach (ProfileEntry q in others)
            {
                var (desktop, code) = ProfileOps.McpServersOf(q);
                int n = desktop.Count + code.Count;
                ProfileEntry source = q;
                var m = Item(q.Name + (n == 0 ? "  (none)" : "  (" + n + ")"), () => _ = ProfileActions.CopyMcp(Owner, snap, source, p, () => Reload(true), Later));
                m.IsEnabled = n > 0;
                mcp.Items.Add(m);
            }
            menu.Items.Add(mcp);
        }
        menu.Items.Add(Item("Duplicate as a new profile…", () => _ = ProfileActions.Duplicate(Owner, snap, p, () => Reload(true))));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Rename…", () => _ = ProfileActions.Rename(Owner, snap, p, () => Reload(true))));
        var colour = new MenuItem { Header = "Colour" };
        string current = Profiles.ColorOf(snap.Profiles, p);
        foreach (string hex in Profiles.Palette)
        {
            string h = hex;
            var m = Item(hex == current ? "Current" : " ", () => _ = ProfileActions.SetColor(Owner, p, h, () => Reload(true)));
            m.Icon = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Neu.B(Color.Parse(h)) };
            Avalonia.Automation.AutomationProperties.SetName(m, "Colour " + h);
            colour.Items.Add(m);
        }
        colour.Items.Add(Item("Custom…", () => _ = ProfileActions.SetColor(Owner, p, null, () => Reload(true))));
        menu.Items.Add(colour);
        var atStart = Item("Open when Claude Switch starts", () => ProfileActions.ToggleOpenAtStart(p, () => Reload(true)));
        atStart.ToggleType = MenuItemToggleType.CheckBox;
        atStart.IsChecked = p.OpenAtStart;
        menu.Items.Add(atStart);
        menu.Items.Add(Item("Show its data folder", () => ProfileActions.ShowFolder(p)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove…", () => _ = RemoveProfile(p)));
        menu.ShowAt(anchor);
    }

    async Task RemoveProfile(ProfileEntry p)
    {
        var dirs = ProfileSetup.DeletableDirs(p);
        var buttons = dirs.Count > 0 ? new[] { "Remove, keep its data", "Remove and delete its data", "Cancel" } : new[] { "Remove", "Cancel" };
        string msg = "This removes " + p.Name + " from Claude Switch and deletes its shortcuts." +
                     (dirs.Count > 0 ? "\n\nIts sign-in and history live in:\n" + string.Join("\n", dirs.Select(Platform.Shorten)) + "\n\nDelete those too? Export first if you might want them back." : "");
        if (p.IsDefault) msg += "\n\nThis is Claude's main folder; it's never deleted.";
        int c = await Dialogs.Ask(Owner, "Remove " + p.Name + "?", msg, buttons);
        if (c < 0 || c == buttons.Length - 1) return;
        bool delete = dirs.Count > 0 && c == 1;
        Later(() => ProfileSetup.Remove(p.Name, delete), 500);
    }

    // Scrolls to the Claude version card, where the fix buttons live.
    Task ShowUpdates()
    {
        Show("updates");
        return Task.CompletedTask;
    }

    public void ScrollToEnd() => scroller.ScrollToEnd();

    // For screenshots: the whole page (not just what's scrolled into view),
    // and stepping through it a screen at a time.
    public Control Page => content;
    public double PageHeight => scroller.Extent.Height;
    public double ViewHeight => scroller.Viewport.Height;
    public void ScrollTo(double y) => scroller.Offset = new Vector(0, y);

    // Unified profiles already share memory and transcripts; make sure the
    // other account lists the same sessions, then open it.
    async Task CarryOn(ProfileEntry from, ProfileEntry to)
    {
        UnifyResult r = null;
        await Task.Run(() => r = Unify.Mirror(snap.Profiles, snap.Accounts));
        bool open = snap.Pid(to) != 0;
        string msg = to.Name + " opens with the same projects, memory and conversations as " + from.Name + "." +
                     (r is { Mirrored: > 0 } ? "\n\n" + r.Mirrored + " conversations were added to " + to.Name + "'s list." : "") +
                     (open ? "\n\nIt's already open, so it needs a restart to list them. Anything running in it stops."
                           : "\n\nOpen its Code tab and pick up where you left off.");
        int c = await Dialogs.Ask(Owner, "Carry on in " + to.Name + "?", msg,
            open ? "Restart " + to.Name : "Open " + to.Name, "Cancel");
        if (c != 0) { Reload(true); return; }
        Later(() => { if (open) Launcher.Restart(to); else Launcher.Spawn(to); }, 6000);
    }

    async Task SwapNames(ProfileEntry a, ProfileEntry b)
    {
        bool aOpen = snap.Pid(a) != 0, bOpen = snap.Pid(b) != 0;
        if (await Dialogs.Ask(Owner, "Swap " + a.Name + " and " + b.Name + "?",
                "Both Claude windows close, the names swap folders, and they reopen. Each name then shows its own account and sessions. Anything running in those windows stops.",
                "Swap names", "Cancel") != 0) return;
        List<ProfileEntry> all = snap.Profiles;
        Later(() =>
        {
            Launcher.Stop(a.LaunchDir);
            Launcher.Stop(b.LaunchDir);
            Transfer.SwapNames(a, b);
            Profiles.Save(all);
            if (aOpen || bOpen) { Launcher.Spawn(a); Launcher.Spawn(b); }
        }, 5000);
    }

    async Task Recover(RecoveryItem item)
    {
        CopyResult r = Transfer.Recover(item);
        string msg = r.Records + " sessions and " + r.Transcripts + " transcripts copied to " + item.To.Name + ".";
        if (r.Errors.Count > 0) msg += "\n\nSome files couldn't be copied:\n" + string.Join("\n", r.Errors.Take(5));
        await OfferRestart(item.To, "Sessions copied", msg);
    }

    async Task Continue(SessionInfo s, ProfileEntry to)
    {
        AccountInfo ta = snap.Accounts[to.Name];
        string msg = "This copies the session and its transcript to " + to.Name + " (" + (ta.Email ?? "its account") +
                     ") so you can carry on there. Nothing is moved or deleted; the copy is tagged “(from " + s.Profile.Name + ")”.\n\n" +
                     to.Name + " has to restart to list it. Anything running in that window stops.";
        if (!Settings.HandoffNoticeShown)
            msg += "\n\nBefore relying on this: the conversation and any code in it will also be stored in your " + to.Name +
                   " account. Check your company's rules on work in another account, and Anthropic's terms for your plans.";
        int c = await Dialogs.Ask(Owner, "Continue “" + s.Title + "” in " + to.Name + "?", msg, "Copy and restart " + to.Name, "Copy only", "Cancel");
        if (c is not (0 or 1)) return;
        Settings.HandoffNoticeShown = true;
        CopyResult r = Transfer.Handoff(s, to, ta);
        if (r.Errors.Count > 0 && r.Records == 0) { await Dialogs.Info(Owner, "Couldn't continue in " + to.Name, string.Join("\n", r.Errors)); return; }
        if (r.Records == 0) { await Dialogs.Info(Owner, "Already there", "“" + s.Title + "” was already handed to " + to.Name + ". Open its Code tab to continue."); return; }
        if (c == 0) Later(() => Launcher.Restart(to), 6000);
        else await Dialogs.Info(Owner, "Copied to " + to.Name, "Restart " + to.Name + " (button on its card) to see it in the Code tab.");
    }

    async Task OfferRestart(ProfileEntry p, string title, string msg)
    {
        if (snap.Pid(p) == 0) { await Dialogs.Info(Owner, title, msg + "\n\nThey'll show the next time you open " + p.Name + "."); Reload(true); return; }
        if (await Dialogs.Ask(Owner, title, msg + "\n\nRestart " + p.Name + " now so its window lists them? Anything running in it stops.", "Restart " + p.Name, "Later") == 0)
            Later(() => Launcher.Restart(p), 6000);
        else Reload(true);
    }

    async Task PasteReport(ProfileEntry p)
    {
        string text = null;
        try { text = await Owner.Clipboard.GetTextAsync(); } catch (Exception) { }
        UsageReportData r = UsageReport.Parse(text);
        if (r == null)
        {
            await Dialogs.Info(Owner, "No usage report on the clipboard",
                "In " + p.Name + "'s Claude window, copy the usage report (it starts with “Claude Code usage report”), then click Paste report again.\n\n" +
                "Tip: while Claude Switch is running, just copying the report updates the card by itself.");
            return;
        }
        await Reports.Assign(Owner, r, p, snap);
        Reload(true);
    }
}
