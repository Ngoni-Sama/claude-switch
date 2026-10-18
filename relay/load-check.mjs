// Sends one transfer of N parts through a relay and reports each part's status
// and time, then deletes it:   node relay/load-check.mjs <relay url> [parts] [parallel]
const base = (process.argv[2] || 'http://127.0.0.1:8787').replace(/\/$/, '');
const parts = Number(process.argv[3] || 20), parallel = Number(process.argv[4] || 1);
const subtle = crypto.subtle;
const B32 = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const b64 = b => Buffer.from(b).toString('base64url');
const sha = async b => new Uint8Array(await subtle.digest('SHA-256', b));
const hex = b => Buffer.from(b).toString('hex');
function b32(bytes) {
  let bits = 0, value = 0, out = '';
  for (const b of bytes) { value = (value << 8) | b; bits += 8; while (bits >= 5) { out += B32[(value >>> (bits - 5)) & 31]; bits -= 5; } }
  if (bits > 0) out += B32[(value << (5 - bits)) & 31];
  return out;
}
async function identity() {
  const ecdh = await subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
  const sig = await subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
  const e = new Uint8Array(await subtle.exportKey('spki', ecdh.publicKey)), s = new Uint8Array(await subtle.exportKey('spki', sig.publicKey));
  const both = new Uint8Array(e.length + s.length); both.set(e); both.set(s, e.length);
  return { ecdh: b64(e), sig: b64(s), sigKey: sig.privateKey, secret: crypto.getRandomValues(new Uint8Array(32)), addr: b32((await sha(both)).slice(0, 15)) };
}
const sign = async (id, bytes) => b64(new Uint8Array(await subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, id.sigKey, bytes)));
async function register(id) {
  const secretHash = hex(await sha(id.secret)), ts = Math.floor(Date.now() / 1000);
  const r = await fetch(base + '/v1/keys/' + id.addr, { method: 'PUT', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ ecdh: id.ecdh, sig: id.sig, secretHash, ts, proof: await sign(id, new TextEncoder().encode('register|' + id.addr + '|' + secretHash + '|' + ts)) }) });
  if (!r.ok) throw new Error('register ' + r.status + ' ' + await r.text());
}
const a = await identity(), b = await identity();
await register(a); await register(b);
const header = new TextEncoder().encode(JSON.stringify({ v: 1, kind: 'transfer', from: a.addr, to: b.addr, eph: 'x', created: Math.floor(Date.now() / 1000), size: parts * 999_872, parts, meta: 'x' }));
let r = await fetch(base + '/v1/mail/' + b.addr, { method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ header: b64(header), signature: await sign(a, header), size: parts * 999_872, parts }) });
const msg = await r.json();
if (!msg.id) { console.log('post failed', r.status, msg); process.exit(1); }
const body = crypto.getRandomValues(new Uint8Array(65536));
const part = new Uint8Array(999_920); for (let i = 0; i < part.length; i += body.length) part.set(body.subarray(0, Math.min(body.length, part.length - i)), i);
const t0 = Date.now(); const statuses = {};
let next = 0;
async function worker() {
  while (next < parts) {
    const n = next++, t = Date.now();
    const res = await fetch(`${base}/v1/mail/${b.addr}/${msg.id}/${n}`, { method: 'PUT', headers: { 'x-upload-token': msg.uploadToken }, body: part });
    const text = res.ok ? '' : (await res.text()).slice(0, 160);
    statuses[res.status] = (statuses[res.status] || 0) + 1;
    console.log(`part ${n} -> ${res.status} in ${Date.now() - t} ms ${text}`);
  }
}
await Promise.all(Array.from({ length: parallel }, worker));
const secs = (Date.now() - t0) / 1000;
console.log(`\n${parts} parts in ${secs.toFixed(1)} s = ${(parts / secs).toFixed(2)} MB/s; statuses ${JSON.stringify(statuses)}`);
// Download every part back as the recipient.
const auth = { authorization: 'Mailbox ' + b64(b.secret) };
const t1 = Date.now(), down = {};
let nextDown = 0;
async function downloader() {
  while (nextDown < parts) {
    const n = nextDown++, t = Date.now();
    const res = await fetch(`${base}/v1/mail/${b.addr}/${msg.id}/${n}`, { headers: auth });
    const buf = new Uint8Array(await res.arrayBuffer());
    down[res.status] = (down[res.status] || 0) + 1;
    const same = res.ok && buf.length === part.length && Buffer.from(buf).equals(Buffer.from(part));
    console.log(`get ${n} -> ${res.status} ${buf.length} bytes ${same ? 'intact' : 'DIFFERENT ' + Buffer.from(buf.slice(0, 120)).toString()} in ${Date.now() - t} ms`);
  }
}
await Promise.all(Array.from({ length: parallel }, downloader));
console.log(`downloads in ${((Date.now() - t1) / 1000).toFixed(1)} s; statuses ${JSON.stringify(down)}`);
await fetch(`${base}/v1/mail/${b.addr}/${msg.id}`, { method: 'DELETE', headers: auth });
