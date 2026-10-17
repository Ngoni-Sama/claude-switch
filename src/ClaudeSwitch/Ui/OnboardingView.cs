// First run: name the account Claude is already signed into, then any others.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public class OnboardingView : StackPanel
{
    public OnboardingView(Action done)
    {
        MaxWidth = 640;
        HorizontalAlignment = HorizontalAlignment.Left;

        var logo = new Image { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 18) };
        try { logo.Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClaudeSwitch/Assets/icon.png"))); } catch (Exception) { }
        Children.Add(logo);
        Children.Add(Neu.T("Welcome to Claude Switch", 28, null, FontWeight.SemiBold));
        var intro = Neu.Para("Run several Claude accounts side by side, each in its own window: for example Work and Personal. " +
                             "Name your accounts below; you can add or remove profiles later.", 14.5);
        intro.Margin = new Thickness(0, 8, 0, 24);
        Children.Add(intro);

        bool installed = ClaudeApp.ResolveExe() != null;
        if (!installed)
        {
            var warn = Neu.Para(Platform.IsLinux
                ? "Claude Desktop isn't installed yet. On Ubuntu/Debian: add Anthropic's apt repository, then run  sudo apt install claude-desktop."
                : "Claude Desktop isn't installed yet. Get it from claude.ai/download first; you can still set up your profiles now.", 13.5, Neu.P.Warn);
            warn.Margin = new Thickness(0, 0, 0, 18);
            Children.Add(warn);
        }

        var form = new StackPanel { Spacing = 6 };
        form.Children.Add(Neu.T("The account Claude is signed in to now", 13.5, null, FontWeight.SemiBold));
        form.Children.Add(Neu.Para("This profile keeps your existing sign-in, history and settings.", 12.5));
        var current = new TextBox { Text = "Personal", Margin = new Thickness(0, 4, 0, 14) };
        form.Children.Add(current);
        form.Children.Add(Neu.T("Your other accounts", 13.5, null, FontWeight.SemiBold));
        form.Children.Add(Neu.Para("Separate names with commas. Each gets its own window; you sign in to it once.", 12.5));
        var others = new TextBox { Text = "Work", Margin = new Thickness(0, 4, 0, 14) };
        form.Children.Add(others);

        var memory = new CheckBox { Content = "Give the other accounts their own Claude Code memory and settings (recommended)", IsChecked = true, Foreground = Neu.B(Neu.P.Text) };
        var desktop = new CheckBox { Content = "Add desktop shortcuts", IsChecked = true, IsVisible = Platform.IsWindows, Foreground = Neu.B(Neu.P.Text) };
        var login = new CheckBox { Content = "Start Claude Switch when I sign in", IsChecked = true, Foreground = Neu.B(Neu.P.Text) };
        form.Children.Add(memory);
        form.Children.Add(desktop);
        form.Children.Add(login);

        var err = Neu.T("", 13, Neu.P.Bad);
        err.Margin = new Thickness(0, 10, 0, 0);
        form.Children.Add(err);

        var go = new NeuButton("Set up my profiles", primary: true) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        go.Click += () =>
        {
            string cur = current.Text?.Trim() ?? "";
            var rest = (others.Text ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            string bad = new[] { cur }.Concat(rest).FirstOrDefault(n => !Platform.ValidProfileName(n));
            if (cur.Length == 0) { err.Text = "Name the account Claude is signed in to now."; return; }
            if (bad != null) { err.Text = "“" + bad + "”: use letters, numbers, spaces, - _ or . (up to 40 characters)."; return; }
            if (rest.Any(r => string.Equals(r, cur, StringComparison.OrdinalIgnoreCase))) { err.Text = "Each profile needs a different name."; return; }
            try
            {
                ProfileSetup.Create(cur, rest, memory.IsChecked == true, desktop.IsChecked == true);
                Shortcuts.SetStartAtLogin(login.IsChecked == true);
                Settings.OnboardingDone = true;
                done();
            }
            catch (Exception ex) { ErrorLog.Write(ex); err.Text = "Couldn't create the profiles: " + ex.Message; }
        };
        form.Children.Add(go);
        Children.Add(Neu.Raised(form, 24, 10, new Thickness(28, 24)));

        var foot = Neu.Para("Unofficial tool, not affiliated with Anthropic. Claude Switch only opens Claude Desktop with a separate data folder per profile; it never reads or sends your sign-in.", 12);
        foot.Margin = new Thickness(0, 22, 0, 0);
        Children.Add(foot);
    }
}
