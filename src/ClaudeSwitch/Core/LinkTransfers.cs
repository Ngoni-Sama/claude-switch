// Linked computers: sending and receiving, resumable.
//
// Every transfer is known by the relay's message id, shown to people as a short
// id like #A1B2C3 on both computers. The sender keeps a record of each one
// (link/outgoing/<id>.json, with the packed archive until every part is up),
// so an interrupted upload picks up where it stopped, even after a restart,
// sending only the parts the relay doesn't have. Sending the same profiles to
// the same computer again replaces the copy still waiting there instead of
// adding a second one. The receiver keeps its partial download
// (link/received/<id>.partial) and resumes it the same way.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

// A transfer this computer sent (or is sending).
public class Outgoing
{
    public string Id, To, ToName, Token, Ctx, Enc, Mac, Fingerprint;
    public long Size;
    public int Parts, Chunk;
    public List<string> Profiles = new();
    public DateTime Created = DateTime.Now, Finished = DateTime.MinValue;
    public string State = "uploading";   // uploading, waiting (all parts up), collected, expired, failed
    public string Error;
    public string ShortId => Link.ShortId(Id);
    public bool Uploading => State == "uploading";
}

public static partial class Link
{
    const int Parallel = 4;   // parts in flight at once: the relay's database write dominates, so overlap them

    public static string ShortId(string id) => id == null ? "" : "#" + id[..Math.Min(6, id.Length)].ToUpperInvariant();

    static string OutgoingDir => Path.Combine(Folder, "outgoing");
    static string Archive(string id) => Path.Combine(OutgoingDir, id + ".claudeprofiles");

    // ---- live progress (UI subscribes) ------------------------------------------------

    static readonly ConcurrentDictionary<string, LinkProgress> live = new();
    static readonly ConcurrentDictionary<string, bool> active = new();
    public static event Action<string, LinkProgress> Progress;

    public static LinkProgress LiveProgress(string id) => id != null && live.TryGetValue(id, out var p) ? p : null;
    public static bool IsActive(string id) => id != null && active.ContainsKey(id);

    static void Report(string id, LinkProgress p, Action<LinkProgress> also)
    {
        live[id] = p;
        also?.Invoke(p);
        Progress?.Invoke(id, p);
    }

    // Something is moving: poll the relay every few seconds instead of minutes.
    public static bool Busy => !active.IsEmpty || Inbox.Any(m => m.Kind == "transfer" && !m.Complete) || Outgoings().Any(o => o.Uploading || o.State == "waiting");

    // ---- outgoing records ---------------------------------------------------------------

    public static List<Outgoing> Outgoings()
    {
        var list = new List<Outgoing>();
        if (!Directory.Exists(OutgoingDir)) return list;
        foreach (string f in Directory.GetFiles(OutgoingDir, "*.json"))
        {
            JsonObject o = Json.ReadObject(f);
            if (o == null || Json.Str(o, "id") == null) continue;
            list.Add(new Outgoing
            {
                Id = Json.Str(o, "id"), To = Json.Str(o, "to"), ToName = Json.Str(o, "toName"), Token = Json.Str(o, "token"),
                Ctx = Json.Str(o, "ctx"), Enc = Json.Str(o, "enc"), Mac = Json.Str(o, "mac"), Fingerprint = Json.Str(o, "fingerprint"),
                Size = Json.Long(o, "size"), Parts = (int)Json.Long(o, "parts"), Chunk = (int)Json.Long(o, "chunk"),
                Profiles = (Json.Arr(o, "profiles") ?? new JsonArray()).Select(n => n?.ToString()).Where(n => n != null).ToList(),
                Created = Json.FromEpochMs(Json.Long(o, "created")), Finished = Json.FromEpochMs(Json.Long(o, "finished")),
                State = Json.Str(o, "state") ?? "uploading", Error = Json.Str(o, "error"),
            });
        }
        return list.OrderByDescending(o => o.Created).ToList();
    }

