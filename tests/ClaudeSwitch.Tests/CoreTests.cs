using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.Tests;

public class UsageReportTests
{
    const string Report = """
        Claude Code usage report (2026-10-09T12:45:51.869Z)
        Client: Mozilla/5.0 (Windows NT 10.0; Win64; x64) Claude/2.31226.0 Electron/44.4.3 MSIX

        Plan limits:
        - session-0: 77% (resets 2026-10-09T14:30:00.470713+00:00)
        - weekly_all-1: 13% (resets 2026-10-16T05:00:00.470738+00:00)
        - weekly_opus-2: 40% (resets 2026-10-16T05:00:00+00:00)

        Session:
        - Opus 4.8: 8.4k in / 5.9k out / 665.5k cache read / 133.2k cache write
        - Cost: $56.94 | API 68m | Wall 94m
        """;

    [Fact]
    public void ParsesLimitsAndResetTimes()
    {
        UsageReportData r = UsageReport.Parse(Report);
        Assert.NotNull(r);
        Assert.Equal(3, r.Limits.Count);
        Assert.Equal(77, r.Session.Pct);
        Assert.Equal(13, r.Weekly.Pct);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 14, 30, 0, 470, TimeSpan.Zero).LocalDateTime, r.Session.ResetsAt, TimeSpan.FromSeconds(1));
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 45, 51, 869, TimeSpan.Zero).LocalDateTime, r.At, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void LabelsMatchClaudesPanel()
    {
        UsageReportData r = UsageReport.Parse(Report);
        Assert.Equal("Session", r.Session.Label);
        Assert.Equal("Weekly \u00B7 all models", r.Weekly.Label);
        Assert.Equal("Weekly \u00B7 Opus", r.Limits[2].Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some copied text 77% (resets tomorrow)")]
    [InlineData("Claude Code usage report (2026-10-09T12:45:51Z)\nno limits here")]
    public void IgnoresAnythingElse(string text) => Assert.Null(UsageReport.Parse(text));
}

public class BucketTests
{
    [Fact]
    public void AfterAResetShowsZeroAndSaysSo()
    {
        var b = new Bucket { Pct = 100, ResetSince = true, Exact = true, ResetAt = DateTime.Today.AddHours(7) };
        Assert.Equal(0, b.Shown);
        Assert.False(b.AtLimit);
        Assert.StartsWith("Reset at 7:00 AM", b.ResetText);
    }

    [Fact]
    public void UpcomingResetUsesClaudesWording()
    {
        var b = new Bucket { Pct = 77, Exact = true, ResetAt = DateTime.Now.AddMinutes(97) };
        Assert.Equal(77, b.Shown);
        Assert.StartsWith("Resets in 1 hr 3", b.ResetText);
        Assert.True(new Bucket { Pct = 96, ResetAt = DateTime.Now.AddHours(1), Exact = true }.AtLimit);
    }

    [Theory]
    [InlineData(14, "14 min")]
    [InlineData(97, "1 hr 37 min")]
    [InlineData(120, "2 hr")]
    public void DurationText(int minutes, string expected) => Assert.Equal(expected, Bucket.In(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(1)));
}

public class PlatformTests
{
    [Theory]
    [InlineData("Work", true)]
    [InlineData("Client A", true)]
    [InlineData("my.side-project_2", true)]
    [InlineData("", false)]
    [InlineData("../etc", false)]
    [InlineData("a/b", false)]
    [InlineData(" leading", true)]
    public void ProfileNames(string name, bool ok) => Assert.Equal(ok, Platform.ValidProfileName(name));

    [Fact]
    public void EncodesProjectFoldersLikeClaudeCode()
    {
        Assert.Equal("C--Users-me-proj", Platform.Encode(@"C:\Users\me\proj"));
        Assert.Equal("-Users-me-proj", Platform.Encode("/Users/me/proj"));
    }

    [Fact]
    public void ProfileFoldersSitNextToClaudesOwn()
    {
        string dir = Platform.NewDataDir("Work");
        Assert.Equal(Platform.ClaudeDataRoot, Path.GetDirectoryName(dir));
        Assert.Equal("Claude-Work", Path.GetFileName(dir));
    }
}

public class BackupTests
{
    [Fact]
    public void EncryptedRoundTripAndWrongPassword()
    {
        string root = Path.Combine(Path.GetTempPath(), "cs-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string data = Path.Combine(root, "data"), cfg = Path.Combine(root, "cfg");
            Directory.CreateDirectory(Path.Combine(data, "claude-code-sessions", "acct", "org"));
            File.WriteAllText(Path.Combine(data, "config.json"), """{"lastKnownAccountUuid":"acct","oauth:tokenCacheV2":"SECRET"}""");
            File.WriteAllText(Path.Combine(data, "claude-code-sessions", "acct", "org", "local_1.json"), """{"sessionId":"local_1","cliSessionId":"cli1","title":"T"}""");
            Directory.CreateDirectory(Path.Combine(cfg, "projects", "p"));
            File.WriteAllText(Path.Combine(cfg, "projects", "p", "cli1.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(cfg, ".credentials.json"), "NEVER");

            var profile = new ProfileEntry { Name = "Test", DataDir = data, ConfigDir = cfg };
            string file = Path.Combine(root, "b.claudeprofiles");
            Backup.Export(new ExportOptions { Profiles = { profile }, Password = "correct horse" }, file, null);

            Assert.True(Backup.IsEncrypted(file));
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => Backup.Open(file, "wrong password"));
            using ArchiveInfo info = Backup.Open(file, "correct horse");
            Assert.Single(info.Profiles);
            Assert.Equal(1, info.Profiles[0].Counts["sessions"]);
            using var zip = System.IO.Compression.ZipFile.OpenRead(info.ZipPath);
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("credentials"));
            var cfgEntry = zip.Entries.Single(e => e.FullName.EndsWith("/data/config.json"));
            using var reader = new StreamReader(cfgEntry.Open());
            Assert.DoesNotContain("SECRET", reader.ReadToEnd());
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }
}

// Directory links are what makes unify mode share memory for real rather than
// copying it, so check the behaviour we depend on: reads and writes go through
// to one folder, and removing a link never takes the shared folder with it.
public class FsLinkTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "cs-fslink-" + Guid.NewGuid().ToString("N"));

    string Real => Path.Combine(root, "real");
    string Link => Path.Combine(root, "link");

    public FsLinkTests()
    {
        Directory.CreateDirectory(Real);
        File.WriteAllText(Path.Combine(Real, "note.md"), "shared");
    }

    public void Dispose()
    {
        try { if (FsLink.IsLink(Link)) FsLink.Remove(Link); } catch (Exception) { }
        try { Directory.Delete(root, true); } catch (Exception) { }
    }

    [Fact]
    public void LinkSharesOneFolderBothWays()
    {
        FsLink.Create(Link, Real);

        Assert.True(FsLink.IsLink(Link));
        Assert.True(FsLink.PointsAt(Link, Real));
        Assert.Equal("shared", File.ReadAllText(Path.Combine(Link, "note.md")));

        File.WriteAllText(Path.Combine(Link, "via-link.md"), "written through the link");
        Assert.True(File.Exists(Path.Combine(Real, "via-link.md")));
    }

    [Fact]
    public void RemovingALinkKeepsTheSharedFolder()
    {
        FsLink.Create(Link, Real);
        FsLink.Remove(Link);

        Assert.False(Directory.Exists(Link));
        Assert.True(File.Exists(Path.Combine(Real, "note.md")));
    }

    [Fact]
    public void AnOrdinaryFolderIsNotALink()
    {
        Assert.False(FsLink.IsLink(Real));
        Assert.Null(FsLink.Target(Real));
        Assert.False(FsLink.IsLink(Path.Combine(root, "does-not-exist")));
    }

    [Fact]
    public void MergeKeepsWhatIsAlreadyThere()
    {
        string other = Path.Combine(root, "other");
        Directory.CreateDirectory(Path.Combine(other, "memory"));
        File.WriteAllText(Path.Combine(other, "note.md"), "theirs");          // clashes
        File.WriteAllText(Path.Combine(other, "memory", "new.md"), "theirs"); // new

        int copied = FsLink.MergeInto(other, Real);

        Assert.Equal(1, copied);
        Assert.Equal("shared", File.ReadAllText(Path.Combine(Real, "note.md")));   // not overwritten
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(Real, "memory", "new.md")));
    }
}

