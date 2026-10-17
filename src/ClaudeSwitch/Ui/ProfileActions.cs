// What the "More…" menu on a profile card does.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.Ui;

public static class ProfileActions
{
    // ---- Claude Code in a terminal -----------------------------------------------

    public static async Task OpenTerminal(Window owner, Snapshot snap, ProfileEntry p, bool pickFolder)
    {
        string folder = null;
        if (pickFolder)
        {
            var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open Claude Code as " + p.Name + " in…", AllowMultiple = false });
            folder = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (folder == null) return;
        }
        if (Terminal.FindCli() == null)
        {
            int c = await Dialogs.Ask(owner, "Claude Code isn't installed",
                "The terminal will open set up for " + p.Name + ", but the claude command wasn't found on this computer. " +
                "Install Claude Code first, then try again.", "Show how to install", "Open the terminal anyway", "Cancel");
            if (c == 0) { Links.Open(Terminal.InstallDocs); return; }
            if (c != 1) return;
        }
        string problem = Terminal.Open(p, folder, snap.Accounts.TryGetValue(p.Name, out AccountInfo a) ? a.Email : null);
        if (problem != null) await Dialogs.Info(owner, "Couldn't open a terminal", problem);
    }

    public static async Task CopyCommand(Window owner, ProfileEntry p)
    {
        string cmd = Terminal.CommandLine(p);
        try { await owner.Clipboard.SetTextAsync(cmd); } catch (Exception) { }
        await Dialogs.Info(owner, "Command copied",
            "Paste it into " + (Platform.IsWindows ? "PowerShell" : "your terminal") + " to run Claude Code as " + p.Name + ":\n\n" + cmd +
            (Terminal.Installed ? "\n\nOr just type " + Terminal.CommandName(p) + "." : "\n\nTip: turn on terminal commands (bottom of the dashboard) to type " + Terminal.CommandName(p) + " instead."));
    }

    // ---- name, colour, start-up --------------------------------------------------------

    public static async Task Rename(Window owner, Snapshot snap, ProfileEntry p, Action reload)
    {
        string to = await Dialogs.Text(owner, "Rename " + p.Name,
            "Its launchers and terminal command follow the new name. Its folders stay where they are, so nothing needs signing in again.", p.Name,
            n => !Platform.ValidProfileName(n) ? "Use letters, numbers, spaces, - _ or . (up to 40 characters)."
               : !string.Equals(n.Trim(), p.Name, StringComparison.OrdinalIgnoreCase) && Profiles.Find(snap.Profiles, n.Trim()) != null ? "There's already a profile with that name." : null);
        if (to == null || to == p.Name) return;
        string problem = await Task.Run(() => ProfileOps.Rename(p.Name, to));
        if (problem != null) await Dialogs.Info(owner, "Couldn't rename it", problem);
        reload();
    }

    public static async Task SetColor(Window owner, ProfileEntry p, string hex, Action reload)
    {
        if (hex == null)
        {
            hex = await Dialogs.Text(owner, "Colour for " + p.Name, "A colour in hex, like #3B82F6.", p.Color ?? "#",
                h => Profiles.IsHex(h?.Trim()) ? null : "Use # and six hex digits, like #3B82F6.");
            if (hex == null) return;
        }
        await Task.Run(() => ProfileOps.SetColor(p.Name, hex.Trim()));
        reload();
    }

    public static void ToggleOpenAtStart(ProfileEntry p, Action reload)
    {
        ProfileOps.SetOpenAtStart(p.Name, !p.OpenAtStart);
        reload();
    }

    public static void ShowFolder(ProfileEntry p) => Links.Open(Directory.Exists(p.Data) ? p.Data : p.LaunchDir);

    // ---- duplicate ---------------------------------------------------------------------

    public static async Task Duplicate(Window owner, Snapshot snap, ProfileEntry p, Action reload)
    {
        string name = await Dialogs.Text(owner, "Duplicate " + p.Name,
            "Makes a new profile with a copy of " + p.Name + "'s chats, Code sessions, settings and Claude Code memory. " +
            "The copy signs in on first open: sign-ins are never copied. Name the new profile:",
            p.Name + " copy",
            n => !Platform.ValidProfileName(n) ? "Use letters, numbers, spaces, - _ or . (up to 40 characters)."
               : Profiles.Find(snap.Profiles, n.Trim()) != null ? "There's already a profile with that name." : null);
        if (name == null) return;
        if (snap.Pid(p) != 0)
        {
            if (await Dialogs.Ask(owner, "Close " + p.Name + " first?", p.Name + " is open. Its window closes for the copy, so the files are complete, then reopens.", "Close and copy", "Cancel") != 0) return;
            await Task.Run(() => Launcher.Stop(p.LaunchDir));
        }
        ProfileOps.DuplicateResult r = await Task.Run(() => ProfileOps.Duplicate(p.Name, name, null));
        if (snap.Pid(p) != 0) Launcher.Spawn(p);
        if (r.Problem != null) { await Dialogs.Info(owner, "Couldn't duplicate it", r.Problem); return; }
        await Dialogs.Info(owner, name + " is ready",
            "Copied " + new RemovalResult { Bytes = r.Bytes }.Size + ". Open " + name + " and sign in; its chats, sessions and settings are already there.");
        reload();
    }

    // ---- MCP servers -------------------------------------------------------------------

    public static async Task CopyMcp(Window owner, Snapshot snap, ProfileEntry from, ProfileEntry to, Action reload, Action<Action, int> later)
    {
        var (desktop, code) = ProfileOps.McpServersOf(from);
        if (desktop.Count + code.Count == 0)
        {
            await Dialogs.Info(owner, "Nothing to copy", from.Name + " has no MCP servers set up.");
            return;
        }
        string list = (desktop.Count > 0 ? "Claude Desktop: " + string.Join(", ", desktop) : "") +
                      (desktop.Count > 0 && code.Count > 0 ? "\n" : "") +
                      (code.Count > 0 ? "Claude Code: " + string.Join(", ", code) : "");
        bool open = snap.Pid(to) != 0;
        int c = await Dialogs.Ask(owner, "Copy MCP servers from " + from.Name + " to " + to.Name + "?",
            list + "\n\nServers with the same name in " + to.Name + " are replaced; its other servers stay. " + to.Name + "'s config is backed up first. " +
            "Servers can hold API keys in their settings, so only copy between your own profiles." +
            (open ? "\n\n" + to.Name + " is open and only loads servers when it starts." : ""),
            open ? new[] { "Copy and restart " + to.Name, "Copy only", "Cancel" } : new[] { "Copy", "Cancel" });
        if (c < 0 || c == (open ? 2 : 1)) return;

        ProfileOps.McpCopyResult r = await Task.Run(() => ProfileOps.CopyMcpServers(from, to));
        if (r.Problem != null) { await Dialogs.Info(owner, "Couldn't copy them", r.Problem); return; }
        if (open && c == 0) later(() => Launcher.Restart(to), 6000);
        await Dialogs.Info(owner, "MCP servers copied",
            (r.Desktop.Count + r.Code.Count) + " copied to " + to.Name + "." + (r.Backup != null ? "\n\nThe previous config is kept at\n" + Platform.Shorten(r.Backup) : ""));
        reload();
    }
}
