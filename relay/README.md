# Claude Switch relay

The mailbox behind **Linked computers**. Computers link by address, and a
transfer waits here until the receiving computer collects it (up to 7 days).

The relay can't read anything. Each transfer is encrypted on the sending
computer to the receiving computer's public key, and only that computer's
private key, which never leaves it, can decrypt it. Addresses are hashes of a
computer's public keys, so the relay can't swap keys either; the app checks.
What the relay does see: which address sent how many bytes to which address,
and when.

| | |
|---|---|
| Runs on | Cloudflare Workers + D1 (free plan is enough), or Node 22+ |
| Stores | public keys per address; encrypted transfers in 1 MB parts |
| Limits | 1 GB per transfer, 20 waiting per computer, 60 sent per day |
| Clean-up | collected transfers are deleted at once; the rest after 7 days |

## Deploy on Cloudflare

```bash
cd relay
npx wrangler login
npx wrangler d1 create claude-switch-relay        # copy the database_id into wrangler.toml
npx wrangler d1 execute claude-switch-relay --remote --file schema.sql
npx wrangler deploy
```

Then, in Claude Switch on every computer you link: **Linked computers > More… >
Use another relay**, and enter your relay's address (for example
`https://claude-switch-relay.<you>.workers.dev`). All linked computers must use
the same relay.

## Run it anywhere else

```bash
node relay/dev-server.mjs 8787 relay.db
```

That serves the same API from your own machine or server with SQLite (put it
behind HTTPS before using it across the internet). The app's end-to-end tests
run against this.

## API

See the header of [`src/worker.js`](src/worker.js).
