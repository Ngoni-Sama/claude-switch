// Linked computers: send profiles to another computer anywhere, over the
// internet, end-to-end encrypted.
//
// Each computer has an address (CS-XXXXX-XXXXX-XXXXX-XXXXX-XXXX) derived from
// its two public keys, so an address can't be faked. Two computers link once by
// address; after that either can send the other a transfer (the same archive as
// Export) that waits on the relay until the other computer collects it, for up
// to a week. The relay (relay/ in the repo, or your own) only ever sees
// ciphertext.
//
// Crypto: P-256 ECDH with a fresh key per message -> HKDF-SHA256 -> AES-256-CBC
// + HMAC-SHA256 per part (encrypt-then-MAC, bound to the message and part
// number). Headers are signed with the sender's P-256 ECDSA key. Sign-ins are
// never included, just as with Export.

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

public class LinkPeer
{
    public string Address, Name, Ecdh, Sig;
    public string State = "linked";        // "linked", or "waiting" for them to accept
    public DateTime Added = DateTime.Now;
    public bool Linked => State == "linked";
}

// A message waiting on the relay for this computer, already checked and decrypted.
public class LinkMessage
{
    public string Id, From, Kind, Name;    // Kind: "link" (they want to link) or "transfer"
    public long Size;
    public int Parts;
    public bool Complete;
    public DateTime Created, Expires;
    public List<string> Profiles = new();
    internal string Eph, Ctx;
}

public class LinkException : Exception { public LinkException(string m, Exception inner = null) : base(m, inner) { } }

public sealed class LinkIdentity
{
    public string Address;
    public byte[] Secret;
    public ECDiffieHellman Ecdh;
    public ECDsa Sig;
    public string EcdhPub => LinkCrypto.B64(Ecdh.ExportSubjectPublicKeyInfo());
    public string SigPub => LinkCrypto.B64(Sig.ExportSubjectPublicKeyInfo());

    public static LinkIdentity Create()
    {
        var id = new LinkIdentity
        {
            Ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            Sig = ECDsa.Create(ECCurve.NamedCurves.nistP256),
            Secret = RandomNumberGenerator.GetBytes(32),
        };
        id.Address = LinkCrypto.AddressOf(id.EcdhPub, id.SigPub);
        return id;
    }

    internal JsonObject ToJson() => new()
    {
        ["address"] = Address, ["secret"] = LinkCrypto.B64(Secret),
        ["ecdh"] = LinkCrypto.B64(Ecdh.ExportPkcs8PrivateKey()), ["sig"] = LinkCrypto.B64(Sig.ExportPkcs8PrivateKey()),
    };

    internal static LinkIdentity FromJson(JsonObject o)
    {
        if (o == null) return null;
        try
        {
            var id = new LinkIdentity { Secret = LinkCrypto.Unb64(Json.Str(o, "secret")), Ecdh = ECDiffieHellman.Create(), Sig = ECDsa.Create() };
            id.Ecdh.ImportPkcs8PrivateKey(LinkCrypto.Unb64(Json.Str(o, "ecdh")), out _);
            id.Sig.ImportPkcs8PrivateKey(LinkCrypto.Unb64(Json.Str(o, "sig")), out _);
            id.Address = LinkCrypto.AddressOf(id.EcdhPub, id.SigPub);
            return id;
        }
        catch (Exception ex) { ErrorLog.Write(ex); return null; }
    }
}

