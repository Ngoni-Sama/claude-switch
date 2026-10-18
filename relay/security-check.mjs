// Tries to reach data that isn't yours on a relay, and reports whether each
// attempt was refused. Run against a local or deployed relay:
//   node relay/security-check.mjs https://claude-switch-relay.ma360-ngoni.workers.dev
// It creates a few throwaway addresses; their messages are deleted at the end.
const base = (process.argv[2] || 'http://127.0.0.1:8787').replace(/\/$/, '');
const subtle = crypto.subtle;
const B32 = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const b64 = b => Buffer.from(b).toString('base64url');
const unb64 = s => new Uint8Array(Buffer.from(s, 'base64url'));
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
  const e = new Uint8Array(await subtle.exportKey('spki', ecdh.publicKey));
  const s = new Uint8Array(await subtle.exportKey('spki', sig.publicKey));
  const both = new Uint8Array(e.length + s.length); both.set(e); both.set(s, e.length);
  return { ecdh: b64(e), sig: b64(s), sigKey: sig.privateKey, secret: crypto.getRandomValues(new Uint8Array(32)), addr: b32((await sha(both)).slice(0, 15)) };
}
const sign = async (id, text) => b64(new Uint8Array(await subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, id.sigKey, new TextEncoder().encode(text))));

async function call(method, path, { secret, body, headers = {} } = {}) {
  const h = { ...headers };
  if (secret) h.authorization = 'Mailbox ' + b64(secret);
  if (body && !(body instanceof Uint8Array)) { h['content-type'] = 'application/json'; body = JSON.stringify(body); }
  const r = await fetch(base + path, { method, headers: h, body });
  return { status: r.status, body: await r.text() };
}

async function register(id, secret = id.secret, keysOf = id) {
  const secretHash = hex(await sha(secret));
  const ts = Math.floor(Date.now() / 1000);
  return call('PUT', '/v1/keys/' + id.addr, {
    body: { ecdh: keysOf.ecdh, sig: keysOf.sig, secretHash, ts, proof: await sign(keysOf, 'register|' + id.addr + '|' + secretHash + '|' + ts) },
  });
}

async function post(from, to, parts = 1, signer = from) {
  const header = { v: 1, kind: 'transfer', from: from.addr, to: to.addr, eph: 'x', created: Math.floor(Date.now() / 1000), size: 10, parts, meta: 'x' };
  const hb = new TextEncoder().encode(JSON.stringify(header));
  const signature = b64(new Uint8Array(await subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, signer.sigKey, hb)));
  return call('POST', '/v1/mail/' + to.addr, { body: { header: b64(hb), signature, size: 10, parts } });
}

let failures = 0;
function expect(name, ok, detail) { console.log((ok ? 'REFUSED ' : 'LEAKED  ') + name + (ok ? '' : '  -> ' + detail)); if (!ok) failures++; }
function check(name, cond, detail) { console.log((cond ? 'ok      ' : 'FAILED  ') + name + (cond ? '' : '  -> ' + detail)); if (!cond) failures++; }

const victim = await identity(), sender = await identity(), attacker = await identity();
check('victim registers', [200, 201].includes((await register(victim)).status), '');
check('sender registers', [200, 201].includes((await register(sender)).status), '');
check('attacker registers its own address', [200, 201].includes((await register(attacker)).status), '');

// A real transfer from sender to victim.
const sent = JSON.parse((await post(sender, victim)).body);
check('sender posts to victim', !!sent.id, JSON.stringify(sent));
const up = await call('PUT', `/v1/mail/${victim.addr}/${sent.id}/0`, { body: new Uint8Array([1, 2, 3, 4]), headers: { 'x-upload-token': sent.uploadToken } });
check('sender uploads its part', up.status === 200, up.body);
check('victim can list its mailbox', (await call('GET', '/v1/mail/' + victim.addr, { secret: victim.secret })).body.includes(sent.id), '');

// 1. Read someone else's mailbox.
let r = await call('GET', '/v1/mail/' + victim.addr, { secret: attacker.secret });
expect('list victim mailbox with attacker secret', r.status === 401 && !r.body.includes(sent.id), r.status + ' ' + r.body.slice(0, 120));
r = await call('GET', '/v1/mail/' + victim.addr);
expect('list victim mailbox with no secret', r.status === 401, r.status + ' ' + r.body.slice(0, 120));
r = await call('GET', '/v1/mail/' + victim.addr, { headers: { authorization: 'Mailbox ' } });
expect('list victim mailbox with an empty secret', r.status === 401, r.status);

// 2. Download someone else's part.
r = await call('GET', `/v1/mail/${victim.addr}/${sent.id}/0`, { secret: attacker.secret });
expect('download victim part with attacker secret', r.status === 401, r.status);
r = await call('GET', `/v1/mail/${attacker.addr}/${sent.id}/0`, { secret: attacker.secret });
expect('download victim part through own mailbox path', r.status === 404, r.status + ' ' + r.body.slice(0, 60));

