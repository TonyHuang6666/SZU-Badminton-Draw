#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS packaging requires hdiutil and must be run on macOS." >&2
  exit 1
fi

RID="${1:-osx-arm64}"
CONFIGURATION="${CONFIGURATION:-Release}"
APP_NAME="${APP_NAME:-SZU Badminton Draw}"
BUNDLE_ID="${BUNDLE_ID:-com.szuba.badmintondraw}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj"
OUTPUT_ROOT="$ROOT_DIR/artifacts/macos/$RID"
PUBLISH_DIR="$OUTPUT_ROOT/publish"
DMG_ROOT="$OUTPUT_ROOT/dmg-root"
APP_PATH="$DMG_ROOT/$APP_NAME.app"
MACOS_DIR="$APP_PATH/Contents/MacOS"
RESOURCES_DIR="$APP_PATH/Contents/Resources"
DMG_PATH="$OUTPUT_ROOT/SZU-Badminton-Draw_${RID}.dmg"
ICON_SOURCE="$ROOT_DIR/src/BadmintonDraw.App/Assets/szuba-app-icon.png"
ICON_FILE=""
ICON_PLIST_ENTRY=""

rm -rf "$OUTPUT_ROOT"
mkdir -p "$PUBLISH_DIR" "$MACOS_DIR" "$RESOURCES_DIR"

dotnet publish "$PROJECT_PATH" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  -o "$PUBLISH_DIR"

cp -R "$PUBLISH_DIR"/. "$MACOS_DIR"/
chmod +x "$MACOS_DIR/BadmintonDraw.Desktop"

if command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1 && [[ -f "$ICON_SOURCE" ]]; then
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
  ICON_FILE="AppIcon"
  ICON_PLIST_ENTRY="  <key>CFBundleIconFile</key>
  <string>$ICON_FILE</string>"
fi

cat > "$APP_PATH/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>zh_CN</string>
  <key>CFBundleDisplayName</key>
  <string>$APP_NAME</string>
  <key>CFBundleExecutable</key>
  <string>BadmintonDraw.Desktop</string>
  <key>CFBundleIdentifier</key>
  <string>$BUNDLE_ID</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>$APP_NAME</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${VERSION:-0.0.0}</string>
  <key>CFBundleVersion</key>
  <string>$(date +%Y%m%d%H%M)</string>
$ICON_PLIST_ENTRY
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
PLIST

ln -s /Applications "$DMG_ROOT/Applications"
hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$DMG_ROOT" \
  -ov \
  -format UDZO \
  "$DMG_PATH"

echo "Created app bundle: $APP_PATH"
echo "Created DMG: $DMG_PATH"
