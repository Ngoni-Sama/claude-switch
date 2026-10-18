// Claude Switch relay: a mailbox for "Linked computers".
//
// It stores and forwards end-to-end encrypted transfers between computers that
// linked each other by address. It can't read anything: the sender encrypts to
// the recipient's public key before upload, and only the recipient's private key
// (which never leaves that computer) can decrypt. All the relay knows is which
// address sent how many bytes to which address, and it forgets that after
// TTL_DAYS.
//
// Addresses are self-certifying: address = Crockford-base32(SHA-256(ecdhKey ||
// signingKey)[0..15]), so a mailbox can only ever hold the keys that hash to
// it. Mailboxes are read with a secret whose SHA-256 was set at registration;
// only the address's owner can set or change it (a signature by its private
// key, or the current secret), and mailboxes are never recycled, so nobody can
// take over an address. Messages are accepted only when signed by a
// registered sender.
//
// API (JSON unless noted; binary fields are base64url):
//   GET    /v1/info                        limits
//   PUT    /v1/keys/:addr                  { ecdh, sig, secretHash, ts, proof }   register
//   GET    /v1/keys/:addr                  { ecdh, sig }
//   POST   /v1/mail/:to                    { header, signature, size, parts } -> { id, uploadToken }
//   PUT    /v1/mail/:to/:id/:n             X-Upload-Token; base64 text (X-Part-Encoding: base64), or raw bytes
//                                           (older apps). Text is stored and served as-is: the Worker never
//                                           converts a part, which keeps it inside the free plan's CPU limit.
//   GET    /v1/mail/:me                    Authorization: Mailbox <secret> -> { messages }
//   GET    /v1/mail/:me/:id/:n             Authorization -> raw bytes
//   DELETE /v1/mail/:me/:id                Authorization
//   GET    /v1/mail/:to/:id                X-Upload-Token -> { parts, received, have }   (sender: resume)
//   DELETE /v1/mail/:to/:id                X-Upload-Token                                (sender: cancel)

const MAX_PART = 1_000_000;            // D1 rows hold up to 2 MB; stay well under
const MAX_SIZE = 1024 * 1024 * 1024;   // 1 GB per transfer
const MAX_HEADER = 16 * 1024;
const MAX_PENDING = 20;                // per recipient
const MAX_PER_DAY = 60;                // per sender
const TTL_DAYS = 7;
const B32 = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';   // Crockford: no I, L, O, U

const enc = new TextEncoder();
const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
const fail = (status, error) => json({ error }, status);
const now = () => Math.floor(Date.now() / 1000);

function b64d(s) {
  s = String(s || '').replace(/-/g, '+').replace(/_/g, '/');
  const bin = atob(s + '==='.slice((s.length + 3) % 4));
  return Uint8Array.from(bin, c => c.charCodeAt(0));
}

function b32(bytes) {
  let bits = 0, value = 0, out = '';
  for (const b of bytes) {
    value = (value << 8) | b; bits += 8;
    while (bits >= 5) { out += B32[(value >>> (bits - 5)) & 31]; bits -= 5; }
  }
  if (bits > 0) out += B32[(value << (5 - bits)) & 31];
  return out;
}

async function sha256(bytes) { return new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)); }
const hex = b => [...b].map(x => x.toString(16).padStart(2, '0')).join('');

export async function addressOf(ecdh, sig) {
  const a = b64d(ecdh), b = b64d(sig), both = new Uint8Array(a.length + b.length);
  both.set(a); both.set(b, a.length);
  return b32((await sha256(both)).slice(0, 15));
}

const validAddr = a => /^[0-9A-HJKMNP-TV-Z]{24}$/.test(a);

