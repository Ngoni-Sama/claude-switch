// Unify mode: make two or more profiles share one memory and one set of
// conversations, so when an account runs out of usage the other can carry on
// from exactly where it left off.
//
// How the sharing is real rather than copied:
//
//   <profile A config>/projects  ─┐
//                                 ├─→  <AppDir>/shared/projects   (one real folder)
//   <profile B config>/projects  ─┘
//
// `projects/` is where Claude Code keeps, per project folder:
//   * memory/*.md    the notes Claude writes about that project
//   * CLAUDE.md      project instructions
//   * <id>.jsonl     the transcript of every conversation
//
// Each profile keeps its own `.claude.json` (its own account identity) and its
// own sign-in. Only memory and conversations are shared.
//
// Claude Desktop lists Code sessions per account, so each account also needs
// its own small record file pointing at the shared transcript. Mirror() keeps
// those in step; the transcripts themselves are never duplicated.
//
// Everything is reversible: Unlink() turns a profile's link back into an
// ordinary folder holding a copy of the shared content, and the folder each
// profile had before linking is kept as `projects.before-unify-<timestamp>`.

using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

public class UnifyStatus
{
    public List<string> Members = new();              // profile names in the group
    public Dictionary<string, bool> Linked = new();   // name -> its projects/ really is the shared folder
    public string SharedDir;
    public int Projects, MemoryNotes, Transcripts;

    public bool On => Members.Count >= 2;
    public bool Healthy => On && Members.All(m => Linked.TryGetValue(m, out bool ok) && ok);
    public List<string> Broken => Members.Where(m => !Linked.TryGetValue(m, out bool ok) || !ok).ToList();
}

public class UnifyResult
{
    public int Merged, Mirrored;
    public List<string> Notes = new(), Errors = new();
    public bool Ok => Errors.Count == 0;
}

public static class Unify
{
    public static string SharedRoot => Path.Combine(Platform.AppDir, "shared");
    public static string SharedProjects => Path.Combine(SharedRoot, "projects");

    static string ProjectsOf(ProfileEntry p) => Path.Combine(p.Config, "projects");

    // ---- group membership (settings.json "unify": ["Work","Personal"]) ----------

    public static List<string> Members()
    {
        var list = new List<string>();
        foreach (JsonNode n in Settings.UnifyMembers()) if (n is JsonValue v) list.Add(v.ToString());
        return list;
    }

    public static bool IsMember(ProfileEntry p) =>
        Members().Any(m => string.Equals(m, p.Name, StringComparison.OrdinalIgnoreCase));