    static void Save(Outgoing o)
    {
        Directory.CreateDirectory(OutgoingDir);
        var j = new JsonObject
        {
            ["id"] = o.Id, ["to"] = o.To, ["toName"] = o.ToName, ["token"] = o.Token, ["ctx"] = o.Ctx, ["enc"] = o.Enc, ["mac"] = o.Mac,
            ["fingerprint"] = o.Fingerprint, ["size"] = o.Size, ["parts"] = o.Parts, ["chunk"] = o.Chunk,
            ["profiles"] = new JsonArray(o.Profiles.Select(p => (JsonNode)p).ToArray()),
            ["created"] = Json.ToEpochMs(o.Created), ["finished"] = o.Finished == DateTime.MinValue ? 0 : Json.ToEpochMs(o.Finished),
            ["state"] = o.State, ["error"] = o.Error,
        };
        Json.WriteAtomic(Path.Combine(OutgoingDir, o.Id + ".json"), j.ToJsonString(Json.Options));
    }

    static void Forget(Outgoing o)
    {
        try { File.Delete(Path.Combine(OutgoingDir, o.Id + ".json")); } catch (Exception) { }
        try { File.Delete(Archive(o.Id)); } catch (Exception) { }
        live.TryRemove(o.Id, out _);
    }

    static string FingerprintOf(LinkPeer peer, ExportOptions o) =>
        peer.Address + "|" + string.Join(",", o.Profiles.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) +
        "|" + o.Sessions + o.Memory + o.CodeConfig + o.DesktopSettings + o.Cowork;

    // ---- sending ------------------------------------------------------------------------

