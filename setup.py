"""py2app build spec for the macOS menubar shell.

Build with:
    python setup.py py2app
The resulting bundle lands at dist/AnyClip.app. LSUIElement=true keeps
the app off the Dock so only the menubar icon is visible.
"""

from __future__ import annotations

import os
import sys

from setuptools import setup

APP = ["anyclip.py"]
DATA_FILES: list = []

# Sparkle public key. The matching private key lives in the
# SPARKLE_PRIVATE_KEY GitHub Actions Secret; the public half is
# injected at build time via SPARKLE_PUBLIC_KEY so it never lives in
# git history. The maintainer generates the pair once with Sparkle's
# `generate_keys` tool (see docs/SPARKLE-SETUP.md) and sets this env
# var both locally and on the CI runner.
SPARKLE_PUBLIC_KEY = os.environ.get("SPARKLE_PUBLIC_KEY", "")

# Two version strings, deliberately separate:
#  - BUILD_VERSION (CFBundleShortVersionString) is the human-facing
#    semver string -- "0.9.0-rc1", "1.0.0", etc.
#  - BUILD_NUMBER (CFBundleVersion) is the *Sparkle comparison key*.
#    Sparkle (and Apple) require this to be monotonically increasing
#    across releases; semver suffixes like "-rc1" / "-rc2" tie when
#    Sparkle parses them, so we use a separate counter that always
#    grows. CI exports it as a Unix timestamp captured at build time
#    (see release.yml). Local source builds get a low default so an
#    installed release always wins.
BUILD_VERSION = os.environ.get("ANYCLIP_BUILD_VERSION", "0.0.0-dev")
BUILD_NUMBER = os.environ.get("ANYCLIP_BUILD_NUMBER", "0")

PLIST = {
    "CFBundleName": "AnyClip",
    "CFBundleDisplayName": "AnyClip",
    "CFBundleIdentifier": "io.github.gum798.anyclip",
    "CFBundleVersion": BUILD_NUMBER,
    "CFBundleShortVersionString": BUILD_VERSION,
    # No Dock icon, no Cmd-Tab presence: this is a menubar-only app.
    "LSUIElement": True,
    # macOS Sonoma+: the user must approve Local Network access. The
    # string below appears in the system prompt.
    "NSLocalNetworkUsageDescription": (
        "AnyClip discovers the other device's clipboard service on your "
        "local network."
    ),
    # mDNS service type we advertise/browse. Pre-declaring it lets
    # Sonoma's Local Network permission dialog name the service.
    "NSBonjourServices": ["_anyclip._tcp"],
    "LSMinimumSystemVersion": "14.0",
    # Sparkle auto-update wiring. The feed URL is GitHub Pages on the
    # main repo; the public key is what users' Sparkle uses to verify
    # the EdDSA signature on each downloaded .dmg.
    "SUFeedURL": "https://gum798.github.io/AnyClip/appcast.xml",
    "SUPublicEDKey": SPARKLE_PUBLIC_KEY,
    "SUEnableAutomaticChecks": True,
    "SUScheduledCheckInterval": 86400,  # daily
}

OPTIONS = {
    "argv_emulation": False,
    "plist": PLIST,
    # macOS .app Finder/About icon. Built by build/icon/build.sh.
    "iconfile": "app/icons/anyclip.icns",
    # Bundle Sparkle.framework when SPARKLE_FRAMEWORK_PATH is set. CI
    # downloads Sparkle.framework into a known location and exports
    # this var before running py2app; local source-checkout builds
    # without auto-update support set it empty and Sparkle is omitted.
    "frameworks": (
        [os.environ["SPARKLE_FRAMEWORK_PATH"]]
        if os.environ.get("SPARKLE_FRAMEWORK_PATH") else []
    ),
    # `packages` ensures the entire local module set is copied (the
    # daemon imports them at runtime via `app.menubar_mac.launch_gui`).
    # The `app` package already contains the `icons/` subfolder; py2app
    # copies non-Python files inside a `packages` entry for free, so
    # the tray PDFs travel with the bundle without a separate data_files
    # block.
    "packages": [
        "app",
        "pyperclip",
        "zeroconf",
        "rumps",
    ],
    "includes": [
        "anyclip",
        "autostart",
        "config_store",
        "peer_state",
        "permission_probe",
        "version_negotiator",
    ],
}

if __name__ == "__main__":
    if sys.platform != "darwin":
        sys.stderr.write(
            "setup.py is the py2app spec; run on macOS to build the .app.\n"
        )
        sys.exit(1)
    setup(
        app=APP,
        name="AnyClip",
        data_files=DATA_FILES,
        options={"py2app": OPTIONS},
        setup_requires=["py2app"],
    )
