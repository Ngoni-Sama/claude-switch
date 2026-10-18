using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.Tests;

// ---- Linked computers: addresses and crypto ------------------------------------------
public class LinkCryptoTests
{
    [Fact]
    public void AddressIsDerivedFromTheKeysAndSurvivesCopyPaste()
    {
        LinkIdentity id = LinkIdentity.Create();
        Assert.Equal(24, id.Address.Length);
        Assert.Equal(id.Address, LinkCrypto.AddressOf(id.EcdhPub, id.SigPub));

        string shown = LinkCrypto.Format(id.Address);
        Assert.Matches(@"^CS-[0-9A-Z]{5}-[0-9A-Z]{5}-[0-9A-Z]{5}-[0-9A-Z]{5}-[0-9A-Z]{4}$", shown);
        Assert.Equal(id.Address, LinkCrypto.Normalize(shown));
        Assert.Equal(id.Address, LinkCrypto.Normalize("  " + shown.ToLowerInvariant().Replace("-", " ") + " "));
        Assert.Equal(LinkCrypto.Normalize("CS-00000-11111-00000-11111-0000"), LinkCrypto.Normalize("cs-ooooo-iiiii-OOOOO-LLLLL-oooo"));
        Assert.Null(LinkCrypto.Normalize("CS-12345"));
        Assert.Null(LinkCrypto.Normalize("CS-UUUUU-UUUUU-UUUUU-UUUUU-UUUU"));   // U isn't in the alphabet

        // Another computer's keys can't claim this address.
        LinkIdentity other = LinkIdentity.Create();
        Assert.NotEqual(id.Address, LinkCrypto.AddressOf(other.EcdhPub, id.SigPub));
    }

    [Fact]
    public void Base32MatchesTheRelay()
    {
        // Same vector as relay/src/worker.js b32(): 0x00..0x0e
        Assert.Equal("000G40R40M30E209185GR38E", LinkCrypto.Base32(Enumerable.Range(0, 15).Select(i => (byte)i).ToArray()));
    }

    [Fact]
    public void OnlyTheRecipientCanOpenAPartAndTamperingIsCaught()
    {
        LinkIdentity recipient = LinkIdentity.Create(), stranger = LinkIdentity.Create();
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        string ephPub = LinkCrypto.B64(eph.ExportSubjectPublicKeyInfo());
        byte[] ctx = LinkCrypto.Context("transfer", "A", recipient.Address, 1700000000, ephPub);
        var sendKeys = LinkCrypto.DeriveKeys(eph, recipient.EcdhPub, ctx);
        byte[] plain = RandomNumberGenerator.GetBytes(5000);
        byte[] part = LinkCrypto.Seal(sendKeys, ctx, 3, false, plain);

        var mine = LinkCrypto.DeriveKeys(recipient.Ecdh, ephPub, ctx);
        Assert.Equal(plain, LinkCrypto.Open(mine, ctx, 3, false, part));

        Assert.Throws<CryptographicException>(() => LinkCrypto.Open(LinkCrypto.DeriveKeys(stranger.Ecdh, ephPub, ctx), ctx, 3, false, part));
        Assert.Throws<CryptographicException>(() => LinkCrypto.Open(mine, ctx, 4, false, part));    // reordered
        Assert.Throws<CryptographicException>(() => LinkCrypto.Open(mine, ctx, 3, true, part));     // truncation passed off as the end
        byte[] flipped = (byte[])part.Clone();
        flipped[40] ^= 1;
        Assert.Throws<CryptographicException>(() => LinkCrypto.Open(mine, ctx, 3, false, flipped));
        byte[] otherCtx = LinkCrypto.Context("transfer", "B", recipient.Address, 1700000000, ephPub);
        Assert.Throws<CryptographicException>(() => LinkCrypto.Open(mine, otherCtx, 3, false, part));   // replayed into another message
    }
}

