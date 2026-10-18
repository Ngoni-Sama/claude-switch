"""Builds the user manual twice from one source: docs/manual.html (the website)
and MANUAL.md (GitHub). Screenshots come from the app's demo mode:

    ClaudeSwitch --screenshots <dir> --demo [--theme dark] [--size WxH]

then copy the PNGs into docs/assets/screens/. Run: python tools/make_manual.py
"""
import html
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SHOTS = 'assets/screens/'   # relative to docs/

# Blocks: ('p', text) ('steps', [..]) ('ul', [..]) ('tip', text) ('img', file, alt) ('pair', (file, alt), (file, alt))
# Inline: **bold**, `code`, [text](url)
SECTIONS = [
    ('start', 'Get started', [
        ('p', 'Claude Switch runs several Claude Desktop accounts side by side, each in its own window, e.g. **Work** and **Personal**. '
              'It shows every account\'s usage and reset times, and hands a session to another account when one runs out.'),
        ('steps', [
            'Install [Claude Desktop](https://claude.ai/download) if you haven\'t, then install Claude Switch from the [download page](https://ngoni-sama.github.io/claude-switch/#install).',
            'Open **Claude Switch**. Name the account Claude is already signed in to (e.g. *Personal*), then your other accounts (e.g. *Work*).',
            'Each new profile opens its own Claude window. Sign in there with that profile\'s account. You do this once per profile.',
        ]),
        ('img', 'onboarding-light.png', 'First launch: name the account you already use, then add the others'),
        ('tip', 'Every profile needs **its own account**. Two profiles on one account would share one usage limit, so Claude Switch asks you to sign in with a different one.'),
    ]),
    ('overview', 'Overview', [
        ('p', 'The home page. Four tiles along the top sum things up: how many profiles you have (and how many are open), which account has the **most room left**, '
              'how many Claude Code sessions there are, and which Claude version you run.'),
        ('img', 'dashboard-light.png', 'The Overview page: summary tiles, a banner, and one card per profile with session and weekly usage rings'),
        ('p', 'Below are **banners** for anything that needs you (a transfer waiting, an account at its limit, a profile with no account) and **one card per profile**:'),
        ('ul', [
            '**Name and pencil.** Click the pencil (or double-click the name) to rename the profile. Its launchers and terminal command follow; nothing needs signing in again.',
            '**Open / Closed / Not signed in**, the account email and organisation.',
            '**Usage rings.** *Session* and *Weekly*, worded like Claude\'s own panel. **0% means all available, 100% means used up.** Reset times are under each ring.',
            '**Switch to / Open** brings that profile\'s Claude window forward or opens it. A profile with no account shows **Sign in** instead.',
            '**Paste report.** For exact numbers, copy the usage report in that profile\'s Claude window (it starts with *Claude Code usage report*) and press this. Claude Switch also notices it on its own.',
            '**··· (More)**: restart the window, open Claude Code in a terminal as this profile, copy MCP servers from another profile, duplicate, rename, pick a colour, open at start-up, show its folder, or remove it.',
        ]),
    ]),
    ('profiles', 'Add a profile and sign in', [
        ('steps', [
            'On **Overview**, press **+ Add profile** and name it, e.g. *Client A*.',
            'A Claude window opens for it straight away, with a **Sign in to Client A** window beside it.',
            'Sign in in the new Claude window. The sign-in window updates by itself and turns green when it\'s done. Press **Done**.',
        ]),
        ('pair', ('signin-waiting-light.png', 'Sign-in window waiting for the new profile to sign in'),
                 ('signin-same-account-light.png', 'Sign-in window warning that the account is already used by another profile')),
        ('p', 'If you sign in with an account another profile already uses, it says which one and keeps **Done** greyed out. '
              'In that Claude window, click your name, choose **Log out**, and sign in with a different account.'),
        ('p', 'Closing the window before signing in offers to **remove** the new profile (a profile with no account can\'t be used) or keep it for later. '
              'Profiles without an account, and two profiles sharing one, get a banner on Overview with a button to fix it.'),
        ('tip', 'Signing in with an email link or in your browser? The link can open in your main Claude window instead. Press **Turn on sign-in links** first (Windows) so it always reaches the window that asked for it.'),
    ]),
    ('sessions', 'Sessions', [
        ('p', 'Claude Code conversations from each signed-in account, newest first. When an account runs out of usage, pick the session and press '
              '**Continue in Personal →** (or any other profile). The conversation is copied there and the original stays where it is.'),
        ('img', 'page-sessions-light.png', 'The Sessions page with a tab per profile and Continue in buttons'),
    ]),
    ('linked', 'Linked computers', [
        ('p', 'Send profiles to your other computers over the internet, wherever they are: another office, another country. '
              'Transfers are **end-to-end encrypted** to the receiving computer and sign-ins never travel.'),
        ('img', 'page-linked-light.png', 'The Linked computers page: transfers on the left, this computer\'s address and linked computers on the right'),
        ('steps', [
            'Press **Turn on**. This computer gets an address like `CS-7KQ2M-9XD4T-PAW8R-ZH3NC-5VJE`. Press **Copy address**.',
            'On the other computer, press **Link a computer…** and paste the address. The first computer gets a request; press **Accept**. You only link once.',
            'Press **Send profiles…** next to a linked computer and tick the profiles to send.',
            'On the other computer, a banner appears: press **Receive**. It imports straight away; nothing there is overwritten.',
        ]),
        ('p', 'Every transfer has a short **ID** like `#A1B2C3`, shown on both computers, and the **Transfers** card shows it live:'),
        ('ul', [
            '**Speed and time left** while it uploads (e.g. *12.4 of 48 MB at 1.1 MB/s, about 30 s left*). The receiving computer sees the upload progress too.',
            '**Resume.** If the connection drops, the transfer is *Paused*. Press **Resume** and it carries on from the parts already sent. It also resumes when Claude Switch starts.',
            '**No duplicates.** Sending the same profiles again resumes or replaces the copy that\'s waiting, so the other computer only ever gets one.',
            'Closing the send window doesn\'t stop it: press **Hide (keeps sending)**. **Cancel** stops it and deletes it from the relay.',
            'A transfer waits on the relay for up to a week, so the other computer needn\'t be on, and is deleted once collected or declined.',
        ]),
        ('tip', 'Update both computers to the same version of Claude Switch before sending.'),
    ]),
    ('memory', 'Shared memory', [
        ('p', 'Let accounts share one memory and one set of conversations. When one runs out of usage, open another and carry on from exactly where you left off: '
              'same projects, same notes, same chats. Sign-ins stay separate.'),
        ('img', 'page-memory-light.png', 'The Shared memory page showing shared projects, memory notes and conversations'),
        ('p', 'Press **Turn on sharing**, pick the profiles, and close their Claude windows while it sets up. **Turn off** gives each profile its own copy back; nothing is deleted.'),
    ]),
    ('updates', 'Updates', [
        ('p', 'Shows every installed copy of Claude and its version, and spots the classic trap: a second, forgotten copy that stopped updating. '
              '**Restart all profiles** moves windows onto the current copy; **Check for a newer Claude** opens the updater. '
              'Choose **Tell me**, **Automatic** (opens profiles that are behind so Claude\'s own updater runs) or **Off**.'),
        ('img', 'page-updates-light.png', 'The Updates page with the installed Claude version'),
    ]),
    ('backup', 'Backup & move', [
        ('p', '**Export…** saves profiles (sessions, memory, settings) to one password-protected file; **Import…** brings them back on any computer, e.g. after a crash. '
              'Sign-ins are never exported. Or send them straight to a linked computer.'),
        ('img', 'page-backup-light.png', 'The Backup and move page with Export, Import and Linked computers'),
    ]),
    ('terminal', 'Terminal', [
        ('p', 'Run Claude Code as any profile. **Turn on terminal commands** to type `claude-work`, `claude-personal`, … in any terminal, '
              'and **Add a folder rule** so plain `claude` picks the right profile inside a project. Or use **··· > Open Claude Code in a terminal** on a profile card.'),
        ('img', 'page-terminal-light.png', 'The Terminal page'),
    ]),
    ('settings', 'Settings', [
        ('ul', [
            '**Keyboard shortcuts** (Windows): Ctrl+Alt+1 to 9 open a profile; Ctrl+Alt+0 opens Claude Switch.',
            '**Reopen profiles when a Claude update closes them.**',
            '**Start Claude Switch when I sign in**, and offer to reopen the profiles you had open.',
            '**Claude Switch updates**: Tell me, Automatic or Off. Updates are signed and only installed if the signature and checksum match.',
            '**Diagnose…** writes one report: where each profile\'s data lives, which Claude each window runs, sign-in state and launchers, with a fix for anything wrong. Emails are masked so you can paste it into an issue.',
        ]),
        ('img', 'page-settings-light.png', 'The Settings page'),
    ]),
    ('screens', 'Small screens, light and dark', [
        ('p', 'On a laptop, or when the window isn\'t maximised, everything gets tighter. Narrower still, the sidebar shrinks to icons; hover one to see its name. '
              'Switch between **Light** and **Dark** at the top right of any page.'),
        ('pair', ('compact-light.png', 'Compact layout on a small laptop window'),
                 ('narrow-dark.png', 'Narrow window in dark mode with an icon-only sidebar')),
    ]),
    ('help', 'When something looks wrong', [
        ('ul', [
            '**"X and Y have swapped accounts".** A sign-in link opened in the wrong window. Press **Swap names**; nobody signs in again.',
            '**"X has no account yet".** Press **Sign in to X**, or **Remove** it if you don\'t need it.',
            '**"X and Y use the same account".** Press **Sign Y in again** and use a different account.',
            '**A transfer says Paused.** Check your connection and press **Resume** on the Linked computers page.',
            '**Usage looks old.** Copy the usage report in that Claude window and press **Paste report**.',
            '**Anything else.** Settings > **Diagnose…**, then [report an issue](https://github.com/Ngoni-Sama/claude-switch/issues) with the report.',
        ]),
        ('p', 'For scripts: `ClaudeSwitch --list`, `--open Work`, `--diagnose`, `--check-update`, `--version`.'),
    ]),
]


