#!/bin/bash
# Assemble AnyClip.app from the SwiftPM release build.
# Usage: Scripts/build-app.sh   (run from anywhere; cd's to the package)
# Version: env ANYCLIP_BUILD_VERSION (default 0.0.0-dev), mirroring the
# Python CI convention.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${ANYCLIP_BUILD_VERSION:-0.0.0-dev}"
APP="dist/AnyClip.app"

swift build -c release --arch arm64

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/arm64-apple-macosx/release/AnyClipApp "$APP/Contents/MacOS/AnyClip"
sed "s/__VERSION__/$VERSION/g" Resources/Info.plist.template \
    > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
cp ../app/icons/anyclip.icns "$APP/Contents/Resources/anyclip.icns"

# Ad-hoc signature: keeps the existing right-click-to-open Gatekeeper flow.
codesign --force --sign - "$APP"

echo "Built $APP (version $VERSION)"
