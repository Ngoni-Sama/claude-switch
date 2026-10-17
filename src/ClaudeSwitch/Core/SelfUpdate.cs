// Claude Switch keeping itself up to date.
//
// Its only network request: once a day it asks GitHub for this project's
// latest release. Before installing anything it checks two things:
//
//   1. SHA256SUMS.txt.sig is a valid signature of SHA256SUMS.txt by the key
//      whose public half is built into this app (the private half never
//      leaves the maintainer and the release workflow's secret store);
//   2. the downloaded installer's SHA-256 matches its line in SHA256SUMS.txt.
//
// Anything that fails either check is deleted and nothing is installed.
// Windows installs silently and reopens Claude Switch; macOS and Linux get
// the release page, because their installers need you there anyway.

using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

public enum SelfUpdateMode { Off, Notify, Automatic }

public class ReleaseInfo
{
    public Version Version;
    public string Tag, PageUrl, Notes;
    public Dictionary<string, string> Assets = new(StringComparer.OrdinalIgnoreCase);   // name -> download URL
}

public static class SelfUpdate
{
    public const string Repo = "Ngoni-Sama/claude-switch";

    // ECDSA P-256 public key (SubjectPublicKeyInfo, DER, base64).
    const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjuu/41nWuNJDc6OzRgcTMT2nCneyZAegEgNRK0mHrmm6NpysH9Q98TvLwJeeNIAtvlXB+uZ0/twWkYiXLArWKw==";

    public static SelfUpdateMode Mode
    {
        get => Settings.SelfUpdate switch { "off" => SelfUpdateMode.Off, "auto" => SelfUpdateMode.Automatic, _ => SelfUpdateMode.Notify };
        set => Settings.SelfUpdate = value switch { SelfUpdateMode.Off => "off", SelfUpdateMode.Automatic => "auto", _ => "notify" };
    }

    // Set by the app's daily check: a newer release, or null.
    public static ReleaseInfo Available { get; set; }

    public static Version Current => typeof(SelfUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0);

    static HttpClient Http()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeSwitch/" + Current.ToString(3) + " (+https://github.com/" + Repo + ")");
        return c;
    }

    /// <summary>The latest release if it's newer than this build, else null.</summary>
    public static async Task<ReleaseInfo> CheckAsync()
    {
        using HttpClient http = Http();
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        string json = await http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest");
        Settings.SelfUpdateCheckedAt = DateTime.Now;
        var o = JsonNode.Parse(json) as JsonObject;
        string tag = Json.Str(o, "tag_name") ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version v)) return null;
        var r = new ReleaseInfo { Version = v, Tag = tag, PageUrl = Json.Str(o, "html_url"), Notes = Json.Str(o, "body") };
        foreach (JsonObject a in (Json.Arr(o, "assets") ?? new JsonArray()).OfType<JsonObject>())
            if (Json.Str(a, "name") is string name && Json.Str(a, "browser_download_url") is string url) r.Assets[name] = url;
        return Normalize(v) > Normalize(Current) ? r : null;
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    // ---- verification ---------------------------------------------------------------

    /// <summary>True when <paramref name="signature"/> (DER) signs <paramref name="data"/> with the given key.</summary>
    public static bool VerifySignature(byte[] data, byte[] signature, string publicKeyBase64 = PublicKey)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    /// <summary>The SHA-256 the checksum file lists for a file name, lower-case hex, or null.</summary>
    public static string ExpectedHash(string sums, string fileName)
    {
        foreach (string line in sums.Split('\n'))
        {
            string[] parts = line.Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[1].TrimStart('*').Trim(), fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    // ---- installing --------------------------------------------------------------

    static string InstallerName(ReleaseInfo r) =>
        r.Assets.Keys.FirstOrDefault(n => n.EndsWith("-windows-x64-setup.exe", StringComparison.OrdinalIgnoreCase));

    public static bool CanInstallHere(ReleaseInfo r) => Platform.IsWindows && InstallerName(r) != null;

    /// <summary>
    /// Downloads, verifies and starts the installer, which closes and reopens
    /// Claude Switch. Returns null when the installer is running, or why not.
    /// </summary>
    public static async Task<string> InstallAsync(ReleaseInfo r, Action<string> progress)
    {
        if (!Platform.IsWindows) { Links.Open(r.PageUrl); return null; }
        string name = InstallerName(r);
        if (name == null || !r.Assets.ContainsKey("SHA256SUMS.txt") || !r.Assets.ContainsKey("SHA256SUMS.txt.sig"))
            return "This release isn't signed, so it won't be installed automatically. Download it from the release page if you trust it.";

        string dir = Path.Combine(Path.GetTempPath(), "claude-switch-update-" + r.Version);
        Directory.CreateDirectory(dir);
        string installer = Path.Combine(dir, name);
        try
        {
            using HttpClient http = Http();
            progress?.Invoke("Checking the release signature…");
            byte[] sums = await http.GetByteArrayAsync(r.Assets["SHA256SUMS.txt"]);
            byte[] sig = await http.GetByteArrayAsync(r.Assets["SHA256SUMS.txt.sig"]);
            if (!VerifySignature(sums, sig)) return "The release's signature doesn't match Claude Switch's key, so it wasn't installed.";
            string expected = ExpectedHash(System.Text.Encoding.UTF8.GetString(sums), name);
            if (expected == null) return "The release doesn't list a checksum for its installer, so it wasn't installed.";

            progress?.Invoke("Downloading Claude Switch " + r.Version.ToString(3) + "…");
            await using (Stream src = await http.GetStreamAsync(r.Assets[name]))
            await using (FileStream dst = File.Create(installer)) await src.CopyToAsync(dst);

            progress?.Invoke("Checking the download…");
            string actual;
            await using (FileStream f = File.OpenRead(installer)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
            if (actual != expected) { File.Delete(installer); return "The download doesn't match the signed checksum, so it was deleted and nothing was installed."; }
        }
        catch (Exception ex) { ErrorLog.Write(ex); try { File.Delete(installer); } catch (Exception) { } return "Couldn't download the update: " + ex.Message; }

        // A separate process runs the installer (which closes this app) and then
        // reopens Claude Switch, so the update finishes after we're gone.
        string app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Claude Switch", "ClaudeSwitch.exe");
        string script = "Start-Process -FilePath " + Q(installer) + " -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait; " +
                        "Start-Process -FilePath " + Q(app) + "; Remove-Item -LiteralPath " + Q(dir) + " -Recurse -Force -ErrorAction SilentlyContinue";
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(a);
        Process.Start(psi);
        return null;
    }

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";
}
