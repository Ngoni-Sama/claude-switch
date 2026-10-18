// Run the relay locally on Node 22+ (no Cloudflare account needed), for
// development, tests, or a relay on your own machine or VPS:
//   node relay/dev-server.mjs [port] [database file]
// It wraps src/worker.js with a small D1 stand-in on node:sqlite.
// POST /__scheduled runs the daily clean-up.
import { createServer } from 'node:http';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import worker from './src/worker.js';

const port = Number(process.argv[2] || 8787);
const db = new DatabaseSync(process.argv[3] || ':memory:');
db.exec(readFileSync(new URL('./schema.sql', import.meta.url), 'utf8'));

// Just the parts of the D1 API the worker uses.
class Statement {
  constructor(sql, args = []) { this.sql = sql; this.args = args; }
  bind(...args) { return new Statement(this.sql, args.map(a => (a instanceof ArrayBuffer ? new Uint8Array(a) : a))); }
  first() { return Promise.resolve(db.prepare(this.sql).get(...this.args) ?? null); }
  all() { return Promise.resolve({ results: db.prepare(this.sql).all(...this.args) }); }
  run() { const r = db.prepare(this.sql).run(...this.args); return Promise.resolve({ meta: { changes: Number(r.changes) } }); }
}
const env = {
  DB: {
    prepare: sql => new Statement(sql),
    // Synchronous inside the transaction, so parallel requests never interleave in it.
    batch: async stmts => {
      db.exec('BEGIN');
      try {
        const out = stmts.map(s => { const r = db.prepare(s.sql).run(...s.args); return { meta: { changes: Number(r.changes) } }; });
        db.exec('COMMIT');
        return out;
      } catch (e) { db.exec('ROLLBACK'); throw e; }
    },
  },
};

// A client that drops mid-request (a cancelled upload) must never take the server down.
createServer((req, res) => handle(req, res).catch(e => {
  console.error(e);
  if (!res.headersSent) res.writeHead(500);
  res.end();
})).listen(port, () => console.log('claude-switch relay on http://localhost:' + port));

async function handle(req, res) {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const body = Buffer.concat(chunks);
  if (req.url === '/__scheduled' && req.method === 'POST') {
    await worker.scheduled({}, env);
    res.writeHead(200).end('ok');
    return;
  }
  const request = new Request('http://localhost' + req.url, {
    method: req.method, headers: req.headers, body: ['GET', 'HEAD'].includes(req.method) ? undefined : body,
  });
  const response = await worker.fetch(request, env);
  res.writeHead(response.status, Object.fromEntries(response.headers));
  res.end(Buffer.from(await response.arrayBuffer()));
}
