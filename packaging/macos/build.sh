#!/usr/bin/env bash
# Builds the macOS release: Claude Switch.app inside a drag-to-Applications .dmg,
# one per architecture (Apple silicon: arm64, Intel: x64).
#
#   packaging/macos/build.sh 2.0.0 arm64
#
# Needs: .NET 10 SDK, macOS (hdiutil, codesign). The app is ad-hoc signed, which
# Apple silicon requires to run at all. It isn't notarized, so on first launch
# macOS asks to confirm: right-click the app > Open (once).
set -euo pipefail

VERSION="${1:?version}"
ARCH="${2:-arm64}"                       # arm64 | x64
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ART="$ROOT/artifacts"
PUB="$ART/osx-$ARCH"
APP="$ART/mac-$ARCH/Claude Switch.app"

dotnet publish "$ROOT/src/ClaudeSwitch/ClaudeSwitch.csproj" -c Release -r "osx-$ARCH" --self-contained \
  -o "$PUB" -p:Version="$VERSION" -p:UseAppHost=true

rm -rf "$ART/mac-$ARCH"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PUB/ClaudeSwitch" "$APP/Contents/MacOS/ClaudeSwitch"
# Native libraries that a single-file publish may leave beside the binary.
find "$PUB" -maxdepth 1 -name '*.dylib' -exec cp {} "$APP/Contents/MacOS/" \;
cp "$ROOT/packaging/macos/icon.icns" "$APP/Contents/Resources/icon.icns"
sed -e "s/@VERSION@/$VERSION/g" "$ROOT/packaging/macos/Info.plist" > "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/ClaudeSwitch"

codesign --force --deep --sign - "$APP"

DMG="$ART/ClaudeSwitch-$VERSION-macos-$ARCH.dmg"
STAGE="$ART/dmg-$ARCH"
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "Claude Switch" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
echo "Built $DMG"