def inline_md(t):
    return t


def inline_html(t):
    t = html.escape(t, quote=False)
    t = re.sub(r'\[([^\]]+)\]\(([^)]+)\)', r'<a href="\2">\1</a>', t)
    t = re.sub(r'\*\*(.+?)\*\*', r'<b>\1</b>', t)
    t = re.sub(r'`(.+?)`', r'<code>\1</code>', t)
    t = re.sub(r'(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])', r'<i>\1</i>', t)
    return t


def build_md():
    out = ['# Claude Switch manual', '',
           'How to use every part of Claude Switch. Also on the [website](https://ngoni-sama.github.io/claude-switch/manual.html).', '']
    out += ['- [%s](#%s)' % (title, re.sub(r'[^a-z0-9 -]', '', title.lower()).replace(' ', '-')) for _, title, _ in SECTIONS]
    out.append('')
    for _, title, blocks in SECTIONS:
        out += ['## ' + title, '']
        for b in blocks:
            kind = b[0]
            if kind == 'p':
                out += [b[1], '']
            elif kind == 'tip':
                out += ['> **Tip.** ' + b[1], '']
            elif kind == 'ul':
                out += ['- ' + x for x in b[1]] + ['']
            elif kind == 'steps':
                out += ['%d. %s' % (i + 1, x) for i, x in enumerate(b[1])] + ['']
            elif kind == 'img':
                out += ['<p align="center"><img src="docs/%s%s" width="820" alt="%s"></p>' % (SHOTS, b[1], html.escape(b[2])), '']
            elif kind == 'pair':
                cells = ''.join('<td width="50%%"><img src="docs/%s%s" alt="%s"></td>' % (SHOTS, f, html.escape(a)) for f, a in b[1:])
                out += ['<table><tr>%s</tr></table>' % cells, '']
    out.append('---')
    out.append('Screenshots use made-up demo data. Claude Switch is unofficial and not affiliated with Anthropic.')
    return '\n'.join(out) + '\n'