async function authorized(env, req, addr, touch = false) {
  const m = /^Mailbox\s+(\S+)$/.exec(req.headers.get('authorization') || '');
  if (!m) return false;
  const row = await env.DB.prepare('SELECT secret_hash FROM mailbox WHERE addr = ?').bind(addr).first();
  if (!row) return false;
  const given = hex(await sha256(b64d(m[1])));
  if (given.length !== row.secret_hash.length) return false;
  let diff = 0;
  for (let i = 0; i < given.length; i++) diff |= given.charCodeAt(i) ^ row.secret_hash.charCodeAt(i);
  if (diff === 0 && touch) await env.DB.prepare('UPDATE mailbox SET seen = ? WHERE addr = ?').bind(now(), addr).run();
  return diff === 0;
}

// proof = ECDSA("register|<addr>|<secretHash>|<ts>") by the address's signing key.
async function proves(sig, addr, secretHash, ts, proof) {
  if (!proof || !Number.isInteger(ts) || Math.abs(now() - ts) > 600) return false;
  const text = enc.encode('register|' + addr + '|' + secretHash + '|' + ts);
  return verifySigned(sig, btoa(String.fromCharCode(...text)), proof);
}

async function verifySigned(sigKeyB64, headerB64, signatureB64) {
  try {
    const key = await crypto.subtle.importKey('spki', b64d(sigKeyB64), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
    return await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, b64d(signatureB64), b64d(headerB64));
  } catch { return false; }
}

async function register(env, req, addr) {
  let body;
  try { body = await req.json(); } catch { return fail(400, 'bad json'); }
  const { ecdh, sig, secretHash, ts, proof } = body || {};
  if (!ecdh || !sig || !/^[0-9a-f]{64}$/.test(secretHash || '')) return fail(400, 'missing keys');
  if (await addressOf(ecdh, sig) !== addr) return fail(400, 'keys do not match this address');
  const existing = await env.DB.prepare('SELECT ecdh, sig FROM mailbox WHERE addr = ?').bind(addr).first();
  const proven = await proves(sig, addr, secretHash, ts, proof);
  if (proof && !proven) return fail(403, 'bad proof');
  if (existing) {
    if (existing.ecdh !== ecdh || existing.sig !== sig) return fail(409, 'address taken');
    // Same (public) keys: only the owner may set the secret: proof by its
    // private key, or the current secret.
    if (proven || await authorized(env, req, addr)) {
      await env.DB.prepare('UPDATE mailbox SET secret_hash = ? WHERE addr = ?').bind(secretHash, addr).run();
      return json({ ok: true, address: addr });
    }
    return fail(403, 'this address is already registered');
  }
  // A new address. Its keys can't be public yet (they're only published by
  // registering), so first registration doesn't strictly need the proof; the
  // app sends it anyway.
  await env.DB.prepare('INSERT INTO mailbox (addr, ecdh, sig, secret_hash, created, seen) VALUES (?, ?, ?, ?, ?, ?)')
    .bind(addr, ecdh, sig, secretHash, now(), now()).run();
  return json({ ok: true, address: addr }, 201);
}

async function keys(env, addr) {
  const row = await env.DB.prepare('SELECT ecdh, sig FROM mailbox WHERE addr = ?').bind(addr).first();
  return row ? json({ address: addr, ecdh: row.ecdh, sig: row.sig }) : fail(404, 'no such address');
}

