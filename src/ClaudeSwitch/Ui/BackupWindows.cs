// Export and Import windows.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClaudeSwitch.Core;
using System.Security.Cryptography;

namespace ClaudeSwitch.Ui;

public static class ExportWindow
{
    static readonly FilePickerFileType BackupType = new("Claude Switch backup") { Patterns = new[] { "*.claudeprofiles" } };

    public static Task Show(Window owner, Snapshot snap)
    {
        Window w = Dialogs.Frame("Export profiles", 620);
        var tcs = new TaskCompletionSource();
        w.Closed += (_, _) => tcs.TrySetResult();

        var body = new StackPanel();
        body.Children.Add(Neu.Para("Save profiles to one file you can import on another computer, for example after a crash or on a new laptop."));
        body.Children.Add(Neu.Section("Profiles"));
        var profileChecks = new List<(ProfileEntry, CheckBox)>();
        foreach (ProfileEntry p in snap.Profiles)
        {
            AccountInfo a = snap.Accounts[p.Name];
            var c = Check(p.Name, (a.SignedIn ? a.Email ?? "signed in" : "not signed in") + "   ·   " + snap.Sessions[p.Name].Count + " sessions", true);
            profileChecks.Add((p, (CheckBox)((Panel)c).Children[0]));
            body.Children.Add(c);
        }
        body.Children.Add(Neu.Section("Include"));
        var cSessions = Add(body, "Sessions and transcripts", "Code-tab conversations, so you can resume them on the new computer", true);
        var cMemory = Add(body, "Claude Code memory", "The notes Claude keeps per project", true);
        var cConfig = Add(body, "Claude Code settings, skills, agents and hooks", "settings.json, CLAUDE.md, skills, agents, hooks and MCP servers. Never your sign-in.", true);
        var cDesktop = Add(body, "Claude Desktop settings and MCP servers", "claude_desktop_config.json can contain API keys, so keep the password on.", true);
        var cCowork = Add(body, "Cowork session records", "Small records only; the Cowork workspace itself can't be moved.", false);

        body.Children.Add(Neu.Section("Protection"));
        var cPassword = Add(body, "Protect with a password (recommended)", "AES-256. Without the password the file can't be opened, so keep it safe.", true);
        var pw1 = new TextBox { PasswordChar = '•', Watermark = "Password (at least 8 characters)", Margin = new Thickness(30, 6, 0, 0) };
        var pw2 = new TextBox { PasswordChar = '•', Watermark = "Repeat password", Margin = new Thickness(30, 8, 0, 0) };
        body.Children.Add(pw1);
        body.Children.Add(pw2);
        cPassword.IsCheckedChanged += (_, _) => pw1.IsEnabled = pw2.IsEnabled = cPassword.IsChecked == true;

        var note = Neu.Para("Sign-ins aren't included: your OS encrypts them to this computer. On the new computer, sign in to each profile once.", 12.5);
        note.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(note);
        var status = Neu.T("", 13, Neu.P.Accent, FontWeight.SemiBold);
        status.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(status);

        var export = new NeuButton("Export…", primary: true);
        var cancel = new NeuButton("Cancel");
        cancel.Click += w.Close;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(export);
        row.Children.Add(cancel);
        body.Children.Add(row);

        export.Click += async () =>
        {
            var o = new ExportOptions
            {
                Profiles = profileChecks.Where(x => x.Item2.IsChecked == true).Select(x => x.Item1).ToList(),
                Sessions = cSessions.IsChecked == true, Memory = cMemory.IsChecked == true, CodeConfig = cConfig.IsChecked == true,
                DesktopSettings = cDesktop.IsChecked == true, Cowork = cCowork.IsChecked == true,
            };
            if (o.Profiles.Count == 0) { status.Text = "Choose at least one profile."; return; }
            if (!(o.Sessions || o.Memory || o.CodeConfig || o.DesktopSettings || o.Cowork)) { status.Text = "Choose at least one thing to include."; return; }
            if (cPassword.IsChecked == true)
            {
                if ((pw1.Text ?? "").Length < 8) { status.Text = "Use a password of at least 8 characters."; return; }
                if (pw1.Text != pw2.Text) { status.Text = "The passwords don't match."; return; }
                o.Password = pw1.Text;
            }
            IStorageFile file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save backup", SuggestedFileName = "claude-switch-" + DateTime.Now.ToString("yyyy-MM-dd") + ".claudeprofiles",
                DefaultExtension = "claudeprofiles", FileTypeChoices = new[] { BackupType },
            });
            string path = file?.TryGetLocalPath();
            if (path == null) return;
            export.IsEnabled = cancel.IsEnabled = false;
            status.Text = "Exporting…";
            long size = -1;
            string error = null;
            await Task.Run(() =>
            {
                try { size = Backup.Export(o, path, m => Dispatcher.UIThread.Post(() => status.Text = m)); }
                catch (Exception ex) { error = ex.Message; ErrorLog.Write(ex); }
            });
            export.IsEnabled = cancel.IsEnabled = true;
            status.Text = "";
            if (error != null) { await Dialogs.Info(w, "Export failed", error); return; }
            await Dialogs.Info(w, "Export complete", "Saved " + (size / 1024.0 / 1024.0).ToString("0.0") + " MB to\n" + path + "\n\n" +
                (o.Password != null ? "It's encrypted; you'll need the password to import it." : "It isn't encrypted and contains your conversations, so store it somewhere private."));
            w.Close();
        };

        Dialogs.SetContent(w, new ScrollViewer { Content = Dialogs.Body("Export profiles", body), MaxHeight = 760 });
        if (owner != null) w.ShowDialog(owner); else w.Show();
        return tcs.Task;
    }

    internal static Control Check(string text, string detail, bool on)
    {
        var box = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Top };
        var label = new StackPanel { Margin = new Thickness(4, 6, 0, 0) };
        label.Children.Add(Neu.T(text, 14, null, FontWeight.SemiBold));
        if (!string.IsNullOrEmpty(detail)) label.Children.Add(Neu.Para(detail, 12.5));
        box.Content = null;
        Avalonia.Automation.AutomationProperties.SetName(box, text);
        var row = new DockPanel { Margin = new Thickness(0, 2) };
        DockPanel.SetDock(box, Dock.Left);
        row.Children.Add(box);
        row.Children.Add(label);
        label.PointerPressed += (_, _) => box.IsChecked = !(box.IsChecked == true);
        return row;
    }

    static CheckBox Add(Panel body, string text, string detail, bool on)
    {
        Control c = Check(text, detail, on);
        body.Children.Add(c);
        return (CheckBox)((Panel)c).Children[0];
    }
}

