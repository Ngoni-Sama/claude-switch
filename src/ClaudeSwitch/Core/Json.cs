// Small JSON helpers over System.Text.Json for reading Claude's own files
// (whose shapes we don't control) and our settings.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Claude keeps these files open; read without locking them.
    public static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    public static JsonObject ReadObject(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(ReadShared(path)) as JsonObject;
        }
        catch (Exception) { return null; }
    }

    public static string Str(JsonObject o, string key)
    {
        if (o == null || !o.TryGetPropertyValue(key, out JsonNode n) || n == null) return null;
        return n is JsonValue v ? v.ToString() : n.ToJsonString();
    }

    public static JsonObject Obj(JsonObject o, string key) =>
        o != null && o.TryGetPropertyValue(key, out JsonNode n) ? n as JsonObject : null;

    public static JsonArray Arr(JsonObject o, string key) =>
        o != null && o.TryGetPropertyValue(key, out JsonNode n) ? n as JsonArray : null;

    public static long Long(JsonObject o, string key)
    {
        if (o == null || !o.TryGetPropertyValue(key, out JsonNode n) || n is not JsonValue v) return 0;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out double d)) return (long)d;
        return long.TryParse(v.ToString(), out l) ? l : 0;
    }

    public static bool Bool(JsonObject o, string key) =>
        o != null && o.TryGetPropertyValue(key, out JsonNode n) && n is JsonValue v && v.TryGetValue(out bool b) && b;

    public static DateTime FromEpochMs(long ms) =>
        ms <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    public static long ToEpochMs(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

    public static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
