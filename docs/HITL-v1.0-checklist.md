# v1.0 Release — Human-In-The-Loop Checklist

Slice 13 (issue #14). Every item here is a manual action the
maintainer performs once before publishing v1.0.0.

## 0. Prerequisites (already in repo)

- [x] Slices 1–12 merged and pushed.
- [x] Local pytest suite green:
      `python -m pytest tests/`.
- [x] `build/icon/build.sh` re-run if any SVG was edited.

## 1. Sparkle / WinSparkle one-time setup

Follow [docs/SPARKLE-SETUP.md](SPARKLE-SETUP.md) end to end:

- [ ] `generate_keys` run locally; private key sits in Keychain.
- [ ] Repository Secrets `SPARKLE_PUBLIC_KEY` and
      `SPARKLE_PRIVATE_KEY` set.
- [ ] `gh-pages` branch created (CI auto-creates on first publish
      if missing).
- [ ] GitHub Pages enabled on `gh-pages` branch, root `/`.

## 2. Pre-flight dry run (release-candidate)

- [ ] `git tag v0.9.0-rc1 && git push origin v0.9.0-rc1`
- [ ] Wait for the **Release** workflow. All three jobs (macos,
      windows, appcast) go green.
- [ ] Confirm Release page has:
  - `AnyClip-v0.9.0-rc1.dmg`
  - `AnyClip-v0.9.0-rc1-windows-x64.zip`
  - `macos-signature.txt`, `windows-signature.txt`
- [ ] `https://gum798.github.io/AnyClip/appcast.xml` returns the
      rendered XML with both enclosures.

## 3. Clean-environment smoke tests

### macOS (clean Mac or fresh user account)

- [ ] Download `AnyClip-v0.9.0-rc1.dmg`, open, drag to
      `/Applications`, eject.
- [ ] First launch: right-click → Open → Open in the warning dialog.
- [ ] Local Network permission prompt appears; accept.
- [ ] Onboarding window appears; click **Generate new token**.
- [ ] Menubar icon shows `Searching for peer` then `Linked: …`
      after the second device joins.
- [ ] Copy text → paste on the other side. Repeat with image and
      file clipboard items.

### Windows (clean Win11 or fresh user)

- [ ] Download the `.zip`, unzip, double-click `AnyClip.exe`.
- [ ] SmartScreen → More info → Run anyway.
- [ ] Firewall prompt appears; allow Private network.
- [ ] Onboarding window appears; choose **Enter existing token** and
      paste the macOS token.
- [ ] Tray icon transitions Searching → Linked, both directions
      copy/paste work.

## 4. Permission-recovery flow (macOS)

- [ ] System Settings → Privacy & Security → Local Network → turn
      AnyClip **off**.
- [ ] Quit and relaunch AnyClip.
- [ ] After ≤30 s the menubar shows ⚠ and a new "Open Local
      Network Settings" menu item.
- [ ] Click it; macOS jumps to the right pane.
- [ ] Turn AnyClip back on, quit, relaunch → state returns to
      Linked.

## 5. Start-at-Login round-trip

- [ ] macOS: menubar → **Start at Login** (toggle on). Quit. Reboot.
      AnyClip auto-launches; menubar visible.
- [ ] Toggle off, quit, reboot → AnyClip does NOT auto-launch.
- [ ] Windows: tray → **Start at Login** on. Reboot. Tray icon back.
- [ ] Toggle off → no auto-start after next reboot.

## 6. Auto-update bootstrap dry run

- [ ] Tag `v0.9.0-rc2` (or any version > rc1) → push → CI publishes.
- [ ] On the v0.9.0-rc1 install: **Check for Updates…** finds rc2.
- [ ] Accept the update; app downloads, verifies, asks to restart.
- [ ] Relaunch picks up rc2 (check menubar's About / version).
- [ ] Negative test: truncate a published `.dmg` (`dd` first 1 MiB to
      `/dev/null`), re-upload as `v0.9.0-rc3`. Sparkle on rc2
      refuses the update with a signature mismatch error.
- [ ] Delete the rc1/rc2/rc3 test tags and releases.

## 7. Cut v1.0.0

- [ ] Update CHANGELOG / commit if applicable.
- [ ] Edit `docs/RELEASE-v1.0.md` to taste; copy its body as the
      Release description on GitHub.
- [ ] `git tag v1.0.0 && git push origin v1.0.0`.
- [ ] Workflow goes green; Release page has `.dmg` + `.zip` +
      signatures.
- [ ] Verify `appcast.xml` lists v1.0.0 with both enclosures.

## 8. Migration broadcast

- [ ] GitHub Discussion or pinned Issue announcing v1.0.0:
  - Link to the Release page.
  - One-time download-and-replace instructions for existing
    `python anyclip.py` users.
  - Note about auto-update being live for every release after v1.0.0.

## 9. Post-release housekeeping

- [ ] Close issues #2–#14 referencing the commits that landed each
      slice.
- [ ] Mark PRD issue #1 as resolved.
- [ ] Bump `APP_VERSION` in `anyclip.py` to `1.0.1` on `main` so the
      next dev build is not confused with v1.0.0.
