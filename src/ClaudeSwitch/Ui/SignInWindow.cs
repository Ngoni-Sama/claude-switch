// Every profile needs its own Claude account. When one is added (or found with no
// account), this opens its Claude window and waits until it's signed in to an
// account none of the other profiles use.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public static class SignInWindow
{
    enum State { Waiting, Checking, SameAccount, Ready }

    public static Task Show(Window owner, ProfileEntry p, bool isNew, Action reload)
    {
        var done = new TaskCompletionSource();
        var w = Dialogs.Frame("Sign in to " + p.Name, 540);

        List<ProfileEntry> all = Profiles.LoadOrEmpty();
        ProfileEntry def = all.FirstOrDefault(x => x.IsDefault);
        bool isDefault = p.IsDefault || def != null && string.Equals(def.Name, p.Name, StringComparison.OrdinalIgnoreCase);
        bool canRoute = LinkRouter.Supported && !isDefault && !LinkRouter.IsActive;

        var intro = Neu.Para("A Claude window is opening for " + p.Name + ". Sign in there with the account this profile is for. " +
                             "It has to be a different account from your other profiles, so each one has its own usage.", 14);
        var tip = Neu.Para(isDefault || LinkRouter.IsActive ? "" :
            "If you sign in with an email link or in your browser, the link may open in your " + (def?.Name ?? "main") +
            " window instead. Turn on sign-in links first so it always reaches the right window.", 12.5);
        tip.Margin = new Thickness(0, 8, 0, 0);
        tip.IsVisible = canRoute;

        var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        var statusText = Neu.T("", 15, null, FontWeight.SemiBold, wrap: true);
        statusText.VerticalAlignment = VerticalAlignment.Center;
        var statusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        statusRow.Children.Add(dot);
        statusText.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(statusText, 1);
        statusRow.Children.Add(statusText);
        var detail = Neu.Para("", 13);
        detail.Margin = new Thickness(20, 4, 0, 0);
        var status = new StackPanel();
        status.Children.Add(statusRow);
        status.Children.Add(detail);
        var statusBox = Neu.Inset(status, 12, new Thickness(16, 14, 16, 14));
        statusBox.Margin = new Thickness(0, 16, 0, 0);

        var finish = new NeuButton("Done", true) { IsEnabled = false };
        var reopen = new NeuButton("Open its window again");
        var routing = new NeuButton("Turn on sign-in links") { IsVisible = canRoute };
        var later = new NeuButton(isNew ? "Cancel and remove it" : "Later");
        var row = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        foreach (NeuButton b in new[] { finish, reopen, routing, later }) { b.Margin = new Thickness(0, 0, 10, 8); row.Children.Add(b); }

        Dialogs.SetContent(w, Dialogs.Body("Sign in to " + p.Name, intro, tip, statusBox, row));

        State state = State.Waiting;
        bool closing = false, busy = false;
        void Set(State s, string title, string more)
        {
            state = s;
            Color c = s switch { State.Ready => Neu.P.Good, State.SameAccount => Neu.P.Bad, _ => Neu.P.Warn };
            dot.Background = Neu.B(c);
            statusText.Text = title;
            statusText.Foreground = Neu.B(s == State.Ready || s == State.SameAccount ? c : Neu.P.Text);
            detail.Text = more;
            detail.IsVisible = !string.IsNullOrEmpty(more);
            finish.IsEnabled = s == State.Ready;
        }
        Set(State.Waiting, "Waiting for you to sign in…", "This updates by itself as soon as you've signed in.");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            try
            {
                var (mine, clash, clashAccount) = await Task.Run(() => Check(p));
                if (closing) return;
                if (!mine.SignedIn) Set(State.Waiting, "Waiting for you to sign in…", "This updates by itself as soon as you've signed in.");
                else if (mine.Uuid == null) Set(State.Checking, "Signed in. Checking which account…", null);
                else if (clash != null)
                    Set(State.SameAccount, "That's the same account as " + clash.Name,
                        (mine.Email ?? clashAccount.Email ?? "This account") + " is already used by " + clash.Name + ". In " + p.Name +
                        "'s Claude window, log out (click your name, then Log out) and sign in with a different account.");
                else if (state != State.Ready)
                {
                    Set(State.Ready, "Signed in as " + (mine.Email ?? "a new account"), p.Name + " is ready to use.");
                    reload();
                }
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
            finally { busy = false; }
        };

        finish.Click += () => { closing = true; w.Close(); };
        reopen.Click += () => Launcher.Activate(p, ClaudeApp.FindMain(Platform.Normal(p.LaunchDir)));
        routing.Click += () => { LinkRouter.Register(); LinkRouter.OpenSettings(); routing.IsVisible = tip.IsVisible = false; };
        later.Click += () => w.Close();

        // A new profile left without its own account is removed, unless you say keep it.
        w.Closing += async (_, e) =>
        {
            if (closing || !isNew || state == State.Ready) return;
            e.Cancel = true;
            int c = await Dialogs.Ask(w, "Remove " + p.Name + "?",
                p.Name + " isn't signed in to its own account yet. A profile with no account can't be used, so it's removed unless you want to finish signing in later.",
                "Remove it", "Keep it, I'll sign in later", "Go back");
            if (c == 2 || c < 0) return;
            closing = true;
            if (c == 0)
            {
                await Task.Run(() =>
                {
                    try { Launcher.Stop(p.LaunchDir); ProfileSetup.Remove(p.Name, deleteData: true); }
                    catch (Exception ex) { ErrorLog.Write(ex); }
                });
            }
            w.Close();
        };
        w.Opened += (_, _) =>
        {
            Launcher.Spawn(p, firstSignIn: true);
            timer.Start();
        };
        w.Closed += (_, _) => { timer.Stop(); reload(); done.TrySetResult(); };

        if (owner != null && owner.IsVisible) { w.WindowStartupLocation = WindowStartupLocation.CenterOwner; w.ShowDialog(owner); }
        else { w.Show(); w.Activate(); }
        return done.Task;
    }

    // This profile's account, and the first other profile signed in to the same one.
    static (AccountInfo mine, ProfileEntry clash, AccountInfo clashAccount) Check(ProfileEntry p)
    {
        AccountInfo mine = Accounts.Read(p);
        if (!mine.SignedIn || mine.Uuid == null) return (mine, null, null);
        foreach (ProfileEntry q in Profiles.LoadOrEmpty())
        {
            if (string.Equals(q.Name, p.Name, StringComparison.OrdinalIgnoreCase)) continue;
            AccountInfo a = Accounts.Read(q);
            if (a.SignedIn && string.Equals(a.Uuid, mine.Uuid, StringComparison.OrdinalIgnoreCase)) return (mine, q, a);
        }
        return (mine, null, null);
    }
}