public static class LinkCrypto
{
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford base32
    static readonly byte[] Info = Encoding.ASCII.GetBytes("claude-switch link v1");
    public const uint MetaPart = 0xFFFFFFFF;

    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Unb64(string s)
    {
        s = (s ?? "").Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    public static string Base32(byte[] bytes)
    {
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(value >> (bits - 5)) & 31]); bits -= 5; }
            value &= (1 << bits) - 1;
        }
        if (bits > 0) sb.Append(Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    // 120 bits of SHA-256 over both public keys: 24 characters.
    public static string AddressOf(string ecdhSpki, string sigSpki) =>
        Base32(SHA256.HashData(Unb64(ecdhSpki).Concat(Unb64(sigSpki)).ToArray())[..15]);

    // "CS-ABCDE-FGHJK-MNPQR-STVWX-YZ01" for people; the bare 24 characters on the wire.
    public static string Format(string address) =>
        address == null || address.Length != 24 ? address
            : "CS-" + string.Join("-", Enumerable.Range(0, 5).Select(i => address.Substring(i * 5, Math.Min(5, 24 - i * 5))));

    // Accepts what people paste: any case, spaces, dashes, the CS- prefix, O for 0, I/L for 1.
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string s = input.Trim().ToUpperInvariant();
        if (s.StartsWith("CS-") || s.StartsWith("CS ")) s = s[3..];
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            char d = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (d is '-' or ' ' or '\t') continue;
            if (Alphabet.IndexOf(d) < 0) return null;
            sb.Append(d);
        }
        return sb.Length == 24 ? sb.ToString() : null;
    }

    public static byte[] Context(string kind, string from, string to, long created, string eph) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + from + "|" + to + "|" + created + "|" + eph));

    public static (byte[] enc, byte[] mac) DeriveKeys(ECDiffieHellman mine, string theirSpki, byte[] ctx)
    {
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(Unb64(theirSpki), out _);
        byte[] shared = mine.DeriveRawSecretAgreement(theirs.PublicKey);
        byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 64, ctx, Info);
        CryptographicOperations.ZeroMemory(shared);
        return (okm[..32], okm[32..]);
    }

    static byte[] Tag(byte[] mac, byte[] ctx, uint n, bool last, ReadOnlySpan<byte> ivAndCipher)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, mac);
        h.AppendData(ctx);
        Span<byte> nb = stackalloc byte[5];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(nb, n);
        nb[4] = (byte)(last ? 1 : 0);
        h.AppendData(nb);
        h.AppendData(ivAndCipher);
        return h.GetHashAndReset();
    }

    // IV[16] || AES-256-CBC(plain) || HMAC[32]
    public static byte[] Seal((byte[] enc, byte[] mac) k, byte[] ctx, uint n, bool last, ReadOnlySpan<byte> plain)
    {
        using Aes aes = Aes.Create();
        aes.Key = k.enc;
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] ct = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
        byte[] body = new byte[16 + ct.Length + 32];
        iv.CopyTo(body, 0);
        ct.CopyTo(body, 16);
        Tag(k.mac, ctx, n, last, body.AsSpan(0, 16 + ct.Length)).CopyTo(body, 16 + ct.Length);
        return body;
    }

    public static byte[] Open((byte[] enc, byte[] mac) k, byte[] ctx, uint n, bool last, byte[] body)
    {
        if (body == null || body.Length < 16 + 16 + 32) throw new CryptographicException("Damaged data.");
        int ctLen = body.Length - 48;
        byte[] want = Tag(k.mac, ctx, n, last, body.AsSpan(0, 16 + ctLen));
        if (!CryptographicOperations.FixedTimeEquals(want, body.AsSpan(16 + ctLen, 32))) throw new CryptographicException("This data was changed or isn't for this computer.");
        using Aes aes = Aes.Create();
        aes.Key = k.enc;
        return aes.DecryptCbc(body.AsSpan(16, ctLen), body.AsSpan(0, 16), PaddingMode.PKCS7);
    }
}

// The relay's HTTP API (see relay/src/worker.js).
public class RelayClient : IDisposable
{
    readonly HttpClient http;
    readonly string baseUrl;

