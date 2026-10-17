// Made-up profiles, usage and sessions for screenshots and videos
// (--demo), so nothing real (emails, project names) ever ends up in them.
// Nothing here touches disk: the data folders don't exist.

namespace ClaudeSwitch.Core;

public static class Demo
{
    public static bool On { get; set; }

    static Bucket B(int pct, TimeSpan resetIn, bool resetSince = false) => new()
    {
        Pct = pct, ResetSince = resetSince, ResetAt = DateTime.Now + resetIn, Exact = true, AsOf = DateTime.Now.AddMinutes(-3),
    };

    public static Snapshot Snapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "claude-switch-demo");
        ProfileEntry P(string name, string color, string email) => new()
        {
            Name = name, Color = color, Email = email,
            DataDir = Path.Combine(root, "Claude-" + name), ConfigDir = Path.Combine(root, ".claude-" + name.ToLowerInvariant()),
        };
        var work = P("Work", "#D97757", "alex@work.example.com");
        var personal = P("Personal", "#3B82F6", "alex.rivera@example.com");
        var client = P("Client", "#16A34A", "alex@client.example.net");

        var s = new Snapshot
        {
            Profiles = new List<ProfileEntry> { work, personal, client },
            Running = new Dictionary<string, int> { [work.Key] = 4120, [personal.Key] = 5388 },
            ClaudeInstalled = true, LinkActive = true, LinkRegistered = true, Signature = "demo",
        };

        AccountInfo A(string uuid, string email, string org, Bucket session, Bucket weekly) => new()
        {
            Uuid = uuid, Email = email, OrgName = org, SignedIn = true, Session = session, Weekly = weekly,
            UsageAt = DateTime.Now.AddMinutes(-3), FromReport = true,
        };
        s.Accounts["Work"] = A("aaaaaaaa-0000-4000-8000-000000000001", work.Email, "Northwind Studio", B(86, TimeSpan.FromMinutes(97)), B(48, TimeSpan.FromDays(3.4)));
        s.Accounts["Work"].Extra.Add(new("Weekly · Opus", B(61, TimeSpan.FromDays(3.4))));
        s.Accounts["Personal"] = A("aaaaaaaa-0000-4000-8000-000000000002", personal.Email, "Personal", B(14, TimeSpan.FromMinutes(212)), B(27, TimeSpan.FromDays(5.1)));
        s.Accounts["Client"] = A("aaaaaaaa-0000-4000-8000-000000000003", client.Email, "Contoso Ltd", B(0, TimeSpan.FromMinutes(-40), resetSince: true), B(66, TimeSpan.FromDays(1.7)));

        string[] workTitles =
        {
            "Refactor the checkout flow", "Fix the flaky login test", "Add dark mode to settings", "Write the orders table migration",
            "Speed up image uploads", "Draft the v2 API docs", "Track down the worker memory leak", "Cache dependencies in CI",
            "Add CSV export to reports", "Tidy up the onboarding emails", "Paginate the admin user list",
        };
        string[] personalTitles = { "Plan a weekend trip itinerary", "Budget spreadsheet formulas", "Build a recipe scraper", "Learn Rust ownership", "Garden watering scheduler" };
        string[] clientTitles = { "Audit the landing page SEO", "Migrate forms to the new CRM", "Fix mobile menu overflow" };
        string[] repos = { "~/code/shop-web", "~/code/api", "~/code/infra", "~/code/mobile" };

        List<SessionInfo> Sessions(ProfileEntry p, string[] titles, int minutesApart)
        {
            AccountInfo a = s.Accounts[p.Name];
            return titles.Select((t, i) => new SessionInfo
            {
                Profile = p, Title = t, Cwd = repos[i % repos.Length], AccountUuid = a.Uuid,
                SessionId = "demo-" + p.Name + "-" + i, LastActivity = DateTime.Now.AddMinutes(-(8 + i * minutesApart)),
            }).ToList();
        }
        s.Sessions["Work"] = Sessions(work, workTitles, 47);
        s.Sessions["Personal"] = Sessions(personal, personalTitles, 190);
        s.Sessions["Client"] = Sessions(client, clientTitles, 600);

        s.Unify = new UnifyStatus
        {
            Members = new List<string> { "Work", "Personal" },
            Linked = new Dictionary<string, bool> { ["Work"] = true, ["Personal"] = true },
            SharedDir = Path.Combine(root, "shared"), Projects = 7, MemoryNotes = 24, Transcripts = 38,
        };

        s.Updates = new UpdateStatus { NewestCode = "2.1.4" };
        s.Updates.Installs.Add(new ClaudeInstall
        {
            Kind = Platform.IsWindows ? "Microsoft Store" : Platform.IsMac ? "app bundle" : "apt package",
            Version = "1.3.7", Path = Path.Combine(root, "Claude"), IsPrimary = true, Updated = DateTime.Now.AddDays(-2),
        });
        foreach (ProfileEntry p in s.Profiles) s.Updates.CodeVersions[p.Name] = "2.1.4";
        return s;
    }
}
