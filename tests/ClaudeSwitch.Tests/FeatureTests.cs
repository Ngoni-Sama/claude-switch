using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.Tests;

// ---- Claude Code in a terminal ------------------------------------------------------
// Runs the generated PowerShell wrapper and cmd shims for real, against a
// stand-in `claude` that prints the CLAUDE_CONFIG_DIR it was given.
public class TerminalScriptTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "cs-term-" + Guid.NewGuid().ToString("N"));
    string Bin => Path.Combine(root, "fakebin");
    string Project => Path.Combine(root, "client-project");
    string Elsewhere => Path.Combine(root, "elsewhere");

    readonly List<ProfileEntry> profiles;

    public TerminalScriptTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, "src"));
        Directory.CreateDirectory(Elsewhere);
        Directory.CreateDirectory(Bin);
        File.WriteAllText(Path.Combine(Bin, "claude.cmd"), "@echo CFG=[%CLAUDE_CONFIG_DIR%] ARGS=[%*]\r\n");
        profiles = new List<ProfileEntry>
        {
            new() { Name = "Personal", DataDir = Path.Combine(root, "Claude") },
            new() { Name = "Client A", DataDir = Path.Combine(root, "Claude-Client-A"), ConfigDir = Path.Combine(root, ".claude-client-a") },
        };
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

    static string Run(string exe, IEnumerable<string> args, string cwd, string path)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = path;
        psi.Environment.Remove("CLAUDE_CONFIG_DIR");
        using Process p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return o;
    }

    [Fact]
    public void ShimsRunClaudeAsTheirProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shims = Terminal.CmdShims(profiles);
        Assert.Contains(shims, s => s.name == "claude-client-a.cmd");
        Assert.Contains(shims, s => s.name == "claude-personal.cmd");
        Assert.Contains(shims, s => s.name == "claude-default.cmd");
        foreach (var (name, text) in shims) File.WriteAllText(Path.Combine(root, name), text);

        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string path = Bin + ";" + sys, cmd = Path.Combine(sys, "cmd.exe");
        Assert.Contains("CFG=[" + Path.Combine(root, ".claude-client-a") + "] ARGS=[--resume]",
            Run(cmd, new[] { "/c", Path.Combine(root, "claude-client-a.cmd"), "--resume" }, Elsewhere, path));
        // The default profile runs with CLAUDE_CONFIG_DIR cleared.
        Assert.Contains("CFG=[]", Run(cmd, new[] { "/c", Path.Combine(root, "claude-personal.cmd") }, Elsewhere, path));
    }

    [Fact]
    public void PowerShellPicksTheProfileByFolderAndRestoresTheShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        string script = Path.Combine(root, "claude-switch.ps1");
        File.WriteAllText(script, Terminal.PowerShellScript(profiles, new[] { (Project, "Client A") }), new System.Text.UTF8Encoding(true));

        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string psDir = Path.Combine(sys, "WindowsPowerShell", "v1.0");
        string test = ". '" + script + "'; " +
                      "Set-Location -LiteralPath '" + Path.Combine(Project, "src") + "'; claude go; " +
                      "Set-Location -LiteralPath '" + Elsewhere + "'; claude; " +
                      "Write-Output ('AFTER=[' + $env:CLAUDE_CONFIG_DIR + ']')";
        string o = Run(Path.Combine(psDir, "powershell.exe"), new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", test },
                       Elsewhere, Bin + ";" + sys + ";" + psDir);

        Assert.Contains("-> profile Client A", o);
        Assert.Contains("CFG=[" + Path.Combine(root, ".claude-client-a") + "] ARGS=[go]", o);   // in a subfolder of the rule's folder
        Assert.Contains("CFG=[] ARGS=[]", o);                                                    // outside it
        Assert.Contains("AFTER=[]", o);                                                          // the shell isn't left changed
    }

    [Fact]
    public void ShellScriptDefinesACommandPerProfile()
    {
        string sh = Terminal.ShScript(profiles, new[] { (Project, "Client A") });
        Assert.Contains("claude-client-a() {", sh);
        Assert.Contains("claude-personal() { env -u CLAUDE_CONFIG_DIR command claude", sh);
        Assert.Contains("unalias claude", sh);
    }
}

