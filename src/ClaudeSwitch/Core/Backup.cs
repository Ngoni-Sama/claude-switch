// Export / import profiles to a .claudeprofiles file (same format as 1.x), e.g.
// to move to a new computer.
//
// File:   "CLPROF1" + mode byte
//           mode 0: zip follows
//           mode 1: salt[16] iv[16] iterations[int32] AES-256-CBC(zip) HMAC-SHA256[32]
//                   keys = PBKDF2-SHA256(password, salt, iterations) -> 32 enc + 32 mac,
//                   MAC covers everything before it (encrypt-then-MAC).
// Zip:    manifest.json, profiles/<n>/data/..., profiles/<n>/config/...
//
// Never exported: sign-ins (oauth* keys, .credentials.json; the OS encrypts them
// to the machine anyway), caches, the Cowork VM. Import never overwrites.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

public class ExportOptions
{
    public List<ProfileEntry> Profiles = new();
    public bool Sessions = true, Memory = true, CodeConfig = true, DesktopSettings = true, Cowork;
    public string Password;   // null = not encrypted
}

public class ArchiveProfile
{
    public int Index;
    public string Name, Email, AccountUuid, OrgName;
    public bool WasDefault, SeparateConfig;
    public Dictionary<string, int> Counts = new();
    public string Summary => Counts.Where(kv => kv.Value > 0).Select(kv => kv.Value + " " + kv.Key).DefaultIfEmpty("nothing").Aggregate((x, y) => x + " · " + y);
}

public sealed class ArchiveInfo : IDisposable
{
    public string SourcePath, ZipPath, OldHome, Created, Machine;
    public List<ArchiveProfile> Profiles = new();
    public void Dispose() { try { if (ZipPath != null && File.Exists(ZipPath)) File.Delete(ZipPath); } catch (Exception) { } }
}

public class ImportChoice { public ArchiveProfile Source; public ProfileEntry Target; public bool CreateNew; }

public class ImportResult
{
    public int Files, Skipped;
    public List<string> Notes = new(), Errors = new();
}

public static class Backup
{
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("CLPROF1");
    const int Iterations = 200000;
    const int HeaderLen = 7 + 1 + 16 + 16 + 4;
    static readonly string[] CodeConfigFiles = { "CLAUDE.md", "settings.json", "settings.local.json", "keybindings.json",
                                                 "plugins/installed_plugins.json", "plugins/known_marketplaces.json" };
    static readonly string[] CodeConfigDirs = { "agents", "commands", "skills", "hooks", "output-styles" };

    record Item(string Entry, string Source, bool StripSignIn = false);

    static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    static int AddTree(List<Item> items, string root, string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return 0;
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        foreach (string f in files) items.Add(new Item(prefix + Rel(root, f), f));
        return files.Length;
    }

    static List<Item> Plan(ProfileEntry p, int idx, ExportOptions o, AccountInfo a, ArchiveProfile ap)
    {
        var items = new List<Item>();
        string data = p.Data, cfg = p.Config, pd = "profiles/" + idx + "/data/", pc = "profiles/" + idx + "/config/";

        if (o.Sessions && a.Uuid != null)
        {
            int sessions = 0, transcripts = 0;
            foreach (SessionInfo s in Accounts.Sessions(p, a.Uuid))
            {
                items.Add(new Item(pd + Rel(data, s.File), s.File));
                sessions++;
                string t = Accounts.FindTranscript(cfg, s.CliSessionId);
                if (t == null) continue;
                items.Add(new Item(pc + Rel(cfg, t), t));
                transcripts++;
                AddTree(items, cfg, t[..^".jsonl".Length], pc);
            }
            ap.Counts["sessions"] = sessions;
            ap.Counts["transcripts"] = transcripts;
        }
        if (o.Memory)
        {
            string projects = Path.Combine(cfg, "projects");
            ap.Counts["memory notes"] = Directory.Exists(projects)
                ? Directory.GetDirectories(projects).Sum(d => AddTree(items, cfg, Path.Combine(d, "memory"), pc)) : 0;
        }
        if (o.CodeConfig)
        {
            int n = 0;
            foreach (string f in CodeConfigFiles)
            {
                string path = Path.Combine(cfg, f);
                if (File.Exists(path)) { items.Add(new Item(pc + f, path)); n++; }
            }
            n += CodeConfigDirs.Sum(d => AddTree(items, cfg, Path.Combine(cfg, d), pc));
            string cj = Path.Combine(cfg, ".claude.json");
            if (File.Exists(cj)) { items.Add(new Item(pc + ".claude.json", cj, true)); n++; }
            ap.Counts["settings files"] = n;
        }
        if (o.DesktopSettings)
        {
            int n = 0;
            foreach (string f in new[] { "claude_desktop_config.json", "config.json" })
            {
                string path = Path.Combine(data, f);
                if (File.Exists(path)) { items.Add(new Item(pd + f, path, f == "config.json")); n++; }
            }
            ap.Counts["desktop settings"] = n;
        }
        if (o.Cowork && a.Uuid != null)
            ap.Counts["cowork records"] = AddTree(items, data, Path.Combine(data, "local-agent-mode-sessions", a.Uuid), pd);
        return items;
    }

