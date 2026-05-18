#!/usr/bin/env bash
# Derive .icns / .ico from the canonical PNG icon in this dir.
# Re-run any time anyclip.png changes. Requires macOS `sips` +
# `iconutil` (already on every Mac) and Pillow on the build Python
# (already in requirements.txt). Cross-platform builds out of scope
# for v1.0; the script aborts if not run on macOS.

set -euo pipefail

if [[ "$(uname)" != "Darwin" ]]; then
  echo "build.sh: must run on macOS (uses sips + iconutil)" >&2
  exit 1
fi

ROOT="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$ROOT/../.." && pwd)"
cd "$ROOT"

# Source PNG lives alongside this script; built artifacts are committed
# under app/icons/ so the GUI shells can resolve them with a path
# relative to their own module rather than relying on bundle layout.
ASSETS="$REPO/app/icons"
mkdir -p "$ASSETS"

SRC="anyclip.png"
ICNS_OUT="$ASSETS/anyclip.icns"
ICO_OUT="$ASSETS/anyclip.ico"
ICONSET="$ROOT/anyclip.iconset"

PYTHON_BIN="${PYTHON_BIN:-python3}"

if ! command -v sips >/dev/null 2>&1; then
  echo "build.sh: sips not found in PATH" >&2
  exit 1
fi
if ! "$PYTHON_BIN" -c "import PIL" >/dev/null 2>&1; then
  echo "build.sh: Pillow not importable from $PYTHON_BIN" >&2
  echo "  pip install -r ../../requirements.txt" >&2
  exit 1
fi
if [[ ! -f "$SRC" ]]; then
  echo "build.sh: source $SRC missing" >&2
  exit 1
fi

echo "[1/2] building $ICNS_OUT from $SRC"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  d=$((s*2))
  sips -s format png -z "$s" "$s" "$SRC" \
    --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -s format png -z "$d" "$d" "$SRC" \
    --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$ICNS_OUT"
rm -rf "$ICONSET"

echo "[2/2] building $ICO_OUT from $SRC"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
for s in 16 32 48 256; do
  sips -s format png -z "$s" "$s" "$SRC" \
    --out "$TMP/icon_${s}.png" >/dev/null
done
"$PYTHON_BIN" - <<PYEOF
from PIL import Image
sizes = [16, 32, 48, 256]
base = Image.open("${TMP}/icon_256.png")
base.save("${ICO_OUT}", format="ICO",
          sizes=[(s, s) for s in sizes])
PYEOF

echo "done: $ICNS_OUT, $ICO_OUT"