// ---- launcher icons -----------------------------------------------------------------
public class ProfileIconTests
{
    [Fact]
    public void WritesValidPngIcoAndIcns()
    {
        byte[] rgba = new byte[256 * 256 * 4];
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        byte[] png = ProfileIcon.Png(rgba);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(256, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
        int at = 8;
        while (at < png.Length)   // every chunk's CRC checks out
        {
            int len = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            uint crc = Crc(png.AsSpan(at + 4, 4 + len));
            uint stored = (uint)((png[at + 8 + len] << 24) | (png[at + 9 + len] << 16) | (png[at + 10 + len] << 8) | png[at + 11 + len]);
            Assert.Equal(stored, crc);
            at += 12 + len;
        }

        byte[] ico = ProfileIcon.Ico(png);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1, 0 }, ico[..6]);
        Assert.Equal(png, ico[22..]);

        byte[] icns = ProfileIcon.Icns(png);
        Assert.Equal("icns", System.Text.Encoding.ASCII.GetString(icns, 0, 4));
        Assert.Equal("ic08", System.Text.Encoding.ASCII.GetString(icns, 8, 4));
        Assert.Equal(icns.Length, (icns[4] << 24) | (icns[5] << 16) | (icns[6] << 8) | icns[7]);
    }

    static uint Crc(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFF;
    }
}

// ---- Claude Switch's own updates ------------------------------------------------------
public class SelfUpdateTests
{
    static byte[] Sign(ECDsa key, byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    [Fact]
    public void AcceptsOnlyTheMatchingSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        byte[] sums = System.Text.Encoding.UTF8.GetBytes("abc123  ClaudeSwitch-9.9.9-windows-x64-setup.exe\n");
        byte[] sig = Sign(key, sums);

        Assert.True(SelfUpdate.VerifySignature(sums, sig, pub));
        byte[] tampered = (byte[])sums.Clone();
        tampered[0] ^= 1;
        Assert.False(SelfUpdate.VerifySignature(tampered, sig, pub));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(SelfUpdate.VerifySignature(sums, sig, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        Assert.False(SelfUpdate.VerifySignature(sums, new byte[] { 1, 2, 3 }, pub));
    }

    [Fact]
    public void ARandomKeyNeverPassesTheBuiltInKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] data = { 1, 2, 3 };
        Assert.False(SelfUpdate.VerifySignature(data, Sign(key, data)));
    }

    [Fact]
    public void ReadsChecksumsInSha256sumFormat()
    {
        string sums = "aaa111  ClaudeSwitch-2.2.0-windows-x64-setup.exe\nbbb222 *ClaudeSwitch-2.2.0-macos-arm64.dmg\n";
        Assert.Equal("aaa111", SelfUpdate.ExpectedHash(sums, "ClaudeSwitch-2.2.0-windows-x64-setup.exe"));
        Assert.Equal("bbb222", SelfUpdate.ExpectedHash(sums, "ClaudeSwitch-2.2.0-macos-arm64.dmg"));
        Assert.Null(SelfUpdate.ExpectedHash(sums, "something-else.exe"));
    }
}

// ---- billing relay status (read-only) --------------------------------------------------
public class RelayTests
{
    [Fact]
    public void ReadsAccountsUsageAndExpiry()
    {
        string f = Path.Combine(Path.GetTempPath(), "relay-" + Guid.NewGuid().ToString("N") + ".alive");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.WriteAllText(f, "{\"port\":48620,\"pid\":1,\"at\":" + now + ",\"accounts\":[\"work\"],\"expires\":{\"work\":\"2027-10-01T00:00:00Z\"}," +
                             "\"usage\":{\"work\":{\"at\":" + now + ",\"h5\":0.37,\"h5_reset\":" + (now + 3600) + ",\"d7\":0.12,\"d7_reset\":" + (now + 86400) + "}}}");
        try
        {
            RelayStatus s = Relay.Read(f);
            Assert.True(s.Fresh);
            RelayAccount a = Assert.Single(s.Accounts);
            Assert.Equal("work", a.Name);
            Assert.Equal(37, a.SessionPct);
            Assert.Equal(12, a.WeeklyPct);
            Assert.Equal(2027, a.Expires.Year);
        }
        finally { File.Delete(f); }
        Assert.Null(Relay.Read(f));   // not installed
    }
}