// ---- Linked computers end to end, against the real relay code ------------------------
// Runs relay/src/worker.js on Node (relay/dev-server.mjs) and links two
// "computers" (separate folders), then sends a profile from one to the other.
// Passes without running when Node 22+ isn't installed.
[Collection("Link")]
public class LinkEndToEndTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "cs-link-" + Guid.NewGuid().ToString("N"));
    Process relay;
    string url;

    static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "relay", "dev-server.mjs"))) dir = Path.GetDirectoryName(dir);
        return dir;
    }

    // CS_REQUIRE_RELAY=1 makes a missing Node a failure instead of a quiet pass (CI).
    bool StartRelay()
    {
        bool ok = TryStartRelay();
        if (!ok && Environment.GetEnvironmentVariable("CS_REQUIRE_RELAY") == "1") Assert.Fail("Couldn't start relay/dev-server.mjs with node.");
        return ok;
    }

    bool TryStartRelay()
    {
        // CS_RELAY_URL runs these against a deployed relay instead (smoke test).
        if (Environment.GetEnvironmentVariable("CS_RELAY_URL") is string live && live.Length > 0) { url = live.TrimEnd('/'); return true; }
        string repo = RepoRoot();
        if (repo == null) return false;
        int port;
        using (var l = new TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        try
        {
            relay = Process.Start(new ProcessStartInfo("node", "relay/dev-server.mjs " + port)
            {
                WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
        }
        catch (Exception) { return false; }   // no node
        url = "http://127.0.0.1:" + port;
        for (int i = 0; i < 50; i++)
        {
            try { using var c = new TcpClient(); c.Connect("127.0.0.1", port); return true; }
            catch (SocketException) { Thread.Sleep(100); }
            if (relay.HasExited) return false;
        }
        return false;
    }

    void As(string computer)
    {
        Link.DirOverride = Path.Combine(root, computer);
        Link.ResetForTests();
    }

    public void Dispose()
    {
        Link.DirOverride = null;
        Link.ClientFactory = null;
        Link.ResetForTests();
        try { if (relay is { HasExited: false }) relay.Kill(true); } catch (Exception) { }
        try { Directory.Delete(root, true); } catch (Exception) { }
    }

    [Fact]
    public async Task TwoComputersLinkByAddressAndSendAProfile()
    {
        if (!StartRelay()) return;
        Link.ClientFactory = () => new RelayClient(url);

        As("harare");
        string harare = await Link.TurnOn();
        As("london");
        string london = await Link.TurnOn();
        Assert.NotEqual(harare, london);

        // London asks to link, using the address it was told (formatted, in lower case).
        LinkPeer asked = await Link.Request(LinkCrypto.Format(harare).ToLowerInvariant(), "Harare desktop");
        Assert.Equal("waiting", asked.State);

        As("harare");
        await Link.Refresh();
        LinkMessage request = Assert.Single(Link.Inbox);
        Assert.Equal("link", request.Kind);
        Assert.Equal(london, request.From);
        await Link.Accept(request, "London laptop");
        Assert.True(Link.Peers().Single().Linked);

        As("london");
        await Link.Refresh();
        Assert.Empty(Link.Inbox);
        LinkPeer peer = Link.Peers().Single();
        Assert.True(peer.Linked);

        // A profile with Claude Desktop settings in it.
        string data = Path.Combine(root, "profile-data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "claude_desktop_config.json"), "{\"mcpServers\":{\"files\":{\"command\":\"npx\"}}}");
        File.WriteAllText(Path.Combine(data, "config.json"), "{\"oauth:tokenCacheV2\":\"SECRET\",\"theme\":\"dark\"}");
        var profile = new ProfileEntry { Name = "Work", DataDir = data };
        long size = await Link.SendProfiles(peer, new ExportOptions
        {
            Profiles = { profile }, Sessions = false, Memory = false, CodeConfig = false, DesktopSettings = true,
        }, null);
        Assert.True(size > 0);

        As("harare");
        await Link.Refresh();
        LinkMessage transfer = Assert.Single(Link.Inbox);
        Assert.Equal("transfer", transfer.Kind);
        Assert.Equal("London laptop", transfer.Name);
        Assert.Equal(new[] { "Work" }, transfer.Profiles);
        Assert.True(transfer.Complete);

        string file = await Link.Receive(transfer, null);
        using (ArchiveInfo info = Backup.Open(file, null))
        {
            Assert.Equal("Work", Assert.Single(info.Profiles).Name);
            using var zip = System.IO.Compression.ZipFile.OpenRead(info.ZipPath);
            string cfg = new StreamReader(zip.GetEntry("profiles/0/data/config.json").Open()).ReadToEnd();
            Assert.DoesNotContain("SECRET", cfg);   // sign-ins never travel
        }
        Assert.Empty(Link.Inbox);

        // It was deleted from the relay once received.
        await Link.Refresh();
        Assert.Empty(Link.Inbox);

        // Unlinking reaches the other side.
        await Link.Unlink(Link.Peers().Single());
        As("london");
        await Link.Refresh();
        Assert.Empty(Link.Peers());
    }

    // relay/security-check.mjs tries to read, download, delete, take over,
    // impersonate and inject; every attempt must be refused.
    [Fact]
    public void NobodyCanReachDataForAnAddressTheyDontOwn()
    {
        if (!StartRelay()) return;
        var p = Process.Start(new ProcessStartInfo("node", "relay/security-check.mjs " + url)
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        });
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        Assert.True(p.ExitCode == 0, output);
        Assert.DoesNotContain("LEAKED", output);
        Assert.Contains("All attempts refused.", output);
    }

    [Fact]
    public async Task CopiedPublicKeysCantTakeOverAnAddress()
    {
        if (!StartRelay()) return;
        using var c = new RelayClient(url);
        LinkIdentity real = LinkIdentity.Create();
        await c.Register(real);
        // A thief reads the public keys and tries to set its own mailbox secret.
        byte[] thiefSecret = RandomNumberGenerator.GetBytes(32);
        using var http = new HttpClient();
        var put = await http.PutAsync(url + "/v1/keys/" + real.Address, new StringContent(
            "{\"ecdh\":\"" + real.EcdhPub + "\",\"sig\":\"" + real.SigPub + "\",\"secretHash\":\"" + Convert.ToHexStringLower(SHA256.HashData(thiefSecret)) + "\"}",
            Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, put.StatusCode);
        var list = new HttpRequestMessage(HttpMethod.Get, url + "/v1/mail/" + real.Address);
        list.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Mailbox", LinkCrypto.B64(thiefSecret));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.SendAsync(list)).StatusCode);
        Assert.Empty(await c.List(real));   // the owner still gets in
    }

    [Fact]
    public async Task TheRelayRefusesSomeoneElsesKeysForAnAddress()
    {
        if (!StartRelay()) return;
        using var c = new RelayClient(url);
        LinkIdentity real = LinkIdentity.Create(), impostor = LinkIdentity.Create();
        await c.Register(real);
        // Impostor tries to register its own keys under the real address.
        impostor.Address = real.Address;
        await Assert.ThrowsAsync<LinkException>(() => c.Register(impostor));
        var (ecdh, sig) = await c.Keys(real.Address);
        Assert.Equal(real.EcdhPub, ecdh);
        Assert.Equal(real.SigPub, sig);
        await Assert.ThrowsAsync<LinkException>(() => c.Keys(LinkCrypto.Normalize("CS-00000-00000-00000-00000-0000")));
    }
}