// 3. Delete someone else's message.
await call('DELETE', `/v1/mail/${victim.addr}/${sent.id}`, { secret: attacker.secret });
await call('DELETE', `/v1/mail/${attacker.addr}/${sent.id}`, { secret: attacker.secret });
expect('delete victim message', (await call('GET', '/v1/mail/' + victim.addr, { secret: victim.secret })).body.includes(sent.id), 'message gone');

// 4. Take over the victim's address.
const stolen = crypto.getRandomValues(new Uint8Array(32));
r = await register({ ...attacker, addr: victim.addr });                                   // attacker's own keys
expect('register victim address with attacker keys', r.status >= 400, r.status + ' ' + r.body);
const pub = JSON.parse((await call('GET', '/v1/keys/' + victim.addr)).body);             // public keys are public
r = await call('PUT', '/v1/keys/' + victim.addr, { body: { ecdh: pub.ecdh, sig: pub.sig, secretHash: hex(await sha(stolen)) } });
r = await call('GET', '/v1/mail/' + victim.addr, { secret: stolen });
expect('re-register victim keys without proof, then read', r.status === 401, r.status + ' ' + r.body.slice(0, 120));
r = await call('PUT', '/v1/keys/' + victim.addr, {
  body: { ecdh: pub.ecdh, sig: pub.sig, secretHash: hex(await sha(stolen)), ts: Math.floor(Date.now() / 1000), proof: await sign(attacker, 'register|' + victim.addr + '|' + hex(await sha(stolen)) + '|' + Math.floor(Date.now() / 1000)) },
});
expect('re-register victim keys with a forged proof', (await call('GET', '/v1/mail/' + victim.addr, { secret: stolen })).status === 401, r.status + ' ' + r.body);
check('victim still reads its mailbox', (await call('GET', '/v1/mail/' + victim.addr, { secret: victim.secret })).status === 200, '');

// 5. Impersonate a sender, or tamper with someone else's transfer.
r = await post({ ...attacker, addr: sender.addr }, victim, 1, attacker);
expect('post as the sender with attacker key', r.status === 403, r.status + ' ' + r.body);
r = await call('PUT', `/v1/mail/${victim.addr}/${sent.id}/0`, { body: new Uint8Array([9, 9, 9]), headers: { 'x-upload-token': b64(crypto.getRandomValues(new Uint8Array(24))) } });
expect('overwrite victim part with a guessed upload token', r.status === 403, r.status);
r = await call('PUT', `/v1/mail/${victim.addr}/${sent.id}/0`, { body: new Uint8Array([9, 9, 9]), headers: { 'x-upload-token': sent.uploadToken } });
const part = await call('GET', `/v1/mail/${victim.addr}/${sent.id}/0`, { secret: victim.secret });
expect('overwrite a part already uploaded, even with the token', Buffer.from(part.body, 'latin1').equals(Buffer.from([1, 2, 3, 4])), 'part changed');

// 5b. The sender's resume/cancel routes need that transfer's upload token.
r = await call('GET', `/v1/mail/${victim.addr}/${sent.id}`, { headers: { 'x-upload-token': b64(crypto.getRandomValues(new Uint8Array(24))) } });
expect('read transfer status with a guessed upload token', r.status === 403, r.status + ' ' + r.body.slice(0, 80));
r = await call('DELETE', `/v1/mail/${victim.addr}/${sent.id}`, { headers: { 'x-upload-token': b64(crypto.getRandomValues(new Uint8Array(24))) } });
expect('cancel someone else\'s transfer with a guessed upload token', (await call('GET', '/v1/mail/' + victim.addr, { secret: victim.secret })).body.includes(sent.id), 'transfer gone');
r = await call('GET', `/v1/mail/${victim.addr}/${sent.id}`, { headers: { 'x-upload-token': sent.uploadToken } });
check('the real sender sees which parts arrived', r.status === 200 && JSON.parse(r.body).have.join() === '0', r.status + ' ' + r.body);

// 6. Injection through the path.
r = await call('GET', "/v1/mail/" + encodeURIComponent("' OR 1=1 --"), { secret: attacker.secret });
expect('SQL injection in the address', r.status === 400, r.status);
r = await call('GET', `/v1/mail/${attacker.addr}/${encodeURIComponent("x' OR '1'='1")}/0`, { secret: attacker.secret });
expect('SQL injection in the message id', r.status === 404, r.status);

// Clean up.
await call('DELETE', `/v1/mail/${victim.addr}/${sent.id}`, { secret: victim.secret });
console.log(failures === 0 ? '\nAll attempts refused.' : '\n' + failures + ' problem(s).');
process.exit(failures ? 1 : 0);