public static class ImportWindow
{
    // Pick a file, unlock it, and show the import window. True if something was imported.
    public static async Task<bool> Run(Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Claude Switch backup", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Claude Switch backup") { Patterns = new[] { "*.claudeprofiles" } }, FilePickerFileTypes.All },
        });
        string path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return false;
        return await Open(owner, path);
    }

    // Unlock a backup file (asking for its password if needed) and show what to import.
    public static async Task<bool> Open(Window owner, string path, string from = null)
    {
        ArchiveInfo info = null;
        try
        {
            if (Backup.IsEncrypted(path))
            {
                for (int attempt = 0; attempt < 3 && info == null; attempt++)
                {
                    string pw = await Dialogs.Password(owner, "Unlock backup",
                        attempt == 0 ? "This backup is password-protected. Enter its password." : "That password didn't work. Try again.", false);
                    if (pw == null) return false;
                    try { info = await Task.Run(() => Backup.Open(path, pw)); } catch (CryptographicException) { }
                }
                if (info == null) { await Dialogs.Info(owner, "Couldn't unlock the backup", "Wrong password, or the file is damaged."); return false; }
            }
            else info = await Task.Run(() => Backup.Open(path, null));
        }
        catch (Exception ex) { await Dialogs.Info(owner, "Couldn't open the backup", ex.Message); return false; }

        List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
        var accounts = profiles.ToDictionary(p => p.Name, Accounts.Read);
        Window w = Dialogs.Frame("Import profiles", 620);
        bool imported = false;
        var tcs = new TaskCompletionSource<bool>();
        w.Closed += (_, _) => { info.Dispose(); tcs.TrySetResult(imported); };

        var body = new StackPanel();
        body.Children.Add(Neu.Para((from != null ? "Sent by " + from : "Backup from " + (info.Machine ?? "another computer")) + ", " + (info.Created ?? "unknown date") + "."));
        body.Children.Add(Neu.Section("Profiles in this backup"));
        var rows = new List<(ImportChoice, CheckBox)>();
        foreach (ArchiveProfile ap in info.Profiles)
        {
            ImportChoice c = Backup.Suggest(ap, profiles, accounts);
            string where = c.CreateNew ? "→ new profile " + c.Target.Name + " (" + Platform.Shorten(c.Target.Data) + ")" : "→ into your " + c.Target.Name + " profile";
            Control row = ExportWindow.Check(ap.Name + (ap.Email != null ? "   ·   " + ap.Email : ""), ap.Summary + "\n" + where, true);
            rows.Add((c, (CheckBox)((Panel)row).Children[0]));
            body.Children.Add(row);
        }
        var note = Neu.Para("Nothing on this computer is overwritten. Close those profiles' Claude windows first so they list the imported sessions. Sign-ins don't move between computers: sign in to each profile once.", 12.5);
        note.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(note);
        var status = Neu.T("", 13, Neu.P.Accent, FontWeight.SemiBold);
        status.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(status);
        var go = new NeuButton("Import", primary: true);
        var cancel = new NeuButton("Cancel");
        cancel.Click += w.Close;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(go);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        go.Click += async () =>
        {
            var choices = rows.Where(r => r.Item2.IsChecked == true).Select(r => r.Item1).ToList();
            if (choices.Count == 0) { status.Text = "Choose at least one profile."; return; }
            go.IsEnabled = cancel.IsEnabled = false;
            status.Text = "Importing…";
            ImportResult r = null;
            string error = null;
            await Task.Run(() =>
            {
                try { r = Backup.Import(info, choices, m => Dispatcher.UIThread.Post(() => status.Text = m)); }
                catch (Exception ex) { error = ex.Message; ErrorLog.Write(ex); }
            });
            go.IsEnabled = cancel.IsEnabled = true;
            status.Text = "";
            if (error != null) { await Dialogs.Info(w, "Import failed", error); return; }
            imported = true;
            string msg = r.Files + " files imported" + (r.Skipped > 0 ? ", " + r.Skipped + " already here (kept)" : "") + ".";
            if (r.Notes.Count > 0) msg += "\n\n" + string.Join("\n", r.Notes);
            if (r.Errors.Count > 0) msg += "\n\nCouldn't import:\n" + string.Join("\n", r.Errors.Take(5));
            await Dialogs.Info(w, "Import complete", msg);
            w.Close();
        };

        Dialogs.SetContent(w, new ScrollViewer { Content = Dialogs.Body("Import profiles", body), MaxHeight = 760 });
        await w.ShowDialog(owner);
        return await tcs.Task;
    }
}
