#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -gt 1 ]]; then
  echo "Usage: publish-macos.sh [osx-arm64|osx-x64]" >&2
  exit 1
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS packaging requires hdiutil and must be run on macOS." >&2
  exit 1
fi

for REQUIRED_TOOL in python3 dotnet git hdiutil; do
  if ! command -v "$REQUIRED_TOOL" >/dev/null 2>&1; then
    echo "macOS packaging requires $REQUIRED_TOOL; no output has been created." >&2
    exit 1
  fi
done

RID="${1-osx-arm64}"
CONFIGURATION="${CONFIGURATION-Release}"
APP_NAME="${APP_NAME-SZU Badminton Draw}"
BUNDLE_ID="${BUNDLE_ID-com.szuba.badmintondraw}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
PROJECT_PATH="$ROOT_DIR/src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj"
ICON_SOURCE="$ROOT_DIR/src/BadmintonDraw.Desktop/Assets/szuba-app-icon.png"
ICON_FILE=""
if command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1 && [[ -f "$ICON_SOURCE" ]]; then
  ICON_FILE="AppIcon"
fi

# Public layout: artifacts/macos/<RID>/<version>/run-<unique>/{dmg-root/*.app,*.dmg}.
# The helper validates all input, captures Git identity, rejects symlink parents,
# and atomically claims a new directory. Earlier runs are never removed.
PREPARED="$(python3 "$ROOT_DIR/scripts/packaging_metadata.py" prepare-macos \
  "$ROOT_DIR" "$RID" "$CONFIGURATION" "$APP_NAME" "$BUNDLE_ID" \
  "${VERSION+x}" "${VERSION-}" "$ICON_FILE")"
{
  IFS= read -r VERSION
  IFS= read -r OUTPUT_ROOT
} <<< "$PREPARED"
trap 'echo "Packaging failed; retained partial output (not a verified release): $OUTPUT_ROOT" >&2' ERR

PUBLISH_DIR="$OUTPUT_ROOT/publish"
DMG_ROOT="$OUTPUT_ROOT/dmg-root"
APP_PATH="$DMG_ROOT/$APP_NAME.app"
MACOS_DIR="$APP_PATH/Contents/MacOS"
RESOURCES_DIR="$APP_PATH/Contents/Resources"
EXECUTABLE_PATH="$MACOS_DIR/BadmintonDraw.Desktop"
DMG_PATH="$OUTPUT_ROOT/SZU-Badminton-Draw_${VERSION}_${RID}.dmg"

mkdir -p "$PUBLISH_DIR" "$MACOS_DIR" "$RESOURCES_DIR"

dotnet restore "$PROJECT_PATH" \
  --locked-mode \
  -p:Configuration="$CONFIGURATION"

dotnet publish "$PROJECT_PATH" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  --no-restore \
  "-p:Version=$VERSION" \
  "-p:VersionPrefix=$VERSION" \
  -o "$PUBLISH_DIR"

cp -R "$PUBLISH_DIR"/. "$MACOS_DIR"/
chmod +x "$EXECUTABLE_PATH"

if [[ -n "$ICON_FILE" ]]; then
  ICONSET="$OUTPUT_ROOT/AppIcon.iconset"
  mkdir -p "$ICONSET"
  sips -z 16 16 "$ICON_SOURCE" --out "$ICONSET/icon_16x16.png" >/dev/null
  sips -z 32 32 "$ICON_SOURCE" --out "$ICONSET/icon_16x16@2x.png" >/dev/null
  sips -z 32 32 "$ICON_SOURCE" --out "$ICONSET/icon_32x32.png" >/dev/null
  sips -z 64 64 "$ICON_SOURCE" --out "$ICONSET/icon_32x32@2x.png" >/dev/null
  sips -z 128 128 "$ICON_SOURCE" --out "$ICONSET/icon_128x128.png" >/dev/null
  sips -z 256 256 "$ICON_SOURCE" --out "$ICONSET/icon_128x128@2x.png" >/dev/null
  sips -z 256 256 "$ICON_SOURCE" --out "$ICONSET/icon_256x256.png" >/dev/null
  sips -z 512 512 "$ICON_SOURCE" --out "$ICONSET/icon_256x256@2x.png" >/dev/null
  sips -z 512 512 "$ICON_SOURCE" --out "$ICONSET/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$ICON_SOURCE" --out "$ICONSET/icon_512x512@2x.png" >/dev/null
  iconutil -c icns "$ICONSET" -o "$RESOURCES_DIR/AppIcon.icns"
fi

if [[ ! -x "$EXECUTABLE_PATH" ]]; then
  echo "macOS app bundle is missing executable: $EXECUTABLE_PATH" >&2
  exit 1
fi

if [[ ! -s "$APP_PATH/Contents/Info.plist" ]]; then
  echo "macOS app bundle is missing Info.plist." >&2
  exit 1
fi

ln -s /Applications "$DMG_ROOT/Applications"
hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$DMG_ROOT" \
  -format UDZO \
  "$DMG_PATH"

if [[ ! -s "$DMG_PATH" ]]; then
  echo "DMG was not created: $DMG_PATH" >&2
  exit 1
fi

hdiutil verify "$DMG_PATH"

echo "Created app bundle: $APP_PATH"
echo "Created DMG: $DMG_PATH"
echo "Build input metadata: $RESOURCES_DIR/build-metadata.json"
