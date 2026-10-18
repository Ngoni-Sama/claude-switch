-- Claude Switch relay database (Cloudflare D1).
CREATE TABLE IF NOT EXISTS mailbox (
  addr        TEXT PRIMARY KEY,      -- 24-char Crockford base32
  ecdh        TEXT NOT NULL,         -- P-256 SPKI, base64url
  sig         TEXT NOT NULL,         -- P-256 SPKI, base64url
  secret_hash TEXT NOT NULL,         -- SHA-256 hex of the mailbox secret
  created     INTEGER NOT NULL,
  seen        INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS message (
  id          TEXT PRIMARY KEY,
  to_addr     TEXT NOT NULL,
  from_addr   TEXT NOT NULL,
  header      TEXT NOT NULL,         -- base64url JSON, signed by the sender
  signature   TEXT NOT NULL,
  size        INTEGER NOT NULL,
  parts       INTEGER NOT NULL,
  received    INTEGER NOT NULL DEFAULT 0,
  upload_hash TEXT NOT NULL,
  created     INTEGER NOT NULL,
  expires     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS message_to ON message (to_addr, expires);
CREATE INDEX IF NOT EXISTS message_from ON message (from_addr, created);

CREATE TABLE IF NOT EXISTS part (
  msg_id      TEXT NOT NULL,
  n           INTEGER NOT NULL,
  data        BLOB NOT NULL,         -- encrypted, at most ~1 MB
  PRIMARY KEY (msg_id, n)
);
