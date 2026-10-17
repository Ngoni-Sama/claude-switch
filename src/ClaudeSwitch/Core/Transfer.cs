// Moving sessions between profiles.
//
//  * Handoff: copy one Code-tab session to another profile so another account
//    can continue it (record under the target account, transcript into its store).
//  * Recovery: sessions filed under an account that now lives in a different
//    profile (a sign-in landed in the wrong window) are copied back to it.
//  * Account-change detection: profiles.json remembers each profile's account.
//
// Everything copies, never moves, and never overwrites an existing file.

using System.Text.RegularExpressions;

namespace ClaudeSwitch.Core;

public class CopyResult
{
    public int Records, Transcripts, Skipped;
    public List<string> Errors = new();
}

public class RecoveryItem
{
    public ProfileEntry From, To;
    public string AccountUuid, Email;
    public List<SessionInfo> Sessions;
}

public static class Transfer
{
    // Copying into a folder Claude has open can fail transiently; retry briefly.
    public static bool CopyIfMissing(string src, string dst, CopyResult r)
    {
        if (File.Exists(dst)) { r.Skipped++; return false; }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write))
                    input.CopyTo(output);
                File.SetLastWriteTime(dst, File.GetLastWriteTime(src));
                return true;
            }
            catch (IOException ex)
            {
                if (File.Exists(dst)) { r.Skipped++; return false; }
                if (attempt >= 5) { r.Errors.Add(Path.GetFileName(src) + ": " + ex.Message); return false; }
                Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException ex) { r.Errors.Add(Path.GetFileName(src) + ": " + ex.Message); return false; }
        }
    }

    public static int CopyTreeIfMissing(string srcDir, string dstDir, CopyResult r)
    {
        if (!Directory.Exists(srcDir)) return 0;
        return Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories)
            .Count(f => CopyIfMissing(f, dstDir + f.Substring(srcDir.Length), r));
    }

    static bool WriteIfMissing(string dst, string text, CopyResult r)
    {
        if (File.Exists(dst)) { r.Skipped++; return false; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            using var w = new StreamWriter(new FileStream(dst, FileMode.CreateNew, FileAccess.Write), new System.Text.UTF8Encoding(false));
            w.Write(text);
            return true;
        }
        catch (IOException ex) { r.Errors.Add(Path.GetFileName(dst) + ": " + ex.Message); return false; }
    }

    static readonly Regex TitleRx = new("(\"title\"\\s*:\\s*\")((?:[^\"\\\\]|\\\\.)*)(\")");

    // Edits the record as text so every other field stays byte-for-byte.
    static string AppendTitle(string json, string suffix)
    {
        string safe = suffix.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return TitleRx.Replace(json, m => m.Groups[1].Value + m.Groups[2].Value + safe + m.Groups[3].Value, 1);
    }

    static void CopyTranscript(SessionInfo s, string toConfig, CopyResult r)
    {
        string fromConfig = s.Profile.Config;
        if (Platform.SamePath(fromConfig, toConfig)) return;
        string t = Accounts.FindTranscript(fromConfig, s.CliSessionId);
        if (t == null) return;
        if (CopyIfMissing(t, toConfig + t.Substring(fromConfig.Length), r)) r.Transcripts++;
        string sub = t[..^".jsonl".Length];
        if (Directory.Exists(sub)) CopyTreeIfMissing(sub, toConfig + sub.Substring(fromConfig.Length), r);
    }

    // Handoff: make `to` able to continue session `s`.
    public static CopyResult Handoff(SessionInfo s, ProfileEntry to, AccountInfo toAccount)
    {
        var r = new CopyResult();
        string org = Accounts.OrgFolder(to, toAccount);
        if (toAccount.Uuid == null || org == null) { r.Errors.Add(to.Name + " isn't signed in yet. Open it once and sign in first."); return r; }
        string dst = Path.Combine(to.Data, "claude-code-sessions", toAccount.Uuid, org, Path.GetFileName(s.File));
        if (WriteIfMissing(dst, AppendTitle(Json.ReadShared(s.File), " (from " + s.Profile.Name + ")"), r)) r.Records++;
        CopyTranscript(s, to.Config, r);
        return r;
    }

    public static List<RecoveryItem> FindRecoverable(List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts)
    {
        var items = new List<RecoveryItem>();
        foreach (ProfileEntry from in profiles)
        {
            accounts.TryGetValue(from.Name, out AccountInfo fromAcct);
            string root;
            try { root = Path.Combine(from.Data, "claude-code-sessions"); } catch (Exception) { continue; }
            if (!Directory.Exists(root)) continue;
            foreach (string acctDir in Directory.GetDirectories(root))
            {
                string acct = Path.GetFileName(acctDir);
                if (fromAcct != null && string.Equals(acct, fromAcct.Uuid, StringComparison.OrdinalIgnoreCase)) continue;
                ProfileEntry to = profiles.FirstOrDefault(q => q != from && accounts.TryGetValue(q.Name, out var qa) &&
                                                                string.Equals(qa.Uuid, acct, StringComparison.OrdinalIgnoreCase));
                if (to == null) continue;
                string toRoot = Path.Combine(to.Data, "claude-code-sessions", acct);
                var missing = Accounts.Sessions(from, acct).Where(s => !File.Exists(Path.Combine(toRoot, s.OrgUuid, Path.GetFileName(s.File)))).ToList();
                if (missing.Count > 0)
                    items.Add(new RecoveryItem { From = from, To = to, AccountUuid = acct, Email = accounts[to.Name].Email, Sessions = missing });
            }
        }
        return items;
    }

    // Same account, so records keep their org folder and title.
    public static CopyResult Recover(RecoveryItem item)
    {
        var r = new CopyResult();
        foreach (SessionInfo s in item.Sessions)
        {
            if (CopyIfMissing(s.File, Path.Combine(item.To.Data, "claude-code-sessions", item.AccountUuid, s.OrgUuid, Path.GetFileName(s.File)), r)) r.Records++;
            CopyTranscript(s, item.To.Config, r);
        }
        CopyTreeIfMissing(Path.Combine(item.From.Data, "local-agent-mode-sessions", item.AccountUuid),
                          Path.Combine(item.To.Data, "local-agent-mode-sessions", item.AccountUuid), r);
        return r;
    }

    public static bool AccountChanged(ProfileEntry p, AccountInfo a) =>
        p.AccountUuid != null && a.Uuid != null && a.SignedIn && !string.Equals(p.AccountUuid, a.Uuid, StringComparison.OrdinalIgnoreCase);

    // The profile whose remembered account is now signed in to `p`, when the
    // two have simply swapped (the classic wrong-window sign-in).
    public static ProfileEntry SwapPartner(ProfileEntry p, List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts)
    {
        if (!accounts.TryGetValue(p.Name, out var pa)) return null;
        return profiles.FirstOrDefault(q => q != p && accounts.TryGetValue(q.Name, out var qa) &&
            string.Equals(q.AccountUuid, pa.Uuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.AccountUuid, qa.Uuid, StringComparison.OrdinalIgnoreCase));
    }

    // Records accounts seen for the first time. True if anything changed.
    public static bool Remember(List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts)
    {
        bool changed = false;
        foreach (ProfileEntry p in profiles)
        {
            if (!accounts.TryGetValue(p.Name, out var a) || !a.SignedIn || a.Uuid == null) continue;
            if (p.AccountUuid == null) { p.AccountUuid = a.Uuid; changed = true; }
            if (string.Equals(p.AccountUuid, a.Uuid, StringComparison.OrdinalIgnoreCase) && a.Email != null && p.Email != a.Email) { p.Email = a.Email; changed = true; }
        }
        return changed;
    }

    public static void Accept(ProfileEntry p, AccountInfo a) { p.AccountUuid = a.Uuid; p.Email = a.Email; }

    // Swaps which data folders (where each sign-in lives) two names point at.
    // The remembered account and Claude Code memory belong to the name and stay.
    public static void SwapNames(ProfileEntry a, ProfileEntry b) => (a.DataDir, b.DataDir) = (b.DataDir, a.DataDir);
}
