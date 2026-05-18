"""py2app build spec for the macOS menubar shell.

Build with:
    python setup.py py2app
The resulting bundle lands at dist/AnyClip.app. LSUIElement=true keeps
the app off the Dock so only the menubar icon is visible.
"""

from __future__ import annotations

import sys

from setuptools import setup

APP = ["anyclip.py"]
DATA_FILES: list = []

PLIST = {
    "CFBundleName": "AnyClip",
    "CFBundleDisplayName": "AnyClip",
    "CFBundleIdentifier": "io.github.gum798.anyclip",
    "CFBundleVersion": "1.0.0",
    "CFBundleShortVersionString": "1.0.0",
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
}

OPTIONS = {
    "argv_emulation": False,
    "plist": PLIST,
    # macOS .app Finder/About icon. Built by build/icon/build.sh.
    "iconfile": "app/icons/anyclip.icns",
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