    public RelayClient(string baseUrl, HttpMessageHandler handler = null)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        http = handler != null ? new HttpClient(handler) : new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeSwitch/" + typeof(RelayClient).Assembly.GetName().Version?.ToString(3));
    }

    public void Dispose() => http.Dispose();

    async Task<JsonObject> Send(HttpRequestMessage req, CancellationToken ct = default)
    {
        HttpResponseMessage r;
        try { r = await http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new LinkException("Couldn't reach the relay. Check your internet connection.", ex); }
        using (r)
        {
            string text = await r.Content.ReadAsStringAsync(ct);
            JsonObject body = null;
            try { body = JsonNode.Parse(text) as JsonObject; } catch (Exception) { }
            if (r.IsSuccessStatusCode) return body ?? new JsonObject();
            string err = Json.Str(body, "error") ?? r.ReasonPhrase;
            throw new LinkException(r.StatusCode switch
            {
                HttpStatusCode.NotFound when err == "no such address" => "There's no computer with that address. Check it, and that it has turned on Linked computers.",
                HttpStatusCode.TooManyRequests => "The relay is busy for this computer (" + err + "). Try again later.",
                HttpStatusCode.RequestEntityTooLarge => "That's too big for the relay (1 GB at most). Send fewer profiles or leave out sessions.",
                _ => "The relay said: " + err + " (" + (int)r.StatusCode + ")",
            });
        }
    }

    static StringContent JsonBody(JsonObject o) => new(o.ToJsonString(), Encoding.UTF8, "application/json");
    HttpRequestMessage Auth(HttpMethod m, string path, LinkIdentity me)
    {
        var req = new HttpRequestMessage(m, baseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Mailbox", LinkCrypto.B64(me.Secret));
        return req;
    }

    public async Task<int> MaxPart()
    {
        JsonObject info = await Send(new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/info"));
        long n = Json.Long(info, "maxPart");
        return n > 4096 ? (int)n : 1_000_000;
    }

    public Task Register(LinkIdentity me)
    {
        // Signed with the address's own key, so only its owner can register it
        // or change its mailbox secret.
        string secretHash = Convert.ToHexStringLower(SHA256.HashData(me.Secret));
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string proof = LinkCrypto.B64(me.Sig.SignData(Encoding.UTF8.GetBytes("register|" + me.Address + "|" + secretHash + "|" + ts), HashAlgorithmName.SHA256));
        var req = Auth(HttpMethod.Put, "/v1/keys/" + me.Address, me);
        req.Content = JsonBody(new JsonObject { ["ecdh"] = me.EcdhPub, ["sig"] = me.SigPub, ["secretHash"] = secretHash, ["ts"] = ts, ["proof"] = proof });
        return Send(req);
    }

    // The relay checks the keys match the address; so do we, so a bad relay can't swap them.
    public async Task<(string ecdh, string sig)> Keys(string address)
    {
        JsonObject o = await Send(new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/keys/" + address));
        string ecdh = Json.Str(o, "ecdh"), sig = Json.Str(o, "sig");
        if (ecdh == null || sig == null || LinkCrypto.AddressOf(ecdh, sig) != address)
            throw new LinkException("The relay returned keys that don't match that address. Nothing was sent.");
        return (ecdh, sig);
    }

    public async Task<(string id, string token)> Post(string to, string header, string signature, long size, int parts)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/mail/" + to)
        {
            Content = JsonBody(new JsonObject { ["header"] = header, ["signature"] = signature, ["size"] = size, ["parts"] = parts }),
        };
        JsonObject o = await Send(req);
        return (Json.Str(o, "id"), Json.Str(o, "uploadToken"));
    }

    public async Task PutPart(string to, string id, string token, int n, byte[] data)
    {
        for (int attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Put, baseUrl + "/v1/mail/" + to + "/" + id + "/" + n) { Content = new ByteArrayContent(data) };
            req.Headers.Add("X-Upload-Token", token);
            try { await Send(req); return; }
            catch (LinkException) when (attempt < 3) { await Task.Delay(1500 * (attempt + 1)); }
        }
    }

    public async Task<JsonArray> List(LinkIdentity me) => Json.Arr(await Send(Auth(HttpMethod.Get, "/v1/mail/" + me.Address, me)), "messages") ?? new JsonArray();

    public async Task<byte[]> GetPart(LinkIdentity me, string id, int n)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using HttpResponseMessage r = await http.SendAsync(Auth(HttpMethod.Get, "/v1/mail/" + me.Address + "/" + id + "/" + n, me));
                if (!r.IsSuccessStatusCode) throw new LinkException("The relay couldn't find part " + (n + 1) + " (" + (int)r.StatusCode + "). The sender may need to send it again.");
                return await r.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && attempt < 3) { await Task.Delay(1500 * (attempt + 1)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new LinkException("Couldn't reach the relay. Check your internet connection.", ex); }
        }
    }

    public Task Delete(LinkIdentity me, string id) => Send(Auth(HttpMethod.Delete, "/v1/mail/" + me.Address + "/" + id, me));
}

public static class Link
{
    public const string DefaultRelay = "https://claude-switch-relay.ma360-ngoni.workers.dev";
    static string Dir => Path.Combine(Platform.AppDir, "link");
    public static string ReceivedDir => Path.Combine(Folder, "received");
    static readonly object gate = new();
    static LinkIdentity identity;

    // Set by tests so nothing touches the real relay or app folder.
    internal static Func<RelayClient> ClientFactory;
    internal static string DirOverride;
    static string Folder => DirOverride ?? Dir;

