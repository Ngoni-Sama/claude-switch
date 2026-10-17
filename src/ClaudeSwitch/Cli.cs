// The command-line side, for scripts, shell aliases and Claude Code itself.
// Output goes to the terminal that ran the command. On Windows the app is a
// windowed program, so it attaches to that terminal's console first.

using System.Runtime.InteropServices;
using ClaudeSwitch.Core;

namespace ClaudeSwitch;

public static class Cli
{
    static readonly string[] Verbs = { "--list", "--open", "--diagnose", "--check-update", "--version", "--taskbar-check", "--help", "-h", "/?" };

    public static bool Handles(string[] args) => args.Length > 0 && Verbs.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    // A windowed program gets no console. When output is redirected (a pipe
    // or a file) it already works; when you type the command in a terminal,
    // attach to that terminal and write to it directly.
    static void UseParentConsole()
    {
        if (!Platform.IsWindows) return;
        uint type = GetFileType(GetStdHandle(-11));
        if (type == 1 /* disk */ || type == 3 /* pipe */) return;
        if (!AttachConsole(-1)) return;
        IntPtr con = CreateFile("CONOUT$", 0x40000000 /* write */, 0x2 /* share write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero);
        if (con == IntPtr.Zero || con == new IntPtr(-1)) return;
        var o = new StreamWriter(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(con, true), FileAccess.Write)) { AutoFlush = true };
        Console.SetOut(o);
        Console.SetError(o);
        Console.WriteLine();   // the prompt is already printed; start on a fresh line
    }

    public static int Run(string[] args)
    {
        UseParentConsole();
        string verb = args[0].ToLowerInvariant();
        bool plain = args.Contains("--plain", StringComparer.OrdinalIgnoreCase);
        try
        {
            switch (verb)
            {
                case "--version":
                    Console.WriteLine("Claude Switch " + SelfUpdate.Current.ToString(3));
                    return 0;

                case "--list":
                {
                    List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
                    Dictionary<string, int> running = ClaudeApp.FindRunning();
                    if (plain) Console.WriteLine("name\temail\topen\tcolor\tcommand\tdata\tconfig");
                    foreach (ProfileEntry p in profiles)
                    {
                        AccountInfo a = Accounts.Read(p);
                        bool open = running.ContainsKey(p.Key);
                        if (plain)
                            Console.WriteLine(string.Join("\t", p.Name, a.Email ?? "-", open ? "1" : "0", Profiles.ColorOf(profiles, p),
                                Terminal.CommandName(p), p.Data, p.Config));
                        else
                            Console.WriteLine("{0,-16} {1,-34} {2,-7} {3}", p.Name, a.SignedIn ? a.Email ?? "signed in" : "not signed in",
                                open ? "open" : "closed", Terminal.CommandName(p));
                    }
                    if (profiles.Count == 0) Console.WriteLine("No profiles yet. Open Claude Switch to set them up.");
                    return 0;
                }

                case "--open":
                {
                    string name = args.Length > 1 ? args[1] : null;
                    ProfileEntry p = name == null ? null : Profiles.Find(Profiles.LoadOrEmpty(), name);
                    if (p == null) { Console.Error.WriteLine("No profile named \"" + name + "\". Try --list."); return 2; }
                    Launcher.Activate(p, ClaudeApp.FindMain(p.LaunchDir));
                    Console.WriteLine("Opening " + p.Name + ".");
                    return 0;
                }

                case "--diagnose":
                {
                    Diagnose.Report r = Diagnose.Run(args.Contains("--show-emails", StringComparer.OrdinalIgnoreCase));
                    Console.Write(r.Text);
                    return r.Healthy ? 0 : 1;
                }

                case "--taskbar-check":
                {
                    // Diagnostics: set a profile's taskbar identity, read it back, then clear it.
                    List<ProfileEntry> all = Profiles.LoadOrEmpty();
                    ProfileEntry p = args.Length > 1 ? Profiles.Find(all, args[1]) : null;
                    if (p == null) { Console.Error.WriteLine("Usage: --taskbar-check <profile>"); return 2; }
                    int pid = ClaudeApp.FindMain(p.LaunchDir);
                    if (pid == 0) { Console.Error.WriteLine(p.Name + " isn't open."); return 1; }
                    Console.WriteLine("before:  " + (TaskbarIdentity.Read(pid) ?? "(none)"));
                    int n = TaskbarIdentity.Apply(p, pid, ProfileIcon.Write(p.Slug, Profiles.ColorOf(all, p)));
                    Console.WriteLine("applied to " + n + " window(s); now: " + (TaskbarIdentity.Read(pid) ?? "(none)"));
                    bool keep = args.Contains("--keep", StringComparer.OrdinalIgnoreCase);
                    if (!keep) { Thread.Sleep(int.TryParse(args.SkipWhile(a => a != "--hold").Skip(1).FirstOrDefault(), out int s) ? s * 1000 : 0); TaskbarIdentity.Clear(pid); }
                    Console.WriteLine("after:   " + (TaskbarIdentity.Read(pid) ?? "(none)") + (keep ? "  (kept)" : "  (cleared)"));
                    return 0;
                }

                case "--check-update":
                {
                    Console.WriteLine("Installed: " + SelfUpdate.Current.ToString(3));
                    ReleaseInfo r = SelfUpdate.CheckAsync().GetAwaiter().GetResult();
                    Console.WriteLine(r == null ? "Up to date." : "Available: " + r.Version.ToString(3) + "  " + r.PageUrl);
                    return 0;
                }

                default:
                    Console.WriteLine("""
                        Claude Switch: run several Claude Desktop accounts side by side.

                          ClaudeSwitch                     open the dashboard
                          ClaudeSwitch --list [--plain]    profiles, their accounts, and which are open
                          ClaudeSwitch --open <name>       open or bring forward a profile
                          ClaudeSwitch --diagnose          health report (emails masked; --show-emails to include them)
                          ClaudeSwitch --check-update      compare with the latest release
                          ClaudeSwitch --version
                        """);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }
}
