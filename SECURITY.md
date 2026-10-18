# Security

Claude Switch works next to your Claude sign-ins, so security reports are taken seriously.

**Please don't open a public issue for a vulnerability.** Report it privately through
[GitHub's private vulnerability reporting](https://github.com/Ngoni-Sama/claude-switch/security/advisories/new).
You'll get a reply within a few days.

## What Claude Switch does and doesn't do

- It **never** reads, copies, exports or sends sign-in tokens or credentials.
- **Linked computers** (off unless you turn it on) talks to a relay: it registers this computer's public keys, checks for transfers every few minutes, and uploads transfers you send. Every transfer is encrypted on the sending computer to the receiving computer's key (P-256 ECDH, HKDF-SHA256, AES-256-CBC + HMAC-SHA256 per part, signed headers), so the relay only stores ciphertext, deleted once collected or after 7 days. Addresses are hashes of a computer's public keys, so neither the relay nor anyone else can swap them. Sign-ins are never included. The relay code is in `relay/`.
- Otherwise its **only network request** is a daily check of this repository's latest release on GitHub (Settings > Claude Switch updates > Off stops it). Updates are installed only when the release's checksum file carries a valid signature from the project's release key and the download matches its checksum.
- It **never reads** the token store of other tools it shows status for (the billing relay's `tokens.json`); it reads only that tool's status file, which holds no tokens.
- Clipboard: it checks copied text only for Claude's *usage report* header. Anything else is compared
  in memory to spot changes and is never stored or sent.
- Exports can be encrypted with AES-256-CBC + HMAC-SHA256, using a key derived with PBKDF2-SHA256
  (200,000 iterations). Exports never contain sign-ins.
- File operations copy and never overwrite. Deleting a profile's data only ever removes folders
  Claude Switch created (`Claude-<Name>`, `.claude-<name>`), and only after you confirm.
