#!/usr/bin/env bash
# Fetch a macOS mpv binary into native/mpv for Transub Player (Desktop).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/native/mpv"
mkdir -p "$DEST"

ARCH="$(uname -m)"
case "$ARCH" in
  arm64|aarch64) RID=osx-arm64 ;;
  x86_64) RID=osx-x64 ;;
  *) echo "Unsupported arch: $ARCH" >&2; exit 1 ;;
esac

echo "Fetching mpv for $RID …"

if command -v brew >/dev/null 2>&1; then
  brew list mpv >/dev/null 2>&1 || brew install mpv
  MPV_BIN="$(brew --prefix mpv)/bin/mpv"
  if [[ -x "$MPV_BIN" ]]; then
    cp -f "$MPV_BIN" "$DEST/mpv"
    chmod +x "$DEST/mpv"
    echo "Installed mpv from Homebrew → $DEST/mpv"
    exit 0
  fi
fi

if command -v mpv >/dev/null 2>&1; then
  cp -f "$(command -v mpv)" "$DEST/mpv"
  chmod +x "$DEST/mpv"
  echo "Copied mpv from PATH → $DEST/mpv"
  exit 0
fi

cat <<EOF >&2
Could not locate mpv. Install with Homebrew:
  brew install mpv
Then re-run: tools/fetch-mpv-mac.sh
EOF
exit 1
