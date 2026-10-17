// Usage limits with reset times.
//
// Sources, best first:
//   1. A copied "Claude Code usage report" (UsageReport): live % + exact resets.
//   2. Claude Code transcripts: a rejected turn records
//        "quotaLimits":{"status":"rejected","resetsAt":<epoch s>,"rateLimitType":"five_hour"|"seven_day"}
//      giving exact 5-hour resets and the weekly "anchor" (same time every week).
//   3. plan-usage-history.json readings (written only now and then).
// Usage only grows inside a window, so a reading holds until that window's
// reset and is 0% (all available) after it. A 5-hour window ends at most
// 5 h after any reading taken inside it.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSwitch.Core;

public class LimitEvent
{
    public string Type, Status, Account;   // Type: five_hour | seven_day
    public DateTime ResetsAt, LoggedAt;
}

// One usage bucket as Claude's own panel shows it.
public class Bucket
{
    public int Pct = -1;                          // -1 = no reading yet
    public bool ResetSince;                       // reading predates a reset: now 0%
    public DateTime ResetAt = DateTime.MinValue;  // next (or the passed) reset
    public bool Exact;                            // ResetAt known exactly, not just "at the latest"
    public DateTime AsOf = DateTime.MinValue;

    public int Shown => ResetSince ? 0 : Pct;
    public bool AtLimit => !ResetSince && Pct >= 95;

    static string Clock(DateTime t)
    {
        string time = t.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (t.Date == DateTime.Today) return time;
        if (t.Date == DateTime.Today.AddDays(1)) return "tomorrow " + time;
        if (Math.Abs((t.Date - DateTime.Today).TotalDays) < 6) return t.ToString("ddd ", CultureInfo.InvariantCulture) + time;
        return t.ToString("ddd d MMM, ", CultureInfo.InvariantCulture) + time;
    }

    public static string In(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return "less than a minute";
        int h = (int)d.TotalHours, m = d.Minutes;
        return ((h > 0 ? h + " hr " : "") + (m > 0 || h == 0 ? m + " min" : "")).Trim();
    }

    // Same wording as Claude's usage panel where possible.
    public string ResetText
    {
        get
        {
            if (Pct < 0) return "No reading yet: copy Claude's usage report to update";
            if (ResetAt == DateTime.MinValue) return "";
            if (ResetSince) return (Exact ? "Reset at " : "Reset by ") + Clock(ResetAt) + " · usage since then unknown";
            TimeSpan d = ResetAt - DateTime.Now;
            if (!Exact) return "Resets by " + Clock(ResetAt) + " at the latest";
            return d.TotalHours < 24 ? "Resets in " + In(d) : "Resets " + Clock(ResetAt);
        }
    }
}

public static class Limits
{
    static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    static readonly Regex QuotaRx = new("\"quotaLimits\":\\{\"status\":\"(\\w+)\",\"resetsAt\":(\\d+)[^}]*?\"rateLimitType\":\"(\\w+)\"");
    static readonly Regex TimeRx = new("\"timestamp\":\"([^\"]+)\"");
    static readonly byte[] Marker = Encoding.ASCII.GetBytes("\"quotaLimits\"");

    class FileScan { public long Offset; public List<LimitEvent> Events = new(); }
    static readonly Dictionary<string, FileScan> cache = new(StringComparer.OrdinalIgnoreCase);

