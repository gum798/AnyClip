#!/usr/bin/env bash
# Derive .icns / .ico / tray PDFs from the canonical SVGs in this dir.
# Re-run any time the SVGs change. Requires macOS `sips` + `iconutil`
# (already on every Mac) and Pillow on the build Python (already in
# requirements.txt). Cross-platform builds out of scope for v1.0; the
# script aborts if not run on macOS.

set -euo pipefail

if [[ "$(uname)" != "Darwin" ]]; then
  echo "build.sh: must run on macOS (uses sips + iconutil)" >&2
  exit 1
fi

ROOT="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$ROOT/../.." && pwd)"
cd "$ROOT"

# Source SVGs live alongside this script; built artifacts are committed
# under app/icons/ so the GUI shells can resolve them with a path
# relative to their own module rather than relying on bundle layout.
ASSETS="$REPO/app/icons"
mkdir -p "$ASSETS/tray"

SRC="anyclip.svg"
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

echo "[1/3] building $ICNS_OUT from $SRC"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
# iconutil expects a strict naming convention with @1x and @2x pairs.
for s in 16 32 128 256 512; do
  d=$((s*2))
  sips -s format png -z "$s" "$s" "$SRC" \
    --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -s format png -z "$d" "$d" "$SRC" \
    --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$ICNS_OUT"
rm -rf "$ICONSET"

echo "[2/3] building $ICO_OUT from $SRC"
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

echo "[3/3] building tray PDFs (template images, macOS auto light/dark)"
for state in linked searching error; do
  svg="tray/${state}.svg"
  png22="$ASSETS/tray/${state}.png"
  png44="$ASSETS/tray/${state}@2x.png"
  pdf="$ASSETS/tray/${state}.pdf"
  sips -s format png -z 22 22 "$svg" --out "$png22" >/dev/null
  sips -s format png -z 44 44 "$svg" --out "$png44" >/dev/null
  # macOS NSImage accepts PDFs as template images. sips on Sonoma+ can
  # convert PNG -> PDF directly; fall back to Pillow if that path
  # fails.
  if ! sips -s format pdf "$png22" --out "$pdf" >/dev/null 2>&1; then
    "$PYTHON_BIN" - <<PYEOF
from PIL import Image
Image.open("${png22}").save("${pdf}", format="PDF", resolution=72.0)
PYEOF
  fi
done

echo "done: $ICNS_OUT, $ICO_OUT, tray/*.{png,pdf}"