async function post(env, req, to) {
  let body;
  try { body = await req.json(); } catch { return fail(400, 'bad json'); }
  const { header, signature, size, parts } = body || {};
  if (typeof header !== 'string' || header.length > MAX_HEADER * 2 || typeof signature !== 'string') return fail(400, 'bad header');
  if (!Number.isInteger(size) || size < 0 || size > MAX_SIZE) return fail(413, 'too large');
  if (!Number.isInteger(parts) || parts < 0 || parts > Math.ceil(MAX_SIZE / MAX_PART) + 1) return fail(400, 'bad parts');

  let h;
  try { h = JSON.parse(new TextDecoder().decode(b64d(header))); } catch { return fail(400, 'bad header'); }
  if (h.to !== to || !validAddr(h.from || '')) return fail(400, 'header does not match');
  if (h.parts !== parts) return fail(400, 'parts mismatch');

  const recipient = await env.DB.prepare('SELECT addr FROM mailbox WHERE addr = ?').bind(to).first();
  if (!recipient) return fail(404, 'no such address');
  const sender = await env.DB.prepare('SELECT sig FROM mailbox WHERE addr = ?').bind(h.from).first();
  if (!sender) return fail(403, 'sender not registered');
  if (!await verifySigned(sender.sig, header, signature)) return fail(403, 'bad signature');

  const t = now();
  const pending = await env.DB.prepare('SELECT COUNT(*) AS n FROM message WHERE to_addr = ? AND expires > ?').bind(to, t).first();
  if (pending.n >= MAX_PENDING) return fail(429, 'recipient mailbox is full');
  const today = await env.DB.prepare('SELECT COUNT(*) AS n FROM message WHERE from_addr = ? AND created > ?').bind(h.from, t - 86400).first();
  if (today.n >= MAX_PER_DAY) return fail(429, 'too many messages today');

  const id = hex(crypto.getRandomValues(new Uint8Array(12)));
  const token = crypto.getRandomValues(new Uint8Array(24));
  const tokenB64 = btoa(String.fromCharCode(...token)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  await env.DB.prepare(`INSERT INTO message (id, to_addr, from_addr, header, signature, size, parts, received, upload_hash, created, expires)
                        VALUES (?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?)`)
    .bind(id, to, h.from, header, signature, size, parts, hex(await sha256(token)), t, t + TTL_DAYS * 86400).run();
  return json({ id, uploadToken: tokenB64 }, 201);
}

async function putPart(env, req, to, id, n) {
  const msg = await env.DB.prepare('SELECT parts, upload_hash FROM message WHERE id = ? AND to_addr = ?').bind(id, to).first();
  if (!msg) return fail(404, 'no such message');
  const token = req.headers.get('x-upload-token') || '';
  if (hex(await sha256(b64d(token))) !== msg.upload_hash) return fail(403, 'bad upload token');
  if (!(n >= 0 && n < msg.parts)) return fail(400, 'bad part');
  let data;
  if ((req.headers.get('x-part-encoding') || '') === 'base64') {
    data = await req.text();
    if (data.length === 0 || data.length > Math.ceil((MAX_PART + 1024) / 3) * 4 + 4 || !/^[A-Za-z0-9+/=_-]+$/.test(data.slice(0, 64)))
      return fail(413, 'part too large');
  } else {
    data = new Uint8Array(await req.arrayBuffer());
    if (data.length === 0 || data.length > MAX_PART + 1024) return fail(413, 'part too large');
  }
  const r = await env.DB.prepare('INSERT OR IGNORE INTO part (msg_id, n, data) VALUES (?, ?, ?)').bind(id, n, data).run();
  if (r.meta.changes > 0) await env.DB.prepare('UPDATE message SET received = received + 1 WHERE id = ?').bind(id).run();
  return json({ ok: true });
}

// The sender, holding its upload token: which parts the relay already has
// (so an interrupted upload resumes instead of starting over), or cancel.
async function senderMessage(env, req, to, id) {
  const msg = await env.DB.prepare('SELECT parts, received, upload_hash, created, expires FROM message WHERE id = ? AND to_addr = ?').bind(id, to).first();
  if (!msg) return fail(404, 'no such message');   // collected, declined or expired
  const token = req.headers.get('x-upload-token') || '';
  if (hex(await sha256(b64d(token))) !== msg.upload_hash) return fail(403, 'bad upload token');
  if (req.method === 'DELETE') {
    await env.DB.batch([
      env.DB.prepare('DELETE FROM part WHERE msg_id = ?').bind(id),
      env.DB.prepare('DELETE FROM message WHERE id = ?').bind(id),
    ]);
    return json({ ok: true });
  }
  const have = await env.DB.prepare('SELECT n FROM part WHERE msg_id = ? ORDER BY n').bind(id).all();
  return json({ id, parts: msg.parts, received: msg.received, have: have.results.map(r => r.n), created: msg.created, expires: msg.expires });
}

async function list(env, me) {
  const rows = await env.DB.prepare(`SELECT id, from_addr, header, signature, size, parts, received, created, expires
                                     FROM message WHERE to_addr = ? AND expires > ? ORDER BY created`).bind(me, now()).all();
  return json({
    messages: rows.results.map(m => ({
      id: m.id, from: m.from_addr, header: m.header, signature: m.signature, size: m.size, parts: m.parts,
      received: m.received, complete: m.received >= m.parts, created: m.created, expires: m.expires,
    })),
  });
}

async function getPart(env, me, id, n) {
  const row = await env.DB.prepare('SELECT p.data FROM part p JOIN message m ON m.id = p.msg_id WHERE m.id = ? AND m.to_addr = ? AND p.n = ?')
    .bind(id, me, n).first();
  if (!row) return fail(404, 'no such part');
  if (typeof row.data === 'string')
    return new Response(row.data, { headers: { 'content-type': 'text/plain', 'x-part-encoding': 'base64' } });
  const data = row.data instanceof ArrayBuffer ? new Uint8Array(row.data) : Uint8Array.from(row.data);
  return new Response(data, { headers: { 'content-type': 'application/octet-stream' } });
}

async function remove(env, id, me) {
  await env.DB.batch([
    env.DB.prepare('DELETE FROM part WHERE msg_id IN (SELECT id FROM message WHERE id = ? AND to_addr = ?)').bind(id, me),
    env.DB.prepare('DELETE FROM message WHERE id = ? AND to_addr = ?').bind(id, me),
  ]);
  return json({ ok: true });
}

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const p = url.pathname.split('/').filter(Boolean);
    try {
      if (p[0] !== 'v1') return url.pathname === '/' ? json({ service: 'claude-switch-relay', docs: 'https://github.com/Ngoni-Sama/claude-switch/tree/main/relay' }) : fail(404, 'not found');
      if (p[1] === 'info' && req.method === 'GET') return json({ version: 1, maxPart: MAX_PART, maxSize: MAX_SIZE, ttlDays: TTL_DAYS });

      const addr = p[2] || '';
      if (!validAddr(addr)) return fail(400, 'bad address');
      if (p[1] === 'keys' && p.length === 3) {
        if (req.method === 'PUT') return register(env, req, addr);
        if (req.method === 'GET') return keys(env, addr);
      }
      if (p[1] === 'mail') {
        if (p.length === 3 && req.method === 'POST') return post(env, req, addr);
        if (p.length === 5 && req.method === 'PUT') return putPart(env, req, addr, p[3], Number(p[4]));
        if (p.length === 4 && req.headers.has('x-upload-token') && ['GET', 'DELETE'].includes(req.method)) return senderMessage(env, req, addr, p[3]);
        if (!await authorized(env, req, addr, p.length === 3)) return fail(401, 'not your mailbox');
        if (p.length === 3 && req.method === 'GET') return list(env, addr);
        if (p.length === 5 && req.method === 'GET') return getPart(env, addr, p[3], Number(p[4]));
        if (p.length === 4 && req.method === 'DELETE') return remove(env, p[3], addr);
      }
      return fail(404, 'not found');
    } catch (e) {
      console.error(e);
      return fail(500, 'relay error');
    }
  },

  // Daily: drop expired transfers. Mailboxes are kept (a few hundred bytes
  // each): recycling one would let someone else register a dormant address.
  async scheduled(_event, env) {
    const t = now();
    await env.DB.batch([
      env.DB.prepare('DELETE FROM part WHERE msg_id IN (SELECT id FROM message WHERE expires <= ?)').bind(t),
      env.DB.prepare('DELETE FROM message WHERE expires <= ?').bind(t),
    ]);
  },
};
