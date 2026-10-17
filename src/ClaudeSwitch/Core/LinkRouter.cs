// Windows: optional routing of claude:// links (sign-in emails, browser
// sign-in) to the right profile.
//
// Windows resolves claude:// to the Claude Store package, which always opens
// the default window, so a sign-in meant for another profile lands there and
// switches its account. Programs can't change that default (Windows protects
// it), so this only registers Claude Switch as a candidate handler, per user.
// The user then picks it once in Settings > Default apps.

using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeSwitch.Core;

public static class LinkRouter
{
    const string ProgId = "ClaudeSwitch.Link";
    const string AppKey = @"Software\ClaudeSwitch";
    const string CapKey = AppKey + @"\Capabilities";
    const string AppName = "Claude Switch";

    public static Func<string, string, List<ProfileEntry>, Dictionary<string, AccountInfo>, Dictionary<string, int>, ProfileEntry> PickProfile;

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public static bool Supported => Platform.IsWindows;

    public static bool IsRegistered
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId);
            return k != null;
        }
    }

    // True once the user has picked Claude Switch for claude:// links.
    public static bool IsActive
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\claude\UserChoice");
            return k != null && string.Equals(k.GetValue("ProgId") as string, ProgId, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Register()
    {
        if (!OperatingSystem.IsWindows()) return;
        string exe = Platform.ExePath;
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId)) { k.SetValue("", "Claude link (Claude Switch)"); k.SetValue("FriendlyTypeName", "Claude link"); }
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + @"\DefaultIcon")) k.SetValue("", "\"" + exe + "\",0");
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command")) k.SetValue("", "\"" + exe + "\" --link \"%1\"");
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CapKey))
        {
            k.SetValue("ApplicationName", AppName);
            k.SetValue("ApplicationDescription", "Opens Claude sign-in links in the right profile");
            k.SetValue("ApplicationIcon", "\"" + exe + "\",0");
        }
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CapKey + @"\URLAssociations")) k.SetValue("claude", ProgId);
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) k.SetValue(AppName, CapKey);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // SHCNE_ASSOCCHANGED
    }

    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false); } catch (Exception) { }
        try { Registry.CurrentUser.DeleteSubKeyTree(AppKey, false); } catch (Exception) { }
        try { using RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true); k?.DeleteValue(AppName, false); } catch (Exception) { }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(AppName)) { UseShellExecute = true }); }
        catch (Exception) { try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch (Exception) { } }
    }

    // Called as  ClaudeSwitch --link "claude://..."
    public static int Handle(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("claude:", StringComparison.OrdinalIgnoreCase)) return 1;
        List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
        Dictionary<string, int> running = ClaudeApp.FindRunning();
        var accounts = profiles.ToDictionary(p => p.Name, Accounts.Read);
        var open = profiles.Where(p => running.ContainsKey(p.Key)).ToList();
        var waiting = open.Where(p => !accounts[p.Name].SignedIn).ToList();

        // A window waiting for sign-in is almost certainly who this link is for.
        ProfileEntry target = waiting.Count == 1 ? waiting[0]
            : open.Count == 1 ? open[0]
            : PickProfile?.Invoke("Open this Claude link in…",
                "Choose the profile that should receive this link. If you're signing in, pick the window that's waiting for it.",
                profiles, accounts, running);
        if (target != null) Launcher.Forward(target, url);
        return 0;
    }
}
