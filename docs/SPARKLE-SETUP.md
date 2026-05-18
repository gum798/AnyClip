# Sparkle Auto-Update Setup (macOS)

These are the one-time steps the maintainer runs to enable auto-update
for the macOS build. The release CI handles every release after that.

## 1. Generate the EdDSA keypair (one time)

Sparkle ships `generate_keys` in its release tarball. Run it locally on
the maintainer's Mac:

```bash
curl -L -o Sparkle.tar.xz \
  https://github.com/sparkle-project/Sparkle/releases/download/2.6.4/Sparkle-2.6.4.tar.xz
mkdir sparkle && tar -xJf Sparkle.tar.xz -C sparkle
./sparkle/bin/generate_keys
```

`generate_keys` stores the private key in the macOS Keychain and prints
the base64 public key to stdout. Copy that public string -- it goes in
two places.

## 2. Configure GitHub Secrets

In the repository settings, add two secrets:

| Secret name             | Value                                                 |
|-------------------------|-------------------------------------------------------|
| `SPARKLE_PUBLIC_KEY`    | The base64 public key printed by `generate_keys`.     |
| `SPARKLE_PRIVATE_KEY`   | The base64 private key (export it from Keychain with `sparkle/bin/generate_keys -x`). |

The release workflow reads both at build time. The private key is fed
to `sign_update` via stdin so it never lands on disk on the runner.

## 3. Configure GitHub Pages

The release workflow pushes `appcast.xml` to the `gh-pages` branch.
Enable GitHub Pages from that branch in the repo settings (`Settings ->
Pages -> Branch: gh-pages, Folder: /`). The stable feed URL becomes
`https://gum798.github.io/AnyClip/appcast.xml`, which matches the
`SUFeedURL` baked into `Info.plist`.

## 4. First release is special

The first signed release is the bootstrap point for every future
auto-update. Mistakes in key wiring, signature format, or appcast
content can put every installed copy into a permanent "update failed"
loop.

Before tagging the first signed release:

- Confirm `SPARKLE_PUBLIC_KEY` is non-empty in the build environment;
  `setup.py` injects it as `SUPublicEDKey` in `Info.plist`, and an
  empty value silently disables EdDSA verification on the user's side.
- Confirm `Sparkle.framework` is bundled by inspecting
  `dist/AnyClip.app/Contents/Frameworks/Sparkle.framework` after the
  py2app step.
- Cut a pre-release tag (`v0.9.0-rc1`) first. Install the resulting
  `.dmg` and verify the menubar's "Check for Updates…" item works
  and reports "you are up to date".
- Then cut a real `v1.0.0`. From a fresh install of the rc1, the
  Sparkle auto-check should within a day surface the v1.0.0 update;
  trigger it manually via "Check for Updates…" to validate the full
  download + signature-verify + install path.
- As a negative test, truncate the `.dmg` (e.g. `dd` the first 1 MiB
  to /dev/null) before re-uploading and confirm Sparkle refuses the
  update because the EdDSA signature no longer matches.

## 5. Day-to-day releases

Once steps 1-4 are done, every future release is just:

```bash
git tag v1.0.1
git push origin v1.0.1
```

The CI:

1. Builds the `.app` with the Sparkle public key baked in,
2. Packages it into a `.dmg`,
3. Signs the `.dmg` with `sign_update` using the private-key secret,
4. Renders `docs/appcast.template.xml` with the new version + signature
   + length, and pushes it to `gh-pages` as `appcast.xml`,
5. Uploads the `.dmg` + signature to the GitHub Release.

Installed copies see the new entry within their next scheduled check
(daily, per `SUScheduledCheckInterval`).
