// Dialog windows. Used inside the running app (async, owned by a window) and
// from command-line launches like `--launch Work` (sync: Avalonia is set up on
// demand and a modal loop runs until the dialog closes).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public static class Dialogs
{
    static WindowIcon icon;

    public static WindowIcon AppIcon
    {
        get
        {
            try { return icon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://ClaudeSwitch/Assets/icon.png"))); }
            catch (Exception) { return null; }
        }
    }

    // ---- command-line (sync) mode ---------------------------------------------------

    public static void EnsureUi()
    {
        if (Application.Current != null) return;
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        Neu.Use(Settings.Theme);
    }

    static T RunModal<T>(Window w, Func<T> result)
    {
        var cts = new CancellationTokenSource();
        w.Closed += (_, _) => cts.Cancel();
        w.Topmost = true;
        w.Show();
        w.Activate();
        w.Topmost = false;
        Dispatcher.UIThread.MainLoop(cts.Token);
        return result();
    }

    public static int AskSync(string title, string message, params string[] buttons)
    {
        EnsureUi();
        int choice = -1;
        Window w = AskWindow(title, message, buttons, i => choice = i);
        return RunModal(w, () => choice);
    }

    public static void ErrorSync(string message) => AskSync("Claude Switch", message, "OK");

    public static ProfileEntry PickSync(string title, string message, List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts, Dictionary<string, int> running)
    {
        EnsureUi();
        ProfileEntry picked = null;
        Window w = PickWindow(title, message, profiles, accounts, running, p => picked = p);
        return RunModal(w, () => picked);
    }

    // Shown before a profile that has never signed in opens. False = cancel.
    public static bool FirstSignInSync(ProfileEntry p)
    {
        List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
        ProfileEntry def = profiles.FirstOrDefault(x => x.IsDefault);
        string defName = def?.Name ?? "default";
        bool isDefault = def == p || p.IsDefault;
        bool routed = LinkRouter.IsActive;
        bool canRoute = LinkRouter.Supported && !isDefault && !routed;

        string msg = "A new Claude window opens for " + p.Name + ". Sign in there with the account you want this profile to use.";
        if (!isDefault && !routed)
            msg += "\n\nHeads-up: if you sign in with an email link or in your browser, the link may open in your " + defName +
                   " window instead, and " + defName + " switches account. If that happens, open Claude Switch: it spots the mix-up and offers Swap names.";
        var buttons = new List<string> { "Open " + p.Name };
        if (canRoute) buttons.Add("Set up sign-in links first");
        buttons.Add("Cancel");
        int choice = AskSync("Sign in to " + p.Name, msg, buttons.ToArray());
        if (choice == 0) return true;
        if (canRoute && choice == 1) { LinkRouter.Register(); LinkRouter.OpenSettings(); }
        return false;
    }

    // ---- in-app (async) mode ---------------------------------------------------------

    public static Task<int> Ask(Window owner, string title, string message, params string[] buttons)
    {
        var tcs = new TaskCompletionSource<int>();
        int choice = -1;
        Window w = AskWindow(title, message, buttons, i => choice = i);
        w.Closed += (_, _) => tcs.TrySetResult(choice);
        Show(w, owner);
        return tcs.Task;
    }

    public static Task Info(Window owner, string title, string message) => Ask(owner, title, message, "OK");

    public static Task<ProfileEntry> Pick(Window owner, string title, string message, List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts, Dictionary<string, int> running)
    {
        var tcs = new TaskCompletionSource<ProfileEntry>();
        ProfileEntry picked = null;
        Window w = PickWindow(title, message, profiles, accounts, running, p => picked = p);
        w.Closed += (_, _) => tcs.TrySetResult(picked);
        Show(w, owner);
        return tcs.Task;
    }

    public static Task<string> Text(Window owner, string title, string message, string initial, Func<string, string> validate)
    {
        var tcs = new TaskCompletionSource<string>();
        string result = null;
        Window w = Frame(title, 460);
        var box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 14, 0, 0) };
        var err = Neu.T("", 12.5, Neu.P.Bad);
        err.Margin = new Thickness(0, 8, 0, 0);
        var ok = new NeuButton("OK", primary: true);
        ok.Click += () =>
        {
            string problem = validate?.Invoke(box.Text);
            if (problem != null) { err.Text = problem; return; }
            result = box.Text.Trim();
            w.Close();
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ok.Focus(); e.Handled = true; } };
        SetContent(w, Body(title, Neu.Para(message), box, err, Buttons(w, ok)));
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        w.Closed += (_, _) => tcs.TrySetResult(result);
        Show(w, owner);
        return tcs.Task;
    }

    // confirm = ask twice (when setting a new password).
    public static Task<string> Password(Window owner, string title, string message, bool confirm)
    {
        var tcs = new TaskCompletionSource<string>();
        string result = null;
        Window w = Frame(title, 460);
        var p1 = new TextBox { PasswordChar = '•', Watermark = "Password", Margin = new Thickness(0, 14, 0, 0) };
        var p2 = new TextBox { PasswordChar = '•', Watermark = "Repeat password", Margin = new Thickness(0, 10, 0, 0), IsVisible = confirm };
        var err = Neu.T("", 12.5, Neu.P.Bad);
        err.Margin = new Thickness(0, 8, 0, 0);
        var ok = new NeuButton("OK", primary: true);
        ok.Click += () =>
        {
            string a = p1.Text ?? "";
            if (a.Length < 8) { err.Text = "Use at least 8 characters."; return; }
            if (confirm && a != (p2.Text ?? "")) { err.Text = "The passwords don't match."; return; }
            result = a;
            w.Close();
        };
        p1.KeyDown += (_, e) => { if (e.Key == Key.Enter && !confirm) { ok.Focus(); e.Handled = true; } };
        SetContent(w, Body(title, Neu.Para(message), p1, p2, err, Buttons(w, ok)));
        w.Opened += (_, _) => p1.Focus();
        w.Closed += (_, _) => tcs.TrySetResult(result);
        Show(w, owner);
        return tcs.Task;
    }

    // ---- building blocks -----------------------------------------------------------

    public static Window Frame(string title, double width) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Neu.B(Neu.P.Bg), Icon = AppIcon,
        ShowInTaskbar = true,
    };

    /// <summary>
    /// Gives a dialog its title bar and content. Use this instead of setting
    /// Window.Content, so every window gets the same close button.
    /// </summary>
    public static void SetContent(Window w, Control body) =>
        w.Content = Chrome.Wrap(w, w.Title, body, canMaximize: false, fitContent: true);

    static void Show(Window w, Window owner)
    {
        if (owner != null && owner.IsVisible) { w.WindowStartupLocation = WindowStartupLocation.CenterOwner; w.ShowDialog(owner); }
        else { w.Topmost = true; w.Show(); w.Activate(); w.Topmost = false; }
    }

    public static Control Body(string title, params Control[] parts)
    {
        var s = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        var head = Neu.T(title, 19, null, FontWeight.SemiBold, wrap: true);
        head.Margin = new Thickness(0, 0, 0, 10);
        s.Children.Add(head);
        foreach (Control c in parts) s.Children.Add(c);
        return s;
    }

    static Control Buttons(Window w, NeuButton primary)
    {
        var cancel = new NeuButton("Cancel");
        cancel.Click += w.Close;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Margin = new Thickness(0, 20, 0, 0) };
        row.Children.Add(primary);
        row.Children.Add(cancel);
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        return row;
    }

    static Window AskWindow(string title, string message, string[] buttons, Action<int> chose)
    {
        Window w = Frame(title, 500);
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new NeuButton(buttons[i], primary: i == 0) { Margin = new Thickness(12, 6, 0, 0) };
            b.Click += () => { chose(idx); w.Close(); };
            row.Children.Add(b);
            if (i == 0) w.Opened += (_, _) => b.Focus();
        }
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        SetContent(w, Body(title, Neu.Para(message), row));
        return w;
    }

    static Window PickWindow(string title, string message, List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts, Dictionary<string, int> running, Action<ProfileEntry> picked)
    {
        Window w = Frame(title, 480);
        var list = new StackPanel { Spacing = 14, Margin = new Thickness(0, 14, 0, 0) };
        foreach (ProfileEntry p in profiles)
        {
            accounts.TryGetValue(p.Name, out var a);
            bool open = running.ContainsKey(p.Key);
            var b = new NeuButton(p.Name + (a?.Email != null ? "   ·   " + a.Email : "") + (open ? "" : "   (not open)")) { HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += () => { picked(p); w.Close(); };
            list.Children.Add(b);
        }
        var cancel = new NeuButton("Cancel") { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        cancel.Click += w.Close;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        SetContent(w, Body(title, Neu.Para(message), list, cancel));
        return w;
    }
}
