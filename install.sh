#!/usr/bin/env bash
# Claude Switch installer for macOS and Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/Ngoni-Sama/claude-switch/main/install.sh | bash
#
# macOS: installs "Claude Switch.app" into /Applications (or ~/Applications).
# Linux (Ubuntu/Debian): installs the .deb with apt (asks for your password);
#        elsewhere: unpacks into ~/.local/share/claude-switch with a launcher.
set -euo pipefail

REPO="Ngoni-Sama/claude-switch"
say() { printf '\033[1;33m==>\033[0m %s\n' "$*"; }
die() { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

case "$(uname -m)" in
  x86_64|amd64) ARCH=x64; DEBARCH=amd64 ;;
  arm64|aarch64) ARCH=arm64; DEBARCH=arm64 ;;
  *) die "Unsupported CPU: $(uname -m)" ;;
esac

say "Finding the latest release..."
API="https://api.github.com/repos/$REPO/releases/latest"
JSON="$(curl -fsSL "$API")" || die "Couldn't reach GitHub."
asset() { printf '%s' "$JSON" | grep -o "\"browser_download_url\": *\"[^\"]*$1\"" | head -n1 | sed 's/.*"\(https[^"]*\)"/\1/'; }
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

if [ "$(uname -s)" = "Darwin" ]; then
  URL="$(asset "macos-$ARCH.dmg")"; [ -n "$URL" ] || die "No macOS $ARCH build in the latest release."
  say "Downloading $(basename "$URL")..."
  curl -fL --progress-bar -o "$TMP/cs.dmg" "$URL"
  MNT="$TMP/mnt"; mkdir -p "$MNT"
  hdiutil attach -nobrowse -quiet -mountpoint "$MNT" "$TMP/cs.dmg"
  DEST=/Applications; [ -w "$DEST" ] || DEST="$HOME/Applications"; mkdir -p "$DEST"
  rm -rf "$DEST/Claude Switch.app"
  cp -R "$MNT/Claude Switch.app" "$DEST/"
  hdiutil detach -quiet "$MNT"
  say "Installed to $DEST/Claude Switch.app"
  open "$DEST/Claude Switch.app"
  exit 0
fi

if command -v apt-get >/dev/null 2>&1; then
  URL="$(asset "_$DEBARCH.deb")"; [ -n "$URL" ] || die "No .deb for $DEBARCH in the latest release."
  say "Downloading $(basename "$URL")..."
  curl -fL --progress-bar -o "$TMP/claude-switch.deb" "$URL"
  say "Installing (apt may ask for your password)..."
  sudo apt-get install -y "$TMP/claude-switch.deb"
  say "Installed. Open \"Claude Switch\" from your apps, or run: claude-switch"
  exit 0
fi

URL="$(asset "linux-$ARCH.tar.gz")"; [ -n "$URL" ] || die "No Linux $ARCH build in the latest release."
say "Downloading $(basename "$URL")..."
curl -fL --progress-bar -o "$TMP/cs.tgz" "$URL"
DEST="$HOME/.local/share/claude-switch"; rm -rf "$DEST"; mkdir -p "$DEST" "$HOME/.local/bin" "$HOME/.local/share/applications"
tar -xzf "$TMP/cs.tgz" -C "$DEST" --strip-components=1
ln -sf "$DEST/ClaudeSwitch" "$HOME/.local/bin/claude-switch"
sed -e "s|^Exec=.*|Exec=$DEST/ClaudeSwitch|" -e "s|^Icon=.*|Icon=$DEST/claude-switch.png|" "$DEST/claude-switch.desktop" > "$HOME/.local/share/applications/claude-switch.desktop"
say "Installed to $DEST. Run: claude-switch  (make sure ~/.local/bin is on your PATH)"
