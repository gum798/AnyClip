# AnyClip v1.0.0 — Release Notes (draft)

> Maintainer template. Edit the sections marked TODO before publishing.

## What's new in v1.0.0

AnyClip is now a real app on macOS and Windows.

- **Download → double-click installs.** No more `git clone`, no
  virtualenv, no `pip install`. Get the `.dmg` (macOS Apple Silicon)
  or `.zip` (Windows x64) from the
  [Releases](https://github.com/gum798/AnyClip/releases/latest) page.
- **Menubar / system-tray UI.** Status, last sync, token panel, Start
  at Login toggle, Open Logs, Check for Updates… and Quit. No console
  window on Windows; no Dock icon on macOS.
- **First-run onboarding dialog.** Generate a new token on the first
  device, paste it on the second. Token persists in
  `~/.anyclip/config.json` (0600).
- **Auto-update.** Sparkle (macOS) and WinSparkle (Windows) check
  daily; new releases land in the background and only ask you to
  restart. Every update is EdDSA-signed.
- **Self-healing daemon** carried over from Phase 2.1: supervisor
  restart, mDNS idle watchdog, app-layer ping, 30-second Local
  Network self-diagnosis with a "permission missing" warning in the
  menubar, brute-force token cooldown.
- **--headless mode.** Same binary works as a pure CLI daemon for
  servers / launchd / Task Scheduler. All previous CLI flags work
  unchanged. See README -> Advanced.

## Known limitations

- **Unsigned build.** Both bundles ship without a code-signing
  certificate. First-run requires the usual bypass:
  - macOS: right-click `AnyClip.app` -> Open -> Open in the warning
    dialog.
  - Windows: SmartScreen -> More info -> Run anyway.
- **macOS Apple Silicon only.** Intel Mac and Windows ARM64 are out
  of scope for v1.0.
- **LAN only.** No NAT traversal, no 3+ device sync.
- **Plaintext content over the wire.** The token handshake is hashed
  but message payloads themselves are not encrypted. AnyClip assumes
  the LAN is trusted.

## Auto-update bootstrap (one-time)

v1.0.0 is the bootstrap point. Anyone running an older non-packaged
copy (`python anyclip.py`) needs to install v1.0.0 by hand once.
Every release after v1.0.0 reaches that install automatically through
Sparkle / WinSparkle.

## Migration from the CLI workflow

If you were running `python anyclip.py` from a launchd plist
(`~/Library/LaunchAgents/com.anyclip.plist`) or a Windows Scheduled
Task, you have two paths:

**Stay on the CLI.** Pull the latest source and you keep working. The
old flow still works.

**Move to the packaged app.** Replace `python anyclip.py` in your
plist / task with the new bundle plus `--headless`. The
README's Migration section has copy-pastable before/after blocks.
The easiest substitute is the menubar/tray **Start at Login** toggle,
which writes the equivalent entry for you.

## Where to ask questions

- Bug reports / feature requests:
  [github.com/gum798/AnyClip/issues](https://github.com/gum798/AnyClip/issues)
- Logs (always attach these for bug reports):
  `~/.anyclip/anyclip.log` -- menubar/tray -> Open Logs -> attach the
  most recent rotated copy.

---

## TODO before publishing

- [ ] Replace this line with a one-paragraph "thanks to" / changelog
      summary if desired.
- [ ] Verify the screenshot / GIF links work once added.
- [ ] Confirm the `.dmg` and `.zip` filenames in the Quick Start
      section match the published asset names.