    public static UnifyStatus Status(List<ProfileEntry> profiles)
    {
        var s = new UnifyStatus { SharedDir = SharedProjects };
        foreach (string name in Members())
        {
            ProfileEntry p = Profiles.Find(profiles, name);
            if (p == null) continue;   // profile was removed; drop it silently
            s.Members.Add(p.Name);
            s.Linked[p.Name] = FsLink.PointsAt(ProjectsOf(p), SharedProjects);
        }
        if (Directory.Exists(SharedProjects))
        {
            try
            {
                string[] dirs = Directory.GetDirectories(SharedProjects);
                s.Projects = dirs.Length;
                foreach (string d in dirs)
                {
                    s.Transcripts += Directory.GetFiles(d, "*.jsonl").Length;
                    string mem = Path.Combine(d, "memory");
                    if (Directory.Exists(mem)) s.MemoryNotes += Directory.GetFiles(mem, "*.md").Length;
                }
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }
        return s;
    }

    // ---- linking ----------------------------------------------------------------

    // Profiles must not be running: Claude holds files in projects/ open.
    public static List<string> Running(IEnumerable<ProfileEntry> profiles)
    {
        Dictionary<string, int> running = ClaudeApp.FindRunning();
        return profiles.Where(p => running.ContainsKey(p.Key)).Select(p => p.Name).ToList();
    }

    // Turns each profile's projects/ into a link to the shared folder, merging
    // whatever each one already had. Existing files are never overwritten.
    public static UnifyResult Link(List<ProfileEntry> members)
    {
        var r = new UnifyResult();
        if (members.Count < 2) { r.Errors.Add("Pick at least two profiles to unify."); return r; }
        List<string> open = Running(members);
        if (open.Count > 0) { r.Errors.Add("Close the Claude window of " + string.Join(" and ", open) + " first."); return r; }

        try { Directory.CreateDirectory(SharedProjects); }
        catch (Exception ex) { ErrorLog.Write(ex); r.Errors.Add("Couldn't create the shared folder: " + ex.Message); return r; }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (ProfileEntry p in members)
        {
            string projects = ProjectsOf(p);
            try
            {
                if (FsLink.PointsAt(projects, SharedProjects)) continue;        // already unified
                if (FsLink.IsLink(projects)) FsLink.Remove(projects);           // points somewhere else
                else if (Directory.Exists(projects))
                {
                    r.Merged += FsLink.MergeInto(projects, SharedProjects);
                    // Keep what this profile had, so unlinking can always go back.
                    string backup = projects + ".before-unify-" + stamp;
                    Directory.Move(projects, backup);
                    r.Notes.Add(p.Name + "'s previous memory is kept at " + Platform.Shorten(backup) + ".");
                }
                FsLink.Create(projects, SharedProjects);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                r.Errors.Add(p.Name + ": " + ex.Message);
            }
        }
        if (r.Ok) Settings.SetUnifyMembers(members.Select(m => m.Name).ToList());
        return r;
    }

    // Gives one profile its own ordinary projects/ folder back, holding a copy
    // of everything shared. The other members keep sharing.
    public static UnifyResult Unlink(ProfileEntry p, List<ProfileEntry> profiles)
    {
        var r = new UnifyResult();
        List<string> open = Running(new[] { p });
        if (open.Count > 0) { r.Errors.Add("Close " + p.Name + "'s Claude window first."); return r; }

        string projects = ProjectsOf(p);
        try
        {
            if (FsLink.IsLink(projects))
            {
                FsLink.Remove(projects);
                if (Directory.Exists(SharedProjects)) FsLink.CopyTree(SharedProjects, projects);
                r.Notes.Add(p.Name + " now has its own copy of the shared memory and conversations.");
            }
            List<string> left = Members().Where(m => !string.Equals(m, p.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            // One profile on its own isn't a group.
            Settings.SetUnifyMembers(left.Count >= 2 ? left : new List<string>());
            if (left.Count == 1) r.Notes.Add("Unify mode is off: " + left[0] + " was the only profile left.");
        }
        catch (Exception ex) { ErrorLog.Write(ex); r.Errors.Add(ex.Message); }
        return r;
    }

    public static UnifyResult UnlinkAll(List<ProfileEntry> profiles)
    {
        var r = new UnifyResult();
        foreach (string name in Members().ToList())
        {
            ProfileEntry p = Profiles.Find(profiles, name);
            if (p == null) continue;
            UnifyResult one = Unlink(p, profiles);
            r.Merged += one.Merged;
            r.Notes.AddRange(one.Notes);
            r.Errors.AddRange(one.Errors);
        }
        Settings.SetUnifyMembers(new List<string>());
        return r;
    }

    // ---- session mirroring --------------------------------------------------------

    // Each account lists its own session records, so give every member a record
    // for every unified session. The transcript each record points at is the
    // shared one, so continuing in the other account resumes the same
    // conversation. Records are copied, never moved, and never overwritten.
    public static UnifyResult Mirror(List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts)
    {
        var r = new UnifyResult();
        List<ProfileEntry> members = Members().Select(n => Profiles.Find(profiles, n)).Where(p => p != null).ToList();
        if (members.Count < 2) return r;

        foreach (ProfileEntry to in members)
        {
            if (!accounts.TryGetValue(to.Name, out AccountInfo ta) || ta.Uuid == null || !ta.SignedIn) continue;
            string org = Accounts.OrgFolder(to, ta);
            if (org == null) continue;
            string toDir = Path.Combine(to.Data, "claude-code-sessions", ta.Uuid, org);

            // Which sessions does this account already know about?
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SessionInfo s in Accounts.Sessions(to, ta.Uuid))
                if (s.CliSessionId != null) known.Add(s.CliSessionId);

            foreach (ProfileEntry from in members)
            {
                if (from == to || !accounts.TryGetValue(from.Name, out AccountInfo fa) || fa.Uuid == null) continue;
                foreach (SessionInfo s in Accounts.Sessions(from, fa.Uuid))
                {
                    if (s.Archived || s.CliSessionId == null || known.Contains(s.CliSessionId)) continue;
                    // Only mirror what the shared folder can actually resume.
                    if (Accounts.FindTranscript(to.Config, s.CliSessionId) == null) continue;
                    string dst = Path.Combine(toDir, Path.GetFileName(s.File));
                    if (File.Exists(dst)) continue;
                    try
                    {
                        Directory.CreateDirectory(toDir);
                        File.Copy(s.File, dst);
                        known.Add(s.CliSessionId);
                        r.Mirrored++;
                    }
                    catch (IOException ex) { ErrorLog.Write(ex); }
                    catch (UnauthorizedAccessException ex) { ErrorLog.Write(ex); }
                }
            }
        }
        return r;
    }

    // Called on every dashboard refresh: cheap when there's nothing new.
    public static void MirrorInBackground(List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts, Action<UnifyResult> done = null)
    {
        if (Members().Count < 2) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                UnifyResult r = Mirror(profiles, accounts);
                if (r.Mirrored > 0) done?.Invoke(r);
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        });
    }

    // The member of the group with the most usage left, for "carry on in …".
    public static ProfileEntry BestAlternative(ProfileEntry from, List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts)
    {
        return Members()
            .Select(n => Profiles.Find(profiles, n))
            .Where(p => p != null && p != from && accounts.TryGetValue(p.Name, out AccountInfo a) && a.SignedIn && !a.AtLimit)
            .OrderBy(p => Headroom(accounts[p.Name]))
            .FirstOrDefault();
    }

    // Lower is better: the fullest bucket decides how much room is left.
    static int Headroom(AccountInfo a) => Math.Max(a.Session?.Shown ?? 0, a.Weekly?.Shown ?? 0);
}