    // Transcripts are append-only (tens of MB): read only what's new, search
    // the raw bytes and decode just the matching lines.
    static List<LimitEvent> Scan(string file)
    {
        FileScan fs;
        lock (cache) { if (!cache.TryGetValue(file, out fs)) cache[file] = fs = new FileScan(); }
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            if (stream.Length < fs.Offset) { fs.Offset = 0; fs.Events.Clear(); }
            if (stream.Length == fs.Offset) return fs.Events;
            stream.Position = fs.Offset;

            // Read in 1 MB chunks (transcripts reach tens of MB). Each chunk is
            // cut at its last newline; the partial line carries over.
            byte[] buf = new byte[1 << 20];
            int carry = 0;
            while (true)
            {
                if (carry == buf.Length) Array.Resize(ref buf, buf.Length * 2);   // one very long line
                int n = stream.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                int len = carry + n;
                int end = Array.LastIndexOf(buf, (byte)'\n', len - 1);
                if (end < 0) { carry = len; continue; }
                ScanLines(buf.AsSpan(0, end + 1), fs.Events);
                fs.Offset += end + 1;
                carry = len - (end + 1);
                Buffer.BlockCopy(buf, end + 1, buf, 0, carry);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return fs.Events;
    }

    // Finds `"quotaLimits"` in complete lines and decodes only those lines.
    static void ScanLines(ReadOnlySpan<byte> span, List<LimitEvent> events)
    {
        int pos = 0;
        while (pos < span.Length)
        {
            int hit = span.Slice(pos).IndexOf(Marker);
            if (hit < 0) return;
            hit += pos;
            int lineStart = span.Slice(0, hit).LastIndexOf((byte)'\n') + 1;
            int rel = span.Slice(hit).IndexOf((byte)'\n');
            int lineEnd = rel < 0 ? span.Length : hit + rel;
            pos = lineEnd + 1;
            string line = Encoding.UTF8.GetString(span.Slice(lineStart, lineEnd - lineStart));
            Match m = QuotaRx.Match(line);
            if (!m.Success) continue;
            Match t = TimeRx.Match(line);
            events.Add(new LimitEvent
            {
                Status = m.Groups[1].Value,
                ResetsAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[2].Value)).LocalDateTime,
                Type = m.Groups[3].Value,
                LoggedAt = t.Success && DateTimeOffset.TryParse(t.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lt)
                    ? lt.LocalDateTime : DateTime.MinValue,
            });
        }
    }

    static volatile bool warm;
    static int warming;

    // The first scan reads every recent transcript (can take seconds). Until it
    // has run once, return nothing and warm up in the background; callers still
    // have copied reports and saved weekly resets meanwhile.
    public static Dictionary<string, List<LimitEvent>> CollectIfReady(List<ProfileEntry> profiles, Action onReady)
    {
        if (warm) return Collect(profiles);
        if (Interlocked.Exchange(ref warming, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Collect(profiles); } catch (Exception ex) { ErrorLog.Write(ex); }
                warm = true;
                warming = 0;
                onReady?.Invoke();
            });
        }
        return new Dictionary<string, List<LimitEvent>>(StringComparer.OrdinalIgnoreCase);
    }

    // Limit events per account. A transcript belongs to the account whose
    // Code-tab record points at it (cliSessionId).
    public static Dictionary<string, List<LimitEvent>> Collect(List<ProfileEntry> profiles)
    {
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProfileEntry p in profiles)
        {
            try { stores.Add(p.Config); } catch (Exception) { }
            foreach (SessionInfo s in Accounts.Sessions(p, null))
                if (!string.IsNullOrEmpty(s.CliSessionId)) owner.TryAdd(s.CliSessionId, s.AccountUuid);
        }

        var result = new Dictionary<string, List<LimitEvent>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>();
        DateTime cutoff = DateTime.Now.AddDays(-8);   // weekly resets older than this are remembered in settings
        foreach (string store in stores)
        {
            string projects = Path.Combine(store, "projects");
            if (!Directory.Exists(projects)) continue;
            foreach (string dir in Directory.GetDirectories(projects))
            {
                foreach (string f in Directory.GetFiles(dir, "*.jsonl"))
                {
                    if (!owner.TryGetValue(Path.GetFileNameWithoutExtension(f), out string account)) continue;
                    if (File.GetLastWriteTime(f) < cutoff) continue;
                    foreach (LimitEvent e in Scan(f))
                    {
                        // The same transcript can exist in two stores (restored copies).
                        if (!seen.Add(account + "|" + e.Type + "|" + e.ResetsAt.Ticks + "|" + e.LoggedAt.Ticks)) continue;
                        if (!result.TryGetValue(account, out var list)) result[account] = list = new List<LimitEvent>();
                        list.Add(new LimitEvent { Type = e.Type, Status = e.Status, ResetsAt = e.ResetsAt, LoggedAt = e.LoggedAt, Account = account });
                    }
                }
            }
        }
        return result;
    }

    // Works out every bucket for an account from its reading, report and events.
    public static void Apply(AccountInfo a, List<LimitEvent> events)
    {
        events = events == null ? new List<LimitEvent>() : new List<LimitEvent>(events);
        DateTime now = DateTime.Now;

        UsageReportData rep = a.Uuid != null ? UsageReport.Load(a.Uuid) : null;
        a.Extra = new List<KeyValuePair<string, Bucket>>();
        if (rep != null)
        {
            foreach (ReportLimit l in rep.Limits.Where(l => l.IsSession || l.IsWeeklyAll))
                events.Add(new LimitEvent { Type = l.IsSession ? "five_hour" : "seven_day", Status = "report", ResetsAt = l.ResetsAt, LoggedAt = rep.At, Account = a.Uuid });
            if (rep.At > a.UsageAt)
            {
                if (rep.Session != null) a.FiveHour = rep.Session.Pct;
                if (rep.Weekly != null) a.SevenDay = rep.Weekly.Pct;
                a.UsageAt = rep.At;
                a.FromReport = true;
                foreach (ReportLimit l in rep.Limits.Where(l => !l.IsSession && !l.IsWeeklyAll))
                    a.Extra.Add(new(l.Label, new Bucket { Pct = l.Pct, AsOf = rep.At, ResetAt = l.ResetsAt, Exact = true, ResetSince = now >= l.ResetsAt }));
            }
        }

        DateTime anchor = events.Where(e => e.Type == "seven_day").Select(e => e.ResetsAt).DefaultIfEmpty(DateTime.MinValue).Max();
        if (a.Uuid != null)
        {
            if (anchor != DateTime.MinValue) Settings.SetWeeklyAnchor(a.Uuid, anchor);
            else anchor = Settings.WeeklyAnchor(a.Uuid);
        }
        a.Session = FiveHour(a, events, now);
        a.Weekly = Weekly(a, events, anchor, now);
    }

    static DateTime NextWeekly(DateTime anchor, DateTime after)
    {
        DateTime r = anchor.AddDays(Math.Ceiling((after - anchor).TotalDays / 7.0) * 7);
        return r <= after ? r.AddDays(7) : r;
    }

    static Bucket FiveHour(AccountInfo a, List<LimitEvent> events, DateTime now)
    {
        var b = new Bucket { Pct = a.FiveHour, AsOf = a.UsageAt };
        LimitEvent blocked = Latest(events, "five_hour", a.UsageAt, now);
        if (blocked != null) return new Bucket { Pct = 100, AsOf = blocked.LoggedAt, ResetAt = blocked.ResetsAt, Exact = true };
        if (b.Pct < 0 || a.UsageAt == DateTime.MinValue) return b;

        // The window ends at the first known reset at/after the reading (2 min
        // slack: a reading taken right at a reset belongs to the old window),
        // else at most 5 h later.
        DateTime end = a.UsageAt + FiveHours;
        bool exact = false;
        foreach (LimitEvent e in events.Where(e => e.Type == "five_hour"))
            if (e.ResetsAt >= a.UsageAt.AddMinutes(-2) && e.ResetsAt <= a.UsageAt + FiveHours && (!exact || e.ResetsAt < end)) { end = e.ResetsAt; exact = true; }
        b.ResetAt = end;
        b.Exact = exact;
        b.ResetSince = now >= end;
        return b;
    }

    static Bucket Weekly(AccountInfo a, List<LimitEvent> events, DateTime anchor, DateTime now)
    {
        var b = new Bucket { Pct = a.SevenDay, AsOf = a.UsageAt };
        LimitEvent blocked = Latest(events, "seven_day", a.UsageAt, now);
        if (blocked != null) return new Bucket { Pct = 100, AsOf = blocked.LoggedAt, ResetAt = blocked.ResetsAt, Exact = true };
        if (b.Pct < 0 || a.UsageAt == DateTime.MinValue) return b;
        if (anchor != DateTime.MinValue) { b.ResetAt = NextWeekly(anchor, a.UsageAt.AddMinutes(-2)); b.Exact = true; }
        else b.ResetAt = a.UsageAt.AddDays(7);
        b.ResetSince = now >= b.ResetAt;
        if (b.ResetSince && b.Exact)
        {
            DateTime last = NextWeekly(anchor, now).AddDays(-7);   // show the reset that just passed
            if (last > b.ResetAt) b.ResetAt = last;
        }
        return b;
    }

    // A rejection logged after the reading whose reset is still ahead.
    static LimitEvent Latest(List<LimitEvent> events, string type, DateTime after, DateTime now) =>
        events.Where(e => e.Type == type && e.Status == "rejected" && e.LoggedAt > after && e.ResetsAt > now)
              .OrderByDescending(e => e.LoggedAt).FirstOrDefault();
}
