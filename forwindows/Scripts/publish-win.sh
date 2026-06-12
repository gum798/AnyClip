#!/bin/bash
# Cross-publish the Windows single-file exe + zip from any host.
# Version: env ANYCLIP_BUILD_VERSION (default 0.0.0-dev).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${ANYCLIP_BUILD_VERSION:-0.0.0-dev}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT="dist"

"$DOTNET" publish src/AnyClipApp -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableWindowsTargeting=true \
  -p:InformationalVersion="$VERSION" \
  -o "$OUT/publish"

rm -f "$OUT/AnyClip-v$VERSION-windows-x64-native.zip"
(cd "$OUT/publish" && zip -q -r "../AnyClip-v$VERSION-windows-x64-native.zip" .)
echo "Built $OUT/AnyClip-v$VERSION-windows-x64-native.zip"