public class LastSessionTests
{
    [Theory]
    [InlineData(new string[0], "nothing")]
    [InlineData(new[] { "Work" }, "Work")]
    [InlineData(new[] { "Work", "Personal" }, "Work and Personal")]
    [InlineData(new[] { "Work", "Personal", "Client" }, "Work, Personal and Client")]
    public void ReadsAsASentence(string[] names, string expected) =>
        Assert.Equal(expected, new LastSession { Profiles = names.ToList() }.Describe);
}

// Removing an old copy of Claude must take exactly what belongs to that copy
// and nothing else. Runs the real removal against a scratch folder, scratch
// shortcuts and a scratch registry key. Windows only (passes trivially elsewhere).
public class RemoveOldCopyTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "cs-rm-" + Guid.NewGuid().ToString("N"));
    readonly string regBase = @"Software\ClaudeSwitchTests\" + Guid.NewGuid().ToString("N");

    string Dir => Path.Combine(root, "AnthropicClaude");
    string Desktop => Path.Combine(root, "Desktop");

    public RemoveOldCopyTests()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(Dir, "app-1.0.0"));
        Directory.CreateDirectory(Path.Combine(Dir, "packages"));
        File.WriteAllText(Path.Combine(Dir, "app-1.0.0", "claude.exe"), "x");
        File.WriteAllText(Path.Combine(Dir, "claude.exe"), "x");
        string ro = Path.Combine(Dir, "packages", "RELEASES");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly);   // Squirrel leaves some of these

        Directory.CreateDirectory(Path.Combine(Desktop, "Apps"));
        Link(Path.Combine(Desktop, "Apps", "Claude.lnk"), Path.Combine(Dir, "claude.exe"));
        Link(Path.Combine(Desktop, "Notepad.lnk"), @"C:\Windows\notepad.exe");

        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        using (var k = hkcu.CreateSubKey(regBase + @"\Microsoft\Windows\CurrentVersion\Uninstall\AnthropicClaude"))
        {
            k.SetValue("DisplayName", "Claude");
            k.SetValue("DisplayVersion", "1.0.0");
            k.SetValue("UninstallString", "\"" + Path.Combine(Dir, "Update.exe") + "\" --uninstall");
        }
        using (var k = hkcu.CreateSubKey(regBase + @"\Microsoft\Windows\CurrentVersion\Uninstall\SomethingElse"))
            k.SetValue("UninstallString", @"C:\Elsewhere\uninstall.exe");
        using (var k = hkcu.CreateSubKey(regBase + @"\Microsoft\Windows\CurrentVersion\Run"))
        {
            k.SetValue("Claude", "\"" + Path.Combine(Dir, "claude.exe") + "\" --startup");
            k.SetValue("Keep", @"C:\Elsewhere\app.exe");
        }
        using (var k = hkcu.CreateSubKey(regBase + @"\Classes\claude\shell\open\command"))
            k.SetValue("", "\"" + Path.Combine(Dir, "claude.exe") + "\" \"%1\"");
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(regBase, false); } catch (Exception) { }
        try
        {
            if (Directory.Exists(root))
            {
                foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
        catch (Exception) { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void Link(string lnk, string target)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        object l = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        l.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, l, new object[] { target });
        l.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, l, null);
    }

    Microsoft.Win32.RegistryKey Key(string sub) => Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regBase + sub);

    [Fact]
    public void DryRunListsEverythingAndTouchesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        RemovalResult r = Updater.RemoveCopy(Dir, new[] { Desktop }, regBase, dryRun: true);

        Assert.True(r.Ok, r.Problem);
        Assert.Contains(r.Removed, x => x.StartsWith("Shortcut") && x.Contains("Claude.lnk"));
        Assert.Contains(r.Removed, x => x.Contains("Installed-apps entry \"Claude 1.0.0\""));
        Assert.Contains(r.Removed, x => x.Contains("Start-at-login entry \"Claude\""));
        Assert.Contains(r.Removed, x => x.Contains("claude:// link handler"));
        Assert.Contains(r.Removed, x => x.StartsWith("Program files"));
        Assert.True(r.Bytes > 0);

        Assert.True(Directory.Exists(Dir));
        Assert.True(File.Exists(Path.Combine(Desktop, "Apps", "Claude.lnk")));
        Assert.NotNull(Key(@"\Microsoft\Windows\CurrentVersion\Uninstall\AnthropicClaude"));
    }

    [Fact]
    public void RemovesOnlyWhatPointsAtTheOldCopy()
    {
        if (!OperatingSystem.IsWindows()) return;
        RemovalResult r = Updater.RemoveCopy(Dir, new[] { Desktop }, regBase, dryRun: false);

        Assert.True(r.Ok, r.Problem);
        Assert.Empty(r.Skipped);
        // Gone: the copy and everything pointing into it.
        Assert.False(Directory.Exists(Dir));
        Assert.False(File.Exists(Path.Combine(Desktop, "Apps", "Claude.lnk")));
        Assert.Null(Key(@"\Microsoft\Windows\CurrentVersion\Uninstall\AnthropicClaude"));
        Assert.Null(Key(@"\Classes\claude"));
        using (var run = Key(@"\Microsoft\Windows\CurrentVersion\Run")) Assert.Null(run.GetValue("Claude"));
        // Kept: everything else.
        Assert.True(File.Exists(Path.Combine(Desktop, "Notepad.lnk")));
        Assert.NotNull(Key(@"\Microsoft\Windows\CurrentVersion\Uninstall\SomethingElse"));
        using (var run = Key(@"\Microsoft\Windows\CurrentVersion\Run")) Assert.NotNull(run.GetValue("Keep"));
    }

    [Fact]
    public void RefusesAFolderThatIsNotAClaudeInstall()
    {
        if (!OperatingSystem.IsWindows()) return;
        string other = Path.Combine(root, "SomethingImportant");
        Directory.CreateDirectory(Path.Combine(other, "app-1.0.0"));

        RemovalResult r = Updater.RemoveCopy(other, new[] { Desktop }, regBase, dryRun: false);

        Assert.False(r.Ok);
        Assert.True(Directory.Exists(other));
        Assert.True(File.Exists(Path.Combine(Desktop, "Apps", "Claude.lnk")));
    }
}