PAGE = '''<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Claude Switch manual</title>
<meta name="description" content="How to use Claude Switch: profiles and sign-in, usage, sessions, linked computers, shared memory, backups, terminal and settings.">
<meta property="og:title" content="Claude Switch manual">
<meta property="og:image" content="https://ngoni-sama.github.io/claude-switch/assets/screens/dashboard-light.png">
<link rel="icon" type="image/png" href="assets/favicon.png">
<style>
  /* The app's own look: violet sidebar whose current item is an open pill. */
  :root {
    --bg: #efedf6; --card: #ffffff; --inner: #f4f3f9; --border: #e4e1ef; --text: #1e1b2e; --muted: #5f5a72;
    --accent: #5240cf; --accent2: #9a55e8; --soft: #ece8fb; --side-a: #5a3fc0; --side-b: #2e1f6e; --side-text: #ffffff;
    color-scheme: light;
  }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --bg: #17151f; --card: #221f2d; --inner: #2d2a3a; --border: #363246; --text: #f3f1fa; --muted: #a7a2b8;
      --accent: #8f80ff; --accent2: #c48bff; --soft: #2e2752; --side-a: #2c2157; --side-b: #120d26; --side-text: #f2efff;
      color-scheme: dark;
    }
  }
  :root[data-theme="dark"] {
    --bg: #17151f; --card: #221f2d; --inner: #2d2a3a; --border: #363246; --text: #f3f1fa; --muted: #a7a2b8;
    --accent: #8f80ff; --accent2: #c48bff; --soft: #2e2752; --side-a: #2c2157; --side-b: #120d26; --side-text: #f2efff;
    color-scheme: dark;
  }
  * { box-sizing: border-box; }
  html { scroll-behavior: smooth; scroll-padding-top: 16px; }
  @media (prefers-reduced-motion: reduce) { html { scroll-behavior: auto; } }
  body { margin: 0; background: var(--bg); color: var(--text);
    font: 15.5px/1.6 "Segoe UI Variable Text", "Segoe UI", Inter, system-ui, -apple-system, "Helvetica Neue", Arial, sans-serif;
    -webkit-font-smoothing: antialiased; }
  a { color: var(--accent); }
  img { max-width: 100%; display: block; }
  code { font: 13.5px/1 ui-monospace, "Cascadia Code", Consolas, monospace; background: var(--soft); padding: 2px 6px; border-radius: 5px; }

  .layout { display: grid; grid-template-columns: 250px minmax(0, 1fr); min-height: 100vh; }
  .side { position: sticky; top: 0; height: 100vh; overflow-y: auto; padding: 22px 0 22px 16px;
    background: linear-gradient(180deg, var(--side-a), var(--side-b)); color: var(--side-text); }
  .brand { display: flex; align-items: center; gap: 10px; color: inherit; text-decoration: none; font-weight: 700; margin: 0 16px 22px 6px; }
  .brand img { width: 34px; height: 34px; border-radius: 9px; background: #fff; padding: 2px; }
  .brand small { display: block; font-weight: 400; opacity: .72; font-size: 12px; }
  .toc { list-style: none; margin: 0; padding: 0; }
  .toc a { position: relative; display: block; padding: 9px 14px; margin-right: 16px; border-radius: 12px;
    color: inherit; text-decoration: none; font-size: 14px; opacity: .9; }
  .toc a:hover { background: rgba(255,255,255,.12); opacity: 1; }
  /* The open pill: runs to the edge in the page colour, curving into it above and below. */
  .toc a.on { background: var(--bg); color: var(--accent); font-weight: 600; opacity: 1; margin-right: 0; border-radius: 22px 0 0 22px; }
  .toc a.on::before, .toc a.on::after { content: ""; position: absolute; right: 0; width: 20px; height: 20px; pointer-events: none;
    background: radial-gradient(circle at 0 0, transparent 19.5px, var(--bg) 20px); }
  .toc a.on::before { top: -20px; }
  .toc a.on::after { bottom: -20px; background: radial-gradient(circle at 0 100%, transparent 19.5px, var(--bg) 20px); }
  .side .links { margin: 24px 16px 0 8px; font-size: 13px; opacity: .8; }
  .side .links a { color: inherit; }

  .top { position: sticky; top: 0; z-index: 2; background: var(--card); box-shadow: 0 1px 0 rgba(0,0,0,.06);
    display: flex; align-items: center; gap: 12px; padding: 14px 36px; }
  .top h1 { font-size: 20px; margin: 0 auto 0 0; letter-spacing: -.3px; }
  .btn { display: inline-flex; align-items: center; min-height: 40px; padding: 0 16px; border-radius: 10px; border: 0;
    background: var(--inner); color: var(--text); font: inherit; font-size: 14px; font-weight: 600; text-decoration: none; cursor: pointer; }
  .btn.primary { background: linear-gradient(90deg, var(--accent), var(--accent2)); color: #fff; }
  .btn:focus-visible, .toc a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }

  main { padding: 26px 36px 60px; max-width: 1080px; }
  .lede { color: var(--muted); margin: 0 0 22px; }
  section { background: var(--card); border: 1px solid var(--border); border-radius: 16px; padding: 24px 28px; margin-bottom: 18px;
    box-shadow: 0 6px 18px rgba(40, 20, 90, .06); }
  section h2 { margin: 0 0 10px; font-size: 21px; letter-spacing: -.3px; }
  section p { margin: 0 0 12px; }
  ol, ul { margin: 0 0 14px; padding-left: 22px; }
  li { margin: 4px 0; }
  ol li::marker { color: var(--accent); font-weight: 700; }
  .tip { background: var(--soft); border-radius: 12px; padding: 12px 16px; margin: 6px 0 12px; }
  figure { margin: 8px 0 16px; }
  figure img { border-radius: 12px; border: 1px solid var(--border); box-shadow: 0 10px 30px rgba(40, 20, 90, .12); }
  figcaption { font-size: 13px; color: var(--muted); margin-top: 8px; text-align: center; }
  .pair { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; align-items: start; }
  footer { color: var(--muted); font-size: 13px; padding: 0 36px 30px; }

  @media (max-width: 860px) {
    .layout { grid-template-columns: 1fr; }
    .side { position: static; height: auto; padding: 16px; }
    .toc { display: flex; flex-wrap: wrap; gap: 4px; }
    .toc a, .toc a.on { margin: 0; border-radius: 10px; padding: 7px 12px; }
    .toc a.on::before, .toc a.on::after { display: none; }
    .side .links { display: none; }
    .top { padding: 12px 16px; }
    main { padding: 18px 16px 40px; }
    section { padding: 18px 16px; }
    .pair { grid-template-columns: 1fr; }
    footer { padding: 0 16px 24px; }
  }
</style>
</head>
<body>
<div class="layout">
  <nav class="side" aria-label="Manual">
    <a class="brand" href="index.html"><img src="assets/icon-512.png" alt=""><span>Claude Switch<small>Manual</small></span></a>
    <ul class="toc">
{{TOC}}
    </ul>
    <p class="links"><a href="index.html">Website</a> &middot; <a href="https://github.com/Ngoni-Sama/claude-switch">Source</a></p>
  </nav>
  <div>
    <header class="top">
      <h1>Manual</h1>
      <button class="btn" id="theme" type="button">Dark</button>
      <a class="btn primary" href="index.html#install">Download</a>
    </header>
    <main>
      <p class="lede">How to use every part of Claude Switch. Screenshots use made-up demo data.</p>
{{BODY}}
    </main>
    <footer>Claude Switch &middot; MIT License &middot; &copy; 2026 <a href="https://github.com/Ngoni-Sama">Ngoni Maphosa</a>. "Claude" is a trademark of Anthropic, PBC; Claude Switch is not affiliated with or endorsed by Anthropic.</footer>
  </div>
</div>
<script>
  // Light/dark: follows the system until you pick one.
  const root = document.documentElement, btn = document.getElementById('theme');
  const dark = () => root.dataset.theme ? root.dataset.theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
  const swap = on => document.querySelectorAll('img[data-dark]').forEach(i => { i.src = on ? i.dataset.dark : i.dataset.light; });
  const paint = () => { btn.textContent = dark() ? 'Light' : 'Dark'; swap(dark()); };
  try { const t = localStorage.getItem('cs-theme'); if (t) root.dataset.theme = t; } catch (e) {}
  btn.addEventListener('click', () => { root.dataset.theme = dark() ? 'light' : 'dark'; try { localStorage.setItem('cs-theme', root.dataset.theme); } catch (e) {} paint(); });
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', paint);
  paint();

  // The open pill follows the section you're reading.
  const links = [...document.querySelectorAll('.toc a')];
  const mark = id => links.forEach(a => a.classList.toggle('on', a.hash === '#' + id));
  const seen = new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) mark(e.target.id); }), { rootMargin: '-20% 0px -70% 0px' });
  document.querySelectorAll('main section').forEach(s => seen.observe(s));
</script>
</body>
</html>
'''