// ---- profile operations ---------------------------------------------------------------
public class ProfileOpsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "cs-ops-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

    ProfileEntry Make(string name)
    {
        var p = new ProfileEntry { Name = name, DataDir = Path.Combine(root, "Claude-" + name), ConfigDir = Path.Combine(root, ".claude-" + name.ToLowerInvariant()) };
        Directory.CreateDirectory(p.DataDir);
        Directory.CreateDirectory(p.ConfigDir);
        return p;
    }

    [Fact]
    public void CopiesMcpServersReplacingSameNamesAndKeepingTheRest()
    {
        ProfileEntry from = Make("Work"), to = Make("Personal");
        File.WriteAllText(Path.Combine(from.DataDir, "claude_desktop_config.json"),
            "{\"mcpServers\":{\"github\":{\"command\":\"gh-mcp\"},\"files\":{\"command\":\"fs-new\"}}}");
        File.WriteAllText(Path.Combine(to.DataDir, "claude_desktop_config.json"),
            "{\"theme\":\"dark\",\"mcpServers\":{\"files\":{\"command\":\"fs-old\"},\"mine\":{\"command\":\"keep\"}}}");
        File.WriteAllText(Path.Combine(from.ConfigDir, ".claude.json"), "{\"mcpServers\":{\"linear\":{\"type\":\"http\"}},\"oauthAccount\":{\"x\":1}}");
        File.WriteAllText(Path.Combine(to.ConfigDir, ".claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"me@example.com\"}}");

        ProfileOps.McpCopyResult r = ProfileOps.CopyMcpServers(from, to);

        Assert.Null(r.Problem);
        Assert.Equal(new[] { "github", "files" }, r.Desktop);
        Assert.Equal(new[] { "linear" }, r.Code);
        var desk = JsonNode.Parse(File.ReadAllText(Path.Combine(to.DataDir, "claude_desktop_config.json")))!.AsObject();
        Assert.Equal("dark", desk["theme"]!.ToString());                                    // other settings untouched
        Assert.Equal("fs-new", desk["mcpServers"]!["files"]!["command"]!.ToString());        // same name replaced
        Assert.Equal("keep", desk["mcpServers"]!["mine"]!["command"]!.ToString());           // its own kept
        var code = JsonNode.Parse(File.ReadAllText(Path.Combine(to.ConfigDir, ".claude.json")))!.AsObject();
        Assert.Equal("me@example.com", code["oauthAccount"]!["emailAddress"]!.ToString());  // its account untouched
        Assert.NotNull(code["mcpServers"]!["linear"]);
        Assert.True(File.Exists(r.Backup));
    }

    [Fact]
    public void DuplicateLeavesSignInsAndCachesBehind()
    {
        string src = Path.Combine(root, "src"), dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(Path.Combine(src, "claude-code-sessions", "acct"));
        Directory.CreateDirectory(Path.Combine(src, "Cache"));
        Directory.CreateDirectory(Path.Combine(src, "Network"));
        File.WriteAllText(Path.Combine(src, "claude-code-sessions", "acct", "local_1.json"), "{}");
        File.WriteAllText(Path.Combine(src, "Cache", "big"), "x");
        File.WriteAllText(Path.Combine(src, "Network", "Cookies"), "x");
        File.WriteAllText(Path.Combine(src, "config.json"), "{\"oauth:tokenCacheV2\":\"SECRET\",\"lastKnownAccountUuid\":\"acct\",\"theme\":\"dark\"}");

        ProfileOps.CopyFiltered(src, dst);
        ProfileOps.StripSignIn(Path.Combine(dst, "config.json"));

        Assert.True(File.Exists(Path.Combine(dst, "claude-code-sessions", "acct", "local_1.json")));
        Assert.False(Directory.Exists(Path.Combine(dst, "Cache")));
        Assert.False(Directory.Exists(Path.Combine(dst, "Network")));
        string cfg = File.ReadAllText(Path.Combine(dst, "config.json"));
        Assert.DoesNotContain("SECRET", cfg);
        Assert.DoesNotContain("oauth", cfg);
        Assert.Contains("dark", cfg);
    }

    [Theory]
    [InlineData("alexr@work.example.com", "a****@work.example.com")]
    [InlineData("a@b.c", "*@b.c")]
    public void MasksEmails(string email, string masked) => Assert.Equal(masked, Diagnose.MaskEmail(email));
}
