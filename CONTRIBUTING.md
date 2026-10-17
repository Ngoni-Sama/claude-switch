# Contributing to Claude Switch

Thanks for helping! Bug reports, fixes, features, docs and packaging improvements are all welcome.

## How changes get in

- `main` is protected. Nobody pushes to it directly.
- Work on a branch in **your fork** (`feature/short-name`, `fix/short-name`), then open a **pull request** to `main`.
- CI builds and tests on Windows, macOS and Linux. It must pass.
- The maintainer ([@Ngoni-Sama](https://github.com/Ngoni-Sama)) reviews and merges. Small, focused PRs get merged fastest.
- For bigger changes, open an issue first so we can agree on the approach.

## Getting started

```bash
git clone https://github.com/<you>/claude-switch.git
cd claude-switch
dotnet run --project src/ClaudeSwitch      # .NET 10 SDK
dotnet test tests/ClaudeSwitch.Tests
```

Useful flags while developing:

```text
ClaudeSwitch --screenshots <dir> [--theme dark] [--size 1440x900] [--demo]   render the dashboard + first-run screen to PNG
ClaudeSwitch --launch <Name>                      open a profile the way its launcher does
ClaudeSwitch --demo                               made-up profiles and sessions, nothing real
```

Public screenshots and videos must come from `--demo`, so no real email or project
name ever lands in the repo.

## Where things are

```
src/ClaudeSwitch/
  Core/          everything that isn't UI; no Avalonia references
    Platform.cs    paths per OS (the one place that knows where Claude lives)
    ClaudeApp.cs   finding Claude and its running windows (WMI on Windows, ps elsewhere)
    Launcher.cs    open / focus / restart a profile, one window per data folder
    Accounts.cs    account, email, usage log, Code sessions (read-only)
    Limits.cs      session/weekly buckets with reset times
    UsageReport.cs parsing Claude's copyable usage report
    Transfer.cs    hand-off, history recovery, swap names
    Backup.cs      export/import (.claudeprofiles, optional AES-256)
    Shortcuts.cs   per-OS launchers (.lnk / .app / .desktop)
  Ui/            Avalonia UI built in code; Neu.cs is the design kit
tests/           xUnit tests for Core
packaging/       installers: windows (Inno Setup), macos (.app + .dmg), linux (.deb + .tar.gz)
support/         Cloudflare Worker behind "Support via Pesepay"
docs/            website (GitHub Pages) and screenshots
scripts/windows/ the original no-install PowerShell launcher
```

## Ground rules

- **Never touch sign-ins.** Don't read, copy, export or send tokens or credentials, ever.
- **Copy, don't move.** Anything that changes Claude's folders must copy, and never overwrite.
- **Cross-platform first.** Path or process logic goes in `Platform.cs` / `ClaudeApp.cs`, behind an OS check.
- **Accessible UI.** Text must keep at least 4.5:1 contrast in both themes. Every control needs a name for screen readers. State is never shown by colour or shadow alone.
- Match the surrounding code style. Keep comments to the *why*.

## Releasing (maintainer)

Bump `<Version>` in `src/ClaudeSwitch/ClaudeSwitch.csproj`, then push a tag:

```bash
git tag v2.0.1 && git push origin v2.0.1
```

The *Release* workflow builds every installer and publishes a GitHub Release.

By contributing you agree your work is released under the [MIT License](LICENSE).
