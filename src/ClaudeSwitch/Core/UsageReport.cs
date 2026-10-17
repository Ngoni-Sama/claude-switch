// Claude's own copyable usage report, the one local source of live numbers:
//
//   Claude Code usage report (2026-10-09T12:45:51.869Z)
//   ...
//   Plan limits:
//   - session-0: 77% (resets 2026-10-09T14:30:00.470713+00:00)
//   - weekly_all-1: 13% (resets 2026-10-16T05:00:00.470738+00:00)
//
// Only text that starts with the report header is ever parsed; nothing else
// from the clipboard is read or kept.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeSwitch.Core;

public class ReportLimit
{
    public string Name;          // session-0, weekly_all-1, weekly_opus-2, ...
    public int Pct;
    public DateTime ResetsAt;    // local time

    public bool IsSession => Name.StartsWith("session", StringComparison.OrdinalIgnoreCase);
    public bool IsWeeklyAll => Name.StartsWith("weekly_all", StringComparison.OrdinalIgnoreCase);

    // "weekly_opus-2" -> "Weekly · Opus"
    public string Label
    {
        get
        {
            if (IsSession) return "Session";
            if (IsWeeklyAll) return "Weekly · all models";
            string n = Regex.Replace(Name, "-\\d+$", "");
            int u = n.IndexOf('_');
            if (u < 0) return n;
            string scope = n[(u + 1)..];
            return char.ToUpperInvariant(n[0]) + n[1..u] + " · " + (scope.Length > 0 ? char.ToUpperInvariant(scope[0]) + scope[1..] : scope);
        }
    }
}

public class UsageReportData
{
    public DateTime At;
    public List<ReportLimit> Limits = new();
    public ReportLimit Session => Limits.FirstOrDefault(l => l.IsSession);
    public ReportLimit Weekly => Limits.FirstOrDefault(l => l.IsWeeklyAll);

    public string Summary => string.Join("  ·  ", new[]
    {
        Session != null ? "Session " + Session.Pct + "%" : null,
        Weekly != null ? "Weekly " + Weekly.Pct + "%" : null,
    }.Where(s => s != null));
}

public static class UsageReport
{
    public const string Header = "Claude Code usage report";
    static readonly Regex HeaderRx = new(@"^\s*Claude Code usage report\s*\(([^)]+)\)");
    static readonly Regex LimitRx = new(@"^\s*-\s*([A-Za-z0-9_\-]+):\s*(\d{1,3})%\s*\(resets\s+([^)]+)\)", RegexOptions.Multiline);

    public static bool Looks(string text) => text != null && text.TrimStart().StartsWith(Header, StringComparison.Ordinal);

    static bool ParseTime(string s, out DateTime local)
    {
        bool ok = DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto);
        local = ok ? dto.LocalDateTime : DateTime.MinValue;
        return ok;
    }

    public static UsageReportData Parse(string text)
    {
        if (!Looks(text)) return null;
        var r = new UsageReportData();
        Match h = HeaderRx.Match(text);
        if (!h.Success || !ParseTime(h.Groups[1].Value, out r.At)) r.At = DateTime.Now;
        // Limit lines follow "Plan limits:"; the per-model cost lines never match.
        int start = text.IndexOf("Plan limits:", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        foreach (Match m in LimitRx.Matches(text[start..]))
        {
            if (!ParseTime(m.Groups[3].Value, out DateTime resets)) continue;
            r.Limits.Add(new ReportLimit { Name = m.Groups[1].Value, Pct = Math.Min(100, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)), ResetsAt = resets });
        }
        return r.Limits.Count > 0 ? r : null;
    }

    public static void Save(string accountUuid, UsageReportData r)
    {
        var limits = new JsonArray();
        foreach (ReportLimit l in r.Limits)
            limits.Add(new JsonObject { ["name"] = l.Name, ["pct"] = l.Pct, ["resets"] = Json.ToEpochMs(l.ResetsAt) });
        Settings.SetReport(accountUuid, new JsonObject { ["at"] = Json.ToEpochMs(r.At), ["limits"] = limits });
        if (r.Weekly != null) Settings.SetWeeklyAnchor(accountUuid, r.Weekly.ResetsAt);
    }

    public static UsageReportData Load(string accountUuid)
    {
        JsonObject rec = Settings.Report(accountUuid);
        if (rec == null) return null;
        var r = new UsageReportData { At = Json.FromEpochMs(Json.Long(rec, "at")) };
        foreach (JsonNode n in Json.Arr(rec, "limits") ?? new JsonArray())
            if (n is JsonObject d)
                r.Limits.Add(new ReportLimit { Name = Json.Str(d, "name") ?? "", Pct = (int)Json.Long(d, "pct"), ResetsAt = Json.FromEpochMs(Json.Long(d, "resets")) });
        return r.Limits.Count > 0 ? r : null;
    }

    // The weekly limit resets at the same time each week, so the report's weekly
    // reset identifies the account when the anchors differ.
    public static ProfileEntry ByWeeklyReset(UsageReportData r, List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts)
    {
        if (r.Weekly == null) return null;
        var matches = profiles.Where(p =>
        {
            if (!accounts.TryGetValue(p.Name, out var a) || a.Uuid == null) return false;
            DateTime anchor = Settings.WeeklyAnchor(a.Uuid);
            if (anchor == DateTime.MinValue) return false;
            double minutes = Math.Abs((r.Weekly.ResetsAt - anchor).TotalMinutes) % (7 * 24 * 60);
            return minutes < 5 || minutes > 7 * 24 * 60 - 5;
        }).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
