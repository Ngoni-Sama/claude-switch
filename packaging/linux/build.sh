#!/usr/bin/env bash
# Builds the Linux release: a .deb (Ubuntu/Debian, like Claude Desktop itself)
# and a .tar.gz, per architecture.
#
#   packaging/linux/build.sh 2.0.0 x64      # or arm64
#
# Needs: .NET 10 SDK, dpkg-deb.
set -euo pipefail

VERSION="${1:?version}"
ARCH="${2:-x64}"                          # x64 | arm64
DEBARCH=$([ "$ARCH" = "arm64" ] && echo arm64 || echo amd64)
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ART="$ROOT/artifacts"
PUB="$ART/linux-$ARCH"

dotnet publish "$ROOT/src/ClaudeSwitch/ClaudeSwitch.csproj" -c Release -r "linux-$ARCH" --self-contained \
  -o "$PUB" -p:Version="$VERSION"

# --- .deb ---------------------------------------------------------------------
PKG="$ART/deb-$ARCH"
rm -rf "$PKG"
mkdir -p "$PKG/DEBIAN" "$PKG/opt/claude-switch" "$PKG/usr/bin" \
         "$PKG/usr/share/applications" "$PKG/usr/share/icons/hicolor/512x512/apps"
cp -a "$PUB/." "$PKG/opt/claude-switch/"
chmod 755 "$PKG/opt/claude-switch/ClaudeSwitch"
ln -s /opt/claude-switch/ClaudeSwitch "$PKG/usr/bin/claude-switch"
cp "$ROOT/packaging/linux/claude-switch.desktop" "$PKG/usr/share/applications/"
cp "$ROOT/packaging/linux/claude-switch.png" "$PKG/usr/share/icons/hicolor/512x512/apps/claude-switch.png"
SIZE=$(du -sk "$PKG" | cut -f1)
cat > "$PKG/DEBIAN/control" <<EOF
Package: claude-switch
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEBARCH
Installed-Size: $SIZE
Depends: libc6, libfontconfig1, libice6, libsm6, libx11-6
Recommends: libnotify-bin
Maintainer: Ngoni Maphosa <60420709+Ngoni-Sama@users.noreply.github.com>
Homepage: https://github.com/Ngoni-Sama/claude-switch
Description: Run several Claude Desktop accounts side by side
 Claude Switch opens Claude Desktop with a separate data folder per profile,
 shows each account's usage and reset times, and can hand a Claude Code session
 to another account when one runs out. Unofficial; not affiliated with Anthropic.
EOF
dpkg-deb --build --root-owner-group "$PKG" "$ART/claude-switch_${VERSION}_${DEBARCH}.deb"

# --- tar.gz ------------------------------------------------------------------------
TARDIR="$ART/tar-$ARCH/claude-switch-$VERSION"
rm -rf "$ART/tar-$ARCH"
mkdir -p "$TARDIR"
cp -a "$PUB/." "$TARDIR/"
cp "$ROOT/packaging/linux/claude-switch.png" "$ROOT/packaging/linux/claude-switch.desktop" "$TARDIR/"
tar -C "$ART/tar-$ARCH" -czf "$ART/ClaudeSwitch-$VERSION-linux-$ARCH.tar.gz" "claude-switch-$VERSION"
echo "Built $ART/claude-switch_${VERSION}_${DEBARCH}.deb and ClaudeSwitch-$VERSION-linux-$ARCH.tar.gz"