    // Export the chosen profiles and send them, encrypted, to a linked computer.
    // Returns the transfer. If the same profiles are already on their way to it,
    // that transfer resumes; if they're already waiting there, a fresh copy
    // replaces the waiting one.
    public static async Task<Outgoing> SendProfiles(LinkPeer peer, ExportOptions o, Action<LinkProgress> progress, CancellationToken cancel = default)
    {
        LinkIdentity me = Identity ?? throw new LinkException("Linked computers is off.");
        if (!peer.Linked) throw new LinkException(peer.Name + " hasn't accepted the link yet.");
        string fingerprint = FingerprintOf(peer, o);
        using RelayClient c = Client();

        foreach (Outgoing old in Outgoings().Where(x => x.Fingerprint == fingerprint))
        {
            if (old.Uploading && File.Exists(Archive(old.Id)) && !IsActive(old.Id)) return await Upload(old, progress, cancel);
            if (old.Uploading || old.State == "waiting")
            {
                try { await c.CancelSent(old.To, old.Id, old.Token); } catch (LinkException) { }   // replaced by this one
                Forget(old);
            }
        }

        o.Password = null;   // the transfer is encrypted to that computer instead
        Directory.CreateDirectory(OutgoingDir);
        string tmp = Path.Combine(OutgoingDir, "packing-" + Guid.NewGuid().ToString("N") + ".claudeprofiles");
        try
        {
            progress?.Invoke(new LinkProgress("Packing profiles…", 0, 0));
            await Task.Run(() => Backup.Export(o, tmp, t => progress?.Invoke(new LinkProgress(t, 0, 0))), cancel);
            long size = new FileInfo(tmp).Length;
            int chunk = await c.MaxPart() - 128;   // room for IV, padding and MAC
            int parts = (int)Math.Max(1, (size + chunk - 1) / chunk);
            JsonObject meta = MyMeta();
            meta["profiles"] = new JsonArray(o.Profiles.Select(p => (JsonNode)p.Name).ToArray());
            Posted m = await Post(c, me, "transfer", peer.Address, peer.Ecdh, meta, size, parts);
            var rec = new Outgoing
            {
                Id = m.Id, To = peer.Address, ToName = peer.Name, Token = m.Token, Fingerprint = fingerprint,
                Ctx = Convert.ToBase64String(m.Ctx), Enc = Convert.ToBase64String(m.Keys.enc), Mac = Convert.ToBase64String(m.Keys.mac),
                Size = size, Parts = parts, Chunk = chunk, Profiles = o.Profiles.Select(p => p.Name).ToList(),
            };
            File.Move(tmp, Archive(rec.Id));
            Save(rec);
            Changed?.Invoke();
            return await Upload(rec, progress, cancel);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } }
    }

    // Upload whatever parts the relay doesn't have yet.
    static async Task<Outgoing> Upload(Outgoing rec, Action<LinkProgress> progress, CancellationToken cancel)
    {
        if (!active.TryAdd(rec.Id, true)) throw new LinkException(rec.ShortId + " is already being sent.");
        try
        {
            using RelayClient c = Client();
            HashSet<int> have = await c.SenderStatus(rec.To, rec.Id, rec.Token, cancel);
            if (have == null)
            {
                rec.State = "expired"; rec.Error = "The relay no longer has this transfer; send it again.";
                Save(rec); Changed?.Invoke();
                throw new LinkException(rec.Error);
            }
            var keys = (Convert.FromBase64String(rec.Enc), Convert.FromBase64String(rec.Mac));
            byte[] ctx = Convert.FromBase64String(rec.Ctx);
            long already = have.Sum(n => PlainSize(rec, n));
            var meter = new LinkMeter("Sending", rec.Size, p => Report(rec.Id, p, progress), already);
            var missing = Enumerable.Range(0, rec.Parts).Where(n => !have.Contains(n)).ToList();
            using (var fs = File.OpenRead(Archive(rec.Id)))
            {
                for (int i = 0; i < missing.Count; i += Parallel)
                {
                    var batch = new List<Task>();
                    foreach (int n in missing.Skip(i).Take(Parallel))
                    {
                        cancel.ThrowIfCancellationRequested();
                        byte[] buf = new byte[PlainSize(rec, n)];
                        fs.Position = (long)n * rec.Chunk;
                        fs.ReadExactly(buf);
                        byte[] body = LinkCrypto.Seal(keys, ctx, (uint)n, n == rec.Parts - 1, buf);
                        batch.Add(Task.Run(async () => { await c.PutPart(rec.To, rec.Id, rec.Token, n, body, cancel); meter.Add(buf.Length); }, cancel));
                    }
                    await Task.WhenAll(batch);
                    cancel.ThrowIfCancellationRequested();
                }
            }
            meter.Done();
            rec.State = "waiting"; rec.Error = null; rec.Finished = DateTime.Now;
            Save(rec);
            try { File.Delete(Archive(rec.Id)); } catch (Exception) { }
            Changed?.Invoke();
            return rec;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (rec.Uploading) { rec.Error = ex.Message; Save(rec); }
            Report(rec.Id, new LinkProgress("Paused: " + ex.Message, LiveProgress(rec.Id)?.Done ?? 0, rec.Size), progress);
            throw;
        }
        finally { active.TryRemove(rec.Id, out _); Changed?.Invoke(); }
    }

    static int PlainSize(Outgoing rec, int n) => (int)Math.Min(rec.Chunk, rec.Size - (long)n * rec.Chunk);

    // Pick up uploads an earlier run (or a lost connection) left half done.
    public static async Task ResumeOutgoing()
    {
        if (Identity == null) return;
        foreach (Outgoing rec in Outgoings().Where(o => o.Uploading && !IsActive(o.Id) && File.Exists(Archive(o.Id))))
        {
            try { await Upload(rec, null, default); }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }
    }

    // Has the other computer collected what's waiting for it?
    public static async Task CheckOutgoing()
    {
        if (Identity == null) return;
        bool changed = false;
        foreach (Outgoing rec in Outgoings())
        {
            if (rec.State == "waiting")
            {
                try
                {
                    using RelayClient c = Client();
                    if (await c.SenderStatus(rec.To, rec.Id, rec.Token) == null)
                    {
                        rec.State = DateTime.Now - rec.Created > TimeSpan.FromDays(7) ? "expired" : "collected";
                        rec.Finished = DateTime.Now;
                        Save(rec);
                        changed = true;
                    }
                }
                catch (LinkException) { }
            }
            else if (rec.State is "collected" or "expired" or "failed" && DateTime.Now - rec.Finished > TimeSpan.FromDays(3))
            {
                Forget(rec);   // keep a few days of history
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    // Stop a transfer this computer sent; the other computer won't get it.
    public static async Task CancelOutgoing(Outgoing rec)
    {
        try { using RelayClient c = Client(); await c.CancelSent(rec.To, rec.Id, rec.Token); }
        catch (LinkException ex) { ErrorLog.Write(ex); }
        Forget(rec);
        Changed?.Invoke();
    }

    public static void ClearOutgoing(Outgoing rec) { Forget(rec); Changed?.Invoke(); }

    // ---- incoming ----------------------------------------------------------------------

    // The sender's upload speed, from how many parts arrived between two checks.
    static readonly ConcurrentDictionary<string, (int parts, DateTime at)> seenParts = new();

    static void TrackUploads(List<LinkMessage> inbox)
    {
        foreach (LinkMessage m in inbox.Where(m => m.Kind == "transfer"))
        {
            if (seenParts.TryGetValue(m.Id, out var before) && m.Received > before.parts && m.Parts > 0)
            {
                double secs = (DateTime.Now - before.at).TotalSeconds;
                if (secs > 0.5) m.UploadRate = (m.Received - before.parts) * ((double)m.Size / m.Parts) / secs;
            }
            else if (Inbox.FirstOrDefault(x => x.Id == m.Id) is LinkMessage old) m.UploadRate = old.UploadRate;
            if (!seenParts.TryGetValue(m.Id, out var last) || last.parts != m.Received) seenParts[m.Id] = (m.Received, DateTime.Now);
        }
    }

    static string Partial(string id) => Path.Combine(ReceivedDir, id + ".partial");
    static string PartialState(string id) => Path.Combine(ReceivedDir, id + ".partial.json");

    // Download and decrypt a transfer to a backup file this computer can import.
    // An interrupted download resumes from the last whole batch. The relay copy
    // is deleted once it has arrived intact.
    public static async Task<string> Receive(LinkMessage m, Action<LinkProgress> progress, CancellationToken cancel = default)
    {
        LinkIdentity me = Identity ?? throw new LinkException("Linked computers is off.");
        if (!m.Complete) throw new LinkException(m.ShortId + " is still uploading from " + m.Name + ".");
        if (!active.TryAdd(m.Id, true)) throw new LinkException(m.ShortId + " is already being received.");
        try
        {
            Directory.CreateDirectory(ReceivedDir);
            byte[] ctx = Convert.FromBase64String(m.Ctx);
            var keys = LinkCrypto.DeriveKeys(me.Ecdh, m.Eph, ctx);
            using RelayClient c = Client();

            // Resume what an earlier attempt already wrote.
            int doneParts = 0;
            long doneBytes = 0;
            JsonObject state = Json.ReadObject(PartialState(m.Id));
            if (state != null && File.Exists(Partial(m.Id)))
            {
                doneParts = (int)Json.Long(state, "parts");
                doneBytes = Json.Long(state, "bytes");
            }
            var meter = new LinkMeter("Receiving", m.Size, p => Report(m.Id, p, progress), doneBytes);
            using (var fs = new FileStream(Partial(m.Id), FileMode.OpenOrCreate))
            {
                fs.SetLength(doneBytes);
                fs.Position = doneBytes;
                for (int first = doneParts; first < m.Parts; first += Parallel)
                {
                    cancel.ThrowIfCancellationRequested();
                    var batch = Enumerable.Range(first, Math.Min(Parallel, m.Parts - first))
                        .Select(n => Task.Run(async () =>
                        {
                            byte[] plain = LinkCrypto.Open(keys, ctx, (uint)n, n == m.Parts - 1, await c.GetPart(me, m.Id, n, cancel));
                            meter.Add(plain.Length);
                            return plain;
                        }, cancel)).ToList();
                    foreach (byte[] plain in await Task.WhenAll(batch)) { fs.Write(plain); doneBytes += plain.Length; }
                    fs.Flush(true);
                    doneParts = Math.Min(m.Parts, first + Parallel);
                    Json.WriteAtomic(PartialState(m.Id), new JsonObject { ["parts"] = doneParts, ["bytes"] = doneBytes }.ToJsonString());
                }
            }
            if (doneBytes != m.Size)
            {
                File.Delete(Partial(m.Id)); File.Delete(PartialState(m.Id));
                throw new LinkException(m.ShortId + " arrived incomplete. Ask " + m.Name + " to send it again.");
            }
            meter.Done();
            string safe = new string(m.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            string path = Path.Combine(ReceivedDir, DateTime.Now.ToString("yyyy-MM-dd-HHmm") + "-from-" + (safe.Length > 0 ? safe : "computer") +
                                                    "-" + m.ShortId.TrimStart('#') + ".claudeprofiles");
            File.Move(Partial(m.Id), path, true);
            File.Delete(PartialState(m.Id));
            try { await c.Delete(me, m.Id); } catch (Exception ex) { ErrorLog.Write(ex); }
            Inbox = Inbox.Where(x => x.Id != m.Id).ToList();
            return path;
        }
        finally { active.TryRemove(m.Id, out _); Changed?.Invoke(); }
    }
}