    static string StripSignIn(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject d) return json;
        foreach (string k in d.Select(kv => kv.Key).Where(k => k.StartsWith("oauth", StringComparison.OrdinalIgnoreCase)).ToList()) d.Remove(k);
        return d.ToJsonString();
    }

    public static long Export(ExportOptions o, string outPath, Action<string> progress)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "claude-switch-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var manifestProfiles = new JsonArray();
            using (var fs = new FileStream(tmp, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                int done = 0;
                for (int i = 0; i < o.Profiles.Count; i++)
                {
                    ProfileEntry p = o.Profiles[i];
                    AccountInfo a = Accounts.Read(p);
                    var ap = new ArchiveProfile();
                    foreach (Item it in Plan(p, i, o, a, ap))
                    {
                        try
                        {
                            ZipArchiveEntry e = zip.CreateEntry(it.Entry, CompressionLevel.Optimal);
                            e.LastWriteTime = File.GetLastWriteTime(it.Source);
                            using Stream w = e.Open();
                            if (it.StripSignIn) { byte[] b = Encoding.UTF8.GetBytes(StripSignIn(Json.ReadShared(it.Source))); w.Write(b, 0, b.Length); }
                            else { using var r = new FileStream(it.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); r.CopyTo(w); }
                        }
                        catch (IOException) { }   // a file vanished mid-export
                        if (++done % 25 == 0) progress?.Invoke("Exporting… " + done + " files");
                    }
                    var counts = new JsonObject();
                    foreach (var kv in ap.Counts) counts[kv.Key] = kv.Value;
                    manifestProfiles.Add(new JsonObject
                    {
                        ["index"] = i, ["name"] = p.Name, ["email"] = a.Email ?? p.Email, ["accountUuid"] = a.Uuid ?? p.AccountUuid,
                        ["orgName"] = a.OrgName, ["wasDefault"] = p.IsDefault, ["separateConfig"] = !string.IsNullOrEmpty(p.ConfigDir),
                        ["counts"] = counts,
                    });
                }
                var manifest = new JsonObject
                {
                    ["format"] = 1, ["app"] = "Claude Switch " + typeof(Backup).Assembly.GetName().Version,
                    ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ["machine"] = Environment.MachineName,
                    ["os"] = Platform.Name, ["home"] = Platform.Home, ["profiles"] = manifestProfiles,
                };
                using var mw = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
                mw.Write(manifest.ToJsonString());
            }

            progress?.Invoke(o.Password != null ? "Encrypting…" : "Writing…");
            if (File.Exists(outPath)) File.Delete(outPath);
            if (o.Password != null) Encrypt(tmp, outPath, o.Password);
            else
            {
                using var output = new FileStream(outPath, FileMode.CreateNew);
                output.Write(Magic);
                output.WriteByte(0);
                using var input = File.OpenRead(tmp);
                input.CopyTo(output);
            }
            return new FileInfo(outPath).Length;
        }
        finally { try { File.Delete(tmp); } catch (Exception) { } }
    }

    static (byte[] enc, byte[] mac) DeriveKeys(string password, byte[] salt, int iterations)
    {
        byte[] keys = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 64);
        return (keys[..32], keys[32..]);
    }

    static Aes MakeAes(byte[] key, byte[] iv)
    {
        Aes aes = Aes.Create();
        aes.KeySize = 256; aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    static void Encrypt(string zipPath, string outPath, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16), iv = RandomNumberGenerator.GetBytes(16);
        var (encKey, macKey) = DeriveKeys(password, salt, Iterations);
        using (var fs = new FileStream(outPath, FileMode.CreateNew))
        {
            fs.Write(Magic); fs.WriteByte(1); fs.Write(salt); fs.Write(iv); fs.Write(BitConverter.GetBytes(Iterations));
            using Aes aes = MakeAes(encKey, iv);
            using var cs = new CryptoStream(fs, aes.CreateEncryptor(), CryptoStreamMode.Write);
            using var input = File.OpenRead(zipPath);
            input.CopyTo(cs);
        }
        byte[] mac;
        using (var h = new HMACSHA256(macKey)) using (var f = File.OpenRead(outPath)) mac = h.ComputeHash(f);
        using (var f = new FileStream(outPath, FileMode.Append)) f.Write(mac);
    }

    public static bool IsEncrypted(string path)
    {
        using var fs = File.OpenRead(path);
        byte[] head = new byte[8];
        if (fs.Read(head, 0, 8) != 8 || !head.AsSpan(0, 7).SequenceEqual(Magic)) throw new InvalidDataException("This isn't a Claude Switch backup.");
        return head[7] == 1;
    }

    static void CopyBytes(Stream from, Stream to, long count, HMACSHA256 mac)
    {
        byte[] buf = new byte[81920];
        while (count > 0)
        {
            int n = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) throw new InvalidDataException("The backup file is truncated.");
            mac?.TransformBlock(buf, 0, n, null, 0);
            to?.Write(buf, 0, n);
            count -= n;
        }
    }

    static string Unwrap(string path, string password)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "claude-switch-" + Guid.NewGuid().ToString("N") + ".zip");
        using var fs = File.OpenRead(path);
        byte[] head = new byte[8];
        fs.ReadExactly(head);
        if (head[7] == 0) { using var output = new FileStream(tmp, FileMode.CreateNew); fs.CopyTo(output); return tmp; }

        byte[] salt = new byte[16], iv = new byte[16], it = new byte[4];
        fs.ReadExactly(salt); fs.ReadExactly(iv); fs.ReadExactly(it);
        int iterations = BitConverter.ToInt32(it);
        long cipherLen = fs.Length - HeaderLen - 32;
        if (cipherLen <= 0 || iterations < 1000) throw new InvalidDataException("The backup file is damaged.");
        var (encKey, macKey) = DeriveKeys(password ?? "", salt, iterations);

        using (var h = new HMACSHA256(macKey))   // verify before decrypting anything
        {
            fs.Position = 0;
            CopyBytes(fs, null, HeaderLen + cipherLen, h);
            h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            byte[] stored = new byte[32];
            fs.ReadExactly(stored);
            if (!CryptographicOperations.FixedTimeEquals(stored, h.Hash)) throw new CryptographicException("Wrong password, or the file is damaged.");
        }
        fs.Position = HeaderLen;
        using Aes aes = MakeAes(encKey, iv);
        using var outFile = new FileStream(tmp, FileMode.CreateNew);
        using var cs = new CryptoStream(outFile, aes.CreateDecryptor(), CryptoStreamMode.Write);
        CopyBytes(fs, cs, cipherLen, null);
        return tmp;
    }

    public static ArchiveInfo Open(string path, string password)
    {
        var info = new ArchiveInfo { SourcePath = path, ZipPath = Unwrap(path, password) };
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(info.ZipPath);
            ZipArchiveEntry me = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("The backup has no manifest.");
            using var r = new StreamReader(me.Open());
            var m = JsonNode.Parse(r.ReadToEnd()) as JsonObject;
            info.OldHome = Json.Str(m, "home");
            info.Created = Json.Str(m, "created");
            info.Machine = Json.Str(m, "machine");
            foreach (JsonNode n in Json.Arr(m, "profiles") ?? new JsonArray())
            {
                if (n is not JsonObject d) continue;
                var ap = new ArchiveProfile
                {
                    Index = (int)Json.Long(d, "index"), Name = Json.Str(d, "name"), Email = Json.Str(d, "email"),
                    AccountUuid = Json.Str(d, "accountUuid"), OrgName = Json.Str(d, "orgName"),
                    WasDefault = Json.Bool(d, "wasDefault") || Json.Str(d, "wasDefault") == "True",
                    SeparateConfig = Json.Bool(d, "separateConfig") || Json.Str(d, "separateConfig") == "True",
                };
                if (Json.Obj(d, "counts") is JsonObject c) foreach (var kv in c) ap.Counts[kv.Key] = (int)Json.Long(c, kv.Key);
                info.Profiles.Add(ap);
            }
        }
        catch (Exception) { info.Dispose(); throw; }
        return info;
    }

    // Where an archived profile should go: same account, else same name, else new.
    public static ImportChoice Suggest(ArchiveProfile s, List<ProfileEntry> profiles, Dictionary<string, AccountInfo> accounts)
    {
        var c = new ImportChoice { Source = s };
        c.Target = profiles.FirstOrDefault(p => s.AccountUuid != null &&
                       (string.Equals(p.AccountUuid, s.AccountUuid, StringComparison.OrdinalIgnoreCase) ||
                        (accounts.TryGetValue(p.Name, out var a) && string.Equals(a.Uuid, s.AccountUuid, StringComparison.OrdinalIgnoreCase))))
                   ?? Profiles.Find(profiles, s.Name);
        if (c.Target != null) return c;

        bool defaultFree = !profiles.Any(p => p.IsDefault);
        c.Target = new ProfileEntry
        {
            Name = s.Name,
            DataDir = Platform.Compress(s.WasDefault && defaultFree ? Platform.DefaultDataDir : Platform.NewDataDir(s.Name)),
            ConfigDir = s.SeparateConfig ? Platform.Compress(Platform.NewConfigDir(s.Name)) : null,
            AccountUuid = s.AccountUuid, Email = s.Email,
        };
        c.CreateNew = true;
        return c;
    }

    public static ImportResult Import(ArchiveInfo info, List<ImportChoice> choices, Action<string> progress)
    {
        var result = new ImportResult();
        string oldHome = (info.OldHome ?? Platform.Home).TrimEnd('\\', '/'), newHome = Platform.Home.TrimEnd('\\', '/');
        bool remap = oldHome != newHome;
        string oldEnc = Platform.Encode(oldHome), newEnc = Platform.Encode(newHome);
        string oldJson = oldHome.Replace("\\", "\\\\"), newJson = newHome.Replace("\\", "\\\\");

        List<ProfileEntry> profiles = Profiles.LoadOrEmpty();
        int done = 0;
        using (ZipArchive zip = ZipFile.OpenRead(info.ZipPath))
        {
            foreach (ImportChoice c in choices)
            {
                if (c.CreateNew && Profiles.Find(profiles, c.Target.Name) == null) { profiles.Add(c.Target); result.Notes.Add("Created profile " + c.Target.Name + "."); }
                string data = c.Target.Data, cfg = c.Target.Config, prefix = "profiles/" + c.Source.Index + "/";
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    if (!e.FullName.StartsWith(prefix, StringComparison.Ordinal) || e.FullName.EndsWith('/')) continue;
                    string rest = e.FullName[prefix.Length..];
                    bool isData = rest.StartsWith("data/", StringComparison.Ordinal);
                    string rel = rest[(rest.IndexOf('/') + 1)..];
                    if (rel.Contains("..")) continue;   // never write outside the profile
                    if (remap && rel.StartsWith("projects/" + oldEnc, StringComparison.OrdinalIgnoreCase))
                        rel = "projects/" + newEnc + rel[("projects/" + oldEnc).Length..];
                    string dst = Path.Combine(isData ? data : cfg, rel.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(dst))
                    {
                        if (isData && rel == "claude_desktop_config.json")   // keep this PC's MCP config; leave the imported one beside it
                        {
                            dst = Path.Combine(data, "claude_desktop_config.imported.json");
                            result.Notes.Add(c.Target.Name + ": kept this computer's MCP settings; imported ones saved as claude_desktop_config.imported.json.");
                            if (File.Exists(dst)) { result.Skipped++; continue; }
                        }
                        else { result.Skipped++; continue; }
                    }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        string ext = Path.GetExtension(dst).ToLowerInvariant();
                        if (remap && (ext == ".json" || ext == ".jsonl"))
                        {
                            using var r = new StreamReader(e.Open());
                            File.WriteAllText(dst, r.ReadToEnd().Replace(oldJson, newJson).Replace(oldHome, newHome), new UTF8Encoding(false));
                        }
                        else { using Stream s = e.Open(); using var w = new FileStream(dst, FileMode.CreateNew); s.CopyTo(w); }
                        File.SetLastWriteTime(dst, e.LastWriteTime.LocalDateTime);
                        result.Files++;
                    }
                    catch (Exception ex) { result.Errors.Add(rel + ": " + ex.Message); }
                    if (++done % 25 == 0) progress?.Invoke("Importing… " + done + " files");
                }
                if (c.Source.Email != null) result.Notes.Add("Sign in to " + c.Target.Name + " as " + c.Source.Email + " when you open it.");
            }
        }
        if (choices.Any(c => c.CreateNew))
        {
            Profiles.Save(profiles);
            foreach (ImportChoice c in choices.Where(c => c.CreateNew)) Shortcuts.Create(c.Target);
        }
        return result;
    }
}