    public static string RelayUrl => string.IsNullOrWhiteSpace(Settings.LinkRelay) ? DefaultRelay : Settings.LinkRelay.Trim();
    public static string ComputerName => string.IsNullOrWhiteSpace(Settings.LinkName) ? Environment.MachineName : Settings.LinkName.Trim();

    public static List<LinkMessage> Inbox { get; private set; } = new();
    public static DateTime CheckedAt { get; private set; } = DateTime.MinValue;
    public static string LastError { get; private set; }
    public static event Action Changed;

    static RelayClient Client() => ClientFactory?.Invoke() ?? new RelayClient(RelayUrl);

    public static LinkIdentity Identity
    {
        get
        {
            lock (gate)
            {
                if (identity == null && File.Exists(Path.Combine(Folder, "identity.json")))
                    identity = LinkIdentity.FromJson(Json.ReadObject(Path.Combine(Folder, "identity.json")));
                return identity;
            }
        }
    }

    public static bool On => Identity != null;
    public static string Address => Identity?.Address;

    internal static void ResetForTests() { lock (gate) identity = null; Inbox = new(); LastError = null; }

    // ---- peers ---------------------------------------------------------------------

    public static List<LinkPeer> Peers()
    {
        var list = new List<LinkPeer>();
        foreach (JsonNode n in Json.Arr(Json.ReadObject(Path.Combine(Folder, "peers.json")), "peers") ?? new JsonArray())
        {
            if (n is not JsonObject o) continue;
            list.Add(new LinkPeer
            {
                Address = Json.Str(o, "address"), Name = Json.Str(o, "name"), Ecdh = Json.Str(o, "ecdh"), Sig = Json.Str(o, "sig"),
                State = Json.Str(o, "state") ?? "linked", Added = Json.FromEpochMs(Json.Long(o, "added")),
            });
        }
        return list.Where(p => p.Address != null && p.Ecdh != null && p.Sig != null && LinkCrypto.AddressOf(p.Ecdh, p.Sig) == p.Address).ToList();
    }

    static void SavePeers(List<LinkPeer> peers)
    {
        Directory.CreateDirectory(Folder);
        var arr = new JsonArray();
        foreach (LinkPeer p in peers)
            arr.Add(new JsonObject { ["address"] = p.Address, ["name"] = p.Name, ["ecdh"] = p.Ecdh, ["sig"] = p.Sig, ["state"] = p.State, ["added"] = Json.ToEpochMs(p.Added) });
        Json.WriteAtomic(Path.Combine(Folder, "peers.json"), new JsonObject { ["peers"] = arr }.ToJsonString(Json.Options));
    }

    static void Upsert(LinkPeer peer)
    {
        lock (gate)
        {
            List<LinkPeer> peers = Peers();
            peers.RemoveAll(p => p.Address == peer.Address);
            peers.Add(peer);
            SavePeers(peers);
        }
    }

    public static void Rename(string address, string name)
    {
        lock (gate)
        {
            List<LinkPeer> peers = Peers();
            foreach (LinkPeer p in peers.Where(p => p.Address == address)) p.Name = name;
            SavePeers(peers);
        }
        Changed?.Invoke();
    }

    // ---- setting up --------------------------------------------------------------------

    public static async Task<string> TurnOn()
    {
        LinkIdentity id = Identity ?? LinkIdentity.Create();
        using (RelayClient c = Client()) await c.Register(id);
        lock (gate)
        {
            if (Identity == null)
            {
                Directory.CreateDirectory(Folder);
                Json.WriteAtomic(Path.Combine(Folder, "identity.json"), id.ToJson().ToJsonString(Json.Options));
                identity = id;
            }
        }
        Changed?.Invoke();
        return id.Address;
    }

    // Forget this computer's address and every link. Others' links to it stop working.
    public static void TurnOff()
    {
        lock (gate)
        {
            identity = null;
            try { File.Delete(Path.Combine(Folder, "identity.json")); File.Delete(Path.Combine(Folder, "peers.json")); }
            catch (Exception ex) { ErrorLog.Write(ex); }
            Inbox = new();
        }
        Changed?.Invoke();
    }


    // ---- messages ----------------------------------------------------------------------

    record Posted(string Id, string Token, (byte[] enc, byte[] mac) Keys, byte[] Ctx);

