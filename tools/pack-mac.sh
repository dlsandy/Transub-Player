#!/usr/bin/env bash
# Build Transub Player .app (+ optional .dmg) for macOS.
# Does NOT bundle ASR/MT model weights.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-}"
if [[ -z "$RID" ]]; then
  case "$(uname -m)" in
    arm64|aarch64) RID=osx-arm64 ;;
    *) RID=osx-x64 ;;
  esac
fi

OUT="$ROOT/dist/mac/$RID"
APP="$OUT/Transub Player.app"
PUBLISH="$OUT/publish"

echo "Building Desktop ($RID)…"
dotnet publish "$ROOT/src/TransubPlayer.Desktop/TransubPlayer.Desktop.csproj" \
  -c Release -r "$RID" --self-contained true \
  -o "$PUBLISH"

# Prefer bundled native/mpv; otherwise fetch.
if [[ ! -x "$PUBLISH/mpv/mpv" && ! -x "$ROOT/native/mpv/mpv" ]]; then
  bash "$ROOT/tools/fetch-mpv-mac.sh" || true
fi
if [[ -x "$ROOT/native/mpv/mpv" ]]; then
  mkdir -p "$PUBLISH/mpv"
  cp -f "$ROOT/native/mpv/mpv" "$PUBLISH/mpv/mpv"
  chmod +x "$PUBLISH/mpv/mpv"
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -a "$PUBLISH/." "$APP/Contents/MacOS/"
cp "$ROOT/tools/mac/Info.plist" "$APP/Contents/Info.plist"
if [[ -f "$ROOT/src/TransubPlayer/Assets/app.png" ]]; then
  cp "$ROOT/src/TransubPlayer/Assets/app.png" "$APP/Contents/Resources/app.png"
fi

# Optional llama-server Metal binary (user-provided).
if [[ -x "$ROOT/native/llama/llama-server" ]]; then
  mkdir -p "$APP/Contents/MacOS/llama"
  cp -f "$ROOT/native/llama/llama-server" "$APP/Contents/MacOS/llama/llama-server"
fi

chmod +x "$APP/Contents/MacOS/TransubPlayer" || true

cat > "$OUT/README-CODESIGN.txt" <<'EOF'
Codesign / notarize (on a Mac with Developer ID):

  codesign --deep --force --options runtime \
    --sign "Developer ID Application: YOUR NAME (TEAMID)" \
    "Transub Player.app"

  xcrun notarytool submit "Transub Player.dmg" --keychain-profile "AC_PROFILE" --wait
  xcrun stapler staple "Transub Player.dmg"

Default package excludes Whisper / GGUF model weights — users download in Settings.
EOF

if command -v hdiutil >/dev/null 2>&1; then
  DMG="$OUT/TransubPlayer-$RID.dmg"
  rm -f "$DMG"
  hdiutil create -volname "Transub Player" -srcfolder "$APP" -ov -format UDZO "$DMG"
  echo "DMG: $DMG"
fi

echo "App: $APP"