def img_html(f, alt):
    dark = f.replace('-light.png', '-dark.png')
    has_dark = dark != f and (ROOT / 'docs' / SHOTS / dark).exists()
    attrs = ' data-light="%s%s" data-dark="%s%s"' % (SHOTS, f, SHOTS, dark) if has_dark else ''
    return '<figure><img src="%s%s"%s alt="%s" loading="lazy"><figcaption>%s</figcaption></figure>' % (
        SHOTS, f, attrs, html.escape(alt), html.escape(alt))


def build_html():
    toc, body = [], []
    for i, (sid, title, blocks) in enumerate(SECTIONS):
        toc.append('      <li><a href="#%s"%s>%s</a></li>' % (sid, ' class="on"' if i == 0 else '', html.escape(title)))
        parts = ['      <section id="%s">' % sid, '        <h2>%s</h2>' % html.escape(title)]
        for b in blocks:
            kind = b[0]
            if kind == 'p':
                parts.append('        <p>%s</p>' % inline_html(b[1]))
            elif kind == 'tip':
                parts.append('        <p class="tip"><b>Tip.</b> %s</p>' % inline_html(b[1]))
            elif kind in ('ul', 'steps'):
                tag = 'ol' if kind == 'steps' else 'ul'
                parts.append('        <%s>%s</%s>' % (tag, ''.join('<li>%s</li>' % inline_html(x) for x in b[1]), tag))
            elif kind == 'img':
                parts.append('        ' + img_html(b[1], b[2]))
            elif kind == 'pair':
                parts.append('        <div class="pair">%s</div>' % ''.join(img_html(f, a) for f, a in b[1:]))
        parts.append('      </section>')
        body.append('\n'.join(parts))
    return PAGE.replace('{{TOC}}', '\n'.join(toc)).replace('{{BODY}}', '\n'.join(body))


def main():
    missing = [f for _, _, blocks in SECTIONS for b in blocks
               for f in ([b[1]] if b[0] == 'img' else [x[0] for x in b[1:]] if b[0] == 'pair' else [])
               if not (ROOT / 'docs' / SHOTS / f).exists()]
    if missing:
        raise SystemExit('missing screenshots: ' + ', '.join(missing))
    (ROOT / 'docs' / 'manual.html').write_text(build_html(), encoding='utf-8', newline='\n')
    (ROOT / 'MANUAL.md').write_text(build_md(), encoding='utf-8', newline='\n')
    print('wrote docs/manual.html and MANUAL.md')


if __name__ == '__main__':
    main()