    // Seal the meta to the recipient, sign the header and register the message.
    static async Task<Posted> Post(RelayClient c, LinkIdentity me, string kind, string to, string theirEcdh, JsonObject meta, long size, int parts)
    {
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        string ephPub = LinkCrypto.B64(eph.ExportSubjectPublicKeyInfo());
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] ctx = LinkCrypto.Context(kind, me.Address, to, created, ephPub);
        var keys = LinkCrypto.DeriveKeys(eph, theirEcdh, ctx);
        var header = new JsonObject
        {
            ["v"] = 1, ["kind"] = kind, ["from"] = me.Address, ["to"] = to, ["eph"] = ephPub, ["created"] = created,
            ["size"] = size, ["parts"] = parts,
            ["meta"] = LinkCrypto.B64(LinkCrypto.Seal(keys, ctx, LinkCrypto.MetaPart, true, Encoding.UTF8.GetBytes(meta.ToJsonString()))),
        };
        byte[] headerBytes = Encoding.UTF8.GetBytes(header.ToJsonString());
        string signature = LinkCrypto.B64(me.Sig.SignData(headerBytes, HashAlgorithmName.SHA256));
        var (id, token) = await c.Post(to, LinkCrypto.B64(headerBytes), signature, size, parts);
        return new Posted(id, token, keys, ctx);
    }

    static JsonObject MyMeta() => new() { ["name"] = ComputerName, ["app"] = "Claude Switch " + typeof(Link).Assembly.GetName().Version?.ToString(3) };

    // Ask a computer to link. It shows up there as a request to accept.
    public static async Task<LinkPeer> Request(string addressInput, string name)
    {
        LinkIdentity me = Identity ?? throw new LinkException("Turn on Linked computers first.");
        string address = LinkCrypto.Normalize(addressInput) ?? throw new LinkException("That doesn't look like a Claude Switch address. It looks like CS-XXXXX-XXXXX-XXXXX-XXXXX-XXXX.");
        if (address == me.Address) throw new LinkException("That's this computer's own address.");
        using RelayClient c = Client();
        var (ecdh, sig) = await c.Keys(address);
        LinkPeer existing = Peers().FirstOrDefault(p => p.Address == address);
        var peer = new LinkPeer
        {
            Address = address, Ecdh = ecdh, Sig = sig, State = existing?.Linked == true ? "linked" : "waiting",
            Name = !string.IsNullOrWhiteSpace(name) ? name.Trim() : existing?.Name ?? "Computer " + address[..5],
        };
        await Post(c, me, "link", address, ecdh, MyMeta(), 0, 0);
        Upsert(peer);
        Changed?.Invoke();
        return peer;
    }

    public static async Task Accept(LinkMessage m, string name)
    {
        LinkIdentity me = Identity ?? throw new LinkException("Linked computers is off.");
        using RelayClient c = Client();
        var (ecdh, sig) = await c.Keys(m.From);
        Upsert(new LinkPeer { Address = m.From, Name = string.IsNullOrWhiteSpace(name) ? m.Name : name.Trim(), Ecdh = ecdh, Sig = sig, State = "linked" });
        await Post(c, me, "accept", m.From, ecdh, MyMeta(), 0, 0);
        await c.Delete(me, m.Id);
        Inbox = Inbox.Where(x => x.Id != m.Id).ToList();
        Changed?.Invoke();
    }

    public static async Task Decline(LinkMessage m)
    {
        LinkIdentity me = Identity ?? throw new LinkException("Linked computers is off.");
        using (RelayClient c = Client()) await c.Delete(me, m.Id);
        Inbox = Inbox.Where(x => x.Id != m.Id).ToList();
        Changed?.Invoke();
    }

    // Remove a link here, and tell the other computer.
    public static async Task Unlink(LinkPeer peer)
    {
        lock (gate)
        {
            List<LinkPeer> peers = Peers();
            peers.RemoveAll(p => p.Address == peer.Address);
            SavePeers(peers);
        }
        Inbox = Inbox.Where(m => m.From != peer.Address).ToList();
        Changed?.Invoke();
        if (Identity is not LinkIdentity me) return;
        try { using RelayClient c = Client(); await Post(c, me, "unlink", peer.Address, peer.Ecdh, MyMeta(), 0, 0); }
        catch (Exception ex) { ErrorLog.Write(ex); }   // best effort
    }

    // Export the chosen profiles and send them, encrypted, to a linked computer.
    public static async Task<long> SendProfiles(LinkPeer peer, ExportOptions o, Action<string> progress, CancellationToken cancel = default)
    {
        LinkIdentity me = Identity ?? throw new LinkException("Linked computers is off.");
        if (!peer.Linked) throw new LinkException(peer.Name + " hasn't accepted the link yet.");
        o.Password = null;   // the transfer is encrypted to that computer instead
        string tmp = Path.Combine(Path.GetTempPath(), "claude-switch-send-" + Guid.NewGuid().ToString("N") + ".claudeprofiles");
        try
        {
            progress?.Invoke("Packing profiles…");
            await Task.Run(() => Backup.Export(o, tmp, progress), cancel);
            long size = new FileInfo(tmp).Length;
            using RelayClient c = Client();
            int chunk = await c.MaxPart() - 128;   // room for IV, padding and MAC
            int parts = (int)Math.Max(1, (size + chunk - 1) / chunk);
            JsonObject meta = MyMeta();
            meta["profiles"] = new JsonArray(o.Profiles.Select(p => (JsonNode)p.Name).ToArray());
            Posted m = await Post(c, me, "transfer", peer.Address, peer.Ecdh, meta, size, parts);

            byte[] buf = new byte[chunk];
            using var fs = File.OpenRead(tmp);
            for (int n = 0; n < parts; n++)
            {
                cancel.ThrowIfCancellationRequested();
                int read = 0, r;
                while (read < chunk && (r = fs.Read(buf, read, chunk - read)) > 0) read += r;
                byte[] body = LinkCrypto.Seal(m.Keys, m.Ctx, (uint)n, n == parts - 1, buf.AsSpan(0, read));
                await c.PutPart(peer.Address, m.Id, m.Token, n, body);
                progress?.Invoke("Sending… " + Mb(Math.Min(size, (long)(n + 1) * chunk)) + " of " + Mb(size));
            }
            return size;
        }
        finally { try { File.Delete(tmp); } catch (Exception) { } }
    }

    static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString(bytes < 10 * 1024 * 1024 ? "0.0" : "0") + " MB";

    // Fetch this computer's mailbox: verify each message, act on link answers,
    // and keep link requests and transfers in Inbox for the person to decide.
    public static async Task Refresh()
    {
        LinkIdentity me = Identity;
        if (me == null) return;
        try
        {
            using RelayClient c = Client();
            JsonArray raw = await c.List(me);
            List<LinkPeer> peers = Peers();
            var inbox = new List<LinkMessage>();
            bool changed = false;
            foreach (JsonObject o in raw.OfType<JsonObject>())
            {
                string id = Json.Str(o, "id");
                try
                {
                    byte[] headerBytes = LinkCrypto.Unb64(Json.Str(o, "header"));
                    var h = JsonNode.Parse(headerBytes) as JsonObject;
                    string kind = Json.Str(h, "kind"), from = Json.Str(h, "from"), eph = Json.Str(h, "eph");
                    long created = Json.Long(h, "created");
                    if (Json.Str(h, "to") != me.Address || from == null || eph == null) { await c.Delete(me, id); continue; }

                    LinkPeer known = peers.FirstOrDefault(p => p.Address == from);
                    string sig = known?.Sig, ecdh = known?.Ecdh;
                    if (sig == null) (ecdh, sig) = await c.Keys(from);
                    using (var verify = ECDsa.Create())
                    {
                        verify.ImportSubjectPublicKeyInfo(LinkCrypto.Unb64(sig), out _);
                        if (!verify.VerifyData(headerBytes, LinkCrypto.Unb64(Json.Str(o, "signature")), HashAlgorithmName.SHA256))
                        { await c.Delete(me, id); continue; }
                    }
                    byte[] ctx = LinkCrypto.Context(kind, from, me.Address, created, eph);
                    var keys = LinkCrypto.DeriveKeys(me.Ecdh, eph, ctx);
                    var meta = JsonNode.Parse(LinkCrypto.Open(keys, ctx, LinkCrypto.MetaPart, true, LinkCrypto.Unb64(Json.Str(h, "meta")))) as JsonObject;
                    string name = Json.Str(meta, "name") ?? "Computer " + from[..5];

                    switch (kind)
                    {
                        case "accept":
                            if (known != null && !known.Linked) { known.State = "linked"; Upsert(known); changed = true; }
                            await c.Delete(me, id);
                            break;
                        case "unlink":
                            if (known != null) { peers.Remove(known); lock (gate) SavePeers(peers); changed = true; }
                            await c.Delete(me, id);
                            break;
                        case "link" when known != null && known.Linked:
                            await Post(c, me, "accept", from, ecdh, MyMeta(), 0, 0);   // they relinked (e.g. reinstalled)
                            await c.Delete(me, id);
                            break;
                        case "link" when known != null:   // we both asked: that's a yes
                            known.State = "linked"; Upsert(known); changed = true;
                            await Post(c, me, "accept", from, ecdh, MyMeta(), 0, 0);
                            await c.Delete(me, id);
                            break;
                        case "link":
                            inbox.Add(Message(o, kind, from, name, meta, eph, ctx));
                            break;
                        case "transfer" when known != null && known.Linked:
                            inbox.Add(Message(o, kind, from, known.Name ?? name, meta, eph, ctx));
                            break;
                        default:   // transfers from computers that aren't linked, or unknown kinds
                            await c.Delete(me, id);
                            break;
                    }
                }
                catch (LinkException) { throw; }
                catch (Exception ex) { ErrorLog.Write(ex); try { await c.Delete(me, id); } catch (Exception) { } }
            }
            Inbox = inbox;
            LastError = null;
            if (changed) Changed?.Invoke();
        }
        catch (LinkException ex) { LastError = ex.Message; }
        finally { CheckedAt = DateTime.Now; Changed?.Invoke(); }
    }

    static LinkMessage Message(JsonObject o, string kind, string from, string name, JsonObject meta, string eph, byte[] ctx) => new()
    {
        Id = Json.Str(o, "id"), From = from, Kind = kind, Name = name, Size = Json.Long(o, "size"), Parts = (int)Json.Long(o, "parts"),
        Complete = Json.Bool(o, "complete"), Eph = eph, Ctx = Convert.ToBase64String(ctx),
        Created = DateTimeOffset.FromUnixTimeSeconds(Json.Long(o, "created")).LocalDateTime,
        Expires = DateTimeOffset.FromUnixTimeSeconds(Json.Long(o, "expires")).LocalDateTime,
        Profiles = (Json.Arr(meta, "profiles") ?? new JsonArray()).Select(n => n?.ToString()).Where(n => n != null).ToList(),
    };

    // Download and decrypt a transfer to a backup file this computer can import.
    // The relay copy is deleted once it has arrived intact.
    public static async Task<string> Receive(LinkMessage m, Action<string> progress, CancellationToken cancel = default)
    {
        LinkIdentity me = Identity ?? throw new LinkException("Linked computers is off.");
        if (!m.Complete) throw new LinkException("That transfer is still uploading. Try again in a minute.");
        Directory.CreateDirectory(ReceivedDir);
        string safe = new string(m.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        string path = Path.Combine(ReceivedDir, DateTime.Now.ToString("yyyy-MM-dd-HHmm") + "-from-" + (safe.Length > 0 ? safe : "computer") + ".claudeprofiles");
        byte[] ctx = Convert.FromBase64String(m.Ctx);
        var keys = LinkCrypto.DeriveKeys(me.Ecdh, m.Eph, ctx);
        using RelayClient c = Client();
        long total = 0;
        try
        {
            using (var fs = new FileStream(path, FileMode.Create))
            {
                for (int n = 0; n < m.Parts; n++)
                {
                    cancel.ThrowIfCancellationRequested();
                    byte[] plain = LinkCrypto.Open(keys, ctx, (uint)n, n == m.Parts - 1, await c.GetPart(me, m.Id, n));
                    fs.Write(plain);
                    total += plain.Length;
                    progress?.Invoke("Receiving… " + Mb(total) + " of " + Mb(m.Size));
                }
            }
            if (total != m.Size) throw new LinkException("The transfer arrived incomplete. Ask " + m.Name + " to send it again.");
        }
        catch (Exception) { try { File.Delete(path); } catch (Exception) { } throw; }
        try { await c.Delete(me, m.Id); } catch (Exception ex) { ErrorLog.Write(ex); }
        Inbox = Inbox.Where(x => x.Id != m.Id).ToList();
        Changed?.Invoke();
        return path;
    }
}
