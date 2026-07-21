# Multi-file clipboard sync (kind "files", protocol 1.1) + filename sanitize fix

**Date:** 2026-07-21
**Status:** Approved

## Problem

Copying multiple files syncs only the first one, silently, in all three
implementations (despite CLAUDE.md claiming multi-file lives in the
Python build — it exists nowhere):

- Swift: `grabFileURL` returns `.first` of the pasteboard URL list
  (`ClipboardWatcher.swift:223-229`), rest dropped with no notification.
- C#: `GetFirstFilePath` returns `list[0]` of `GetFileDropList()`
  (`ClipboardWatcher.cs:55-60`).
- Python: `files[0]` with an explicit scope-out comment
  (`anyclip.py:934`). On macOS the AppleScript `«class furl»` grab can
  only ever see one file at all.

The wire message is single-file by construction — one `name`, one
`content` — so multi-file has no representation on the wire.

Separately reported: the receive-side filename sanitizer is an
alnum-whitelist, so `(E&S)_SCM 마스터플랜_20250915_공유6.pptx` arrives as
`_E_S__SCM 마스터플랜_20250915_공유6.pptx` — `(`, `&`, `)` all become
`_`. Same whitelist in all three implementations.

## Decision

Add a new clip kind `"files"` carrying an array of files in a single
frame, bump `protocol_minor` 0 → 1, and gate sending on the peer's
advertised minor. Fix the sanitizer by switching whitelist → denylist.

Alternatives rejected:

- **N sequential `kind:"file"` frames** — receiver clears the clipboard
  per file (only the last survives), and the per-kind single-slot
  `EchoSuppressor` clobbers itself → echo-loop risk. Timing-based
  receiver batching is fragile.
- **ZIP-and-send** — receiver gets an archive, not files; already
  rejected in the 2026-06-10 folder-skip design.

User-approved behavior choices:

- **Scope:** all three implementations send and receive. Exception:
  Python-macOS *send* stays first-file-only (AppleScript grab
  limitation) and Python-macOS *receive* writes all files but places
  only the first on the clipboard (no reliable multi-furl AppleScript
  set; the shipped Mac app is the Swift port).
- **Over budget:** greedy — send the files that fit, in selection
  order; notify about the skipped ones.
- **Folders in the selection:** skip folders (with the existing folder
  notification), sync the remaining files. Folder sync itself remains
  scoped out.

## Wire format

New message, field order fixed (golden-vector material):

```json
{"type":"clip","kind":"files",
 "files":[{"name":"<NFC basename>","content":"<base64>",
           "hash":"<sha256 hex of raw bytes>","bytes":<int>}, ...],
 "hash":"<aggregate>","ts":<epoch float>,"bytes":<int sum of raw>}
```

- A selection of exactly **one** sendable file still uses the existing
  `kind:"file"` message — existing vectors and behavior unchanged.
  `kind:"files"` is used only for ≥ 2 sendable files.
- **Aggregate hash** (echo-suppression key, identical formula in all
  three): sort the per-file sha256 hex strings lexicographically,
  concatenate with no separator, sha256 the ASCII bytes. Order-
  independent, so pasteboard re-detection order cannot break echo
  suppression.
- Per-entry `name` is NFC-normalized on the wire (same as `kind:"file"`,
  per the v1.1.14 NFC fix).
- Frame cap stays `MAX_PAYLOAD` = 16 MiB. Send budget: existing
  `fileBudget` ≈ 12,221,153 bytes applied to the **sum** of raw file
  sizes. Sender also caps at 100 files per clip; the receiver stays
  lenient (any count that fits the frame).

## Compatibility

- `protocol_minor` 0 → 1 in all three (`Wire`/`WireProtocol` constants,
  Python `PROTOCOL_MINOR`). Minor mismatch is already advisory-only in
  `VersionNegotiator` — links stay up in both directions.
- The sender reads the peer's `protocol_minor` from the hello (PeerLink
  must retain it and expose it to the send path). Peer minor ≥ 1 →
  send `kind:"files"`. Peer minor 0 → fall back to today's behavior:
  send the first sendable file as `kind:"file"`, and report the dropped
  count through the existing skip-notification path.
- Old peers that somehow receive `kind:"files"` hit the existing
  "ignore unknown kind" branch — logged, dropped, link stays up.

## Send pipeline (per implementation)

1. Grab layer returns **all** paths: Swift `readObjects` full list,
   C# full `GetFileDropList()`, Python-Windows full `FileDropList`
   (Python-macOS stays single-path).
2. Watcher fingerprint becomes an ordered list of per-path
   `(path, size, mtime)` tuples; equality of the whole list gates
   re-detection (replaces the single-file fingerprint).
3. Filtering, in order: drop folders (one notification naming the
   skipped folder(s)); then walk the remaining files in selection
   order, greedily accepting while `sum(raw) ≤ fileBudget` and
   `count ≤ 100`; skipped files produce one notification
   ("N files skipped (too large to sync)"). A file that fails to read
   is skipped the same way (fingerprint still recorded — no retry
   loops, per the folder-skip design).
4. 0 sendable files → nothing sent. 1 → legacy `kind:"file"`.
   ≥ 2 → `kind:"files"` (or old-peer fallback above).

## Receive pipeline (per implementation)

1. Validate: every entry needs a strict-base64 `content`; any invalid
   entry ⇒ ignore the whole frame (log, no partial apply). Empty
   `files` array ⇒ ignore.
2. Per entry: sanitize name (new rules below, NFC included), then
   uniquify collisions *within the batch* after sanitization:
   `name.ext`, `name (2).ext`, `name (3).ext`, ….
3. Write all files into the flat `~/.anyclip/received/` (no subdirs —
   existing start/stop wipe semantics keep working).
4. Place **all** files on the clipboard in one operation: Swift
   `writeObjects([NSURL])`, C# `SetFileDropList` with N entries,
   Python-Windows `Set-Clipboard -Path` with N paths. Python-macOS:
   first file only (limitation noted above).
5. Baseline the watcher fingerprint list to the paths actually
   **placed on the clipboard** (not all written paths), and mark the
   suppressor for the kind a re-detection would produce: N ≥ 2 placed →
   `mark_received("files", aggregate)`; exactly 1 placed (Python-macOS)
   → `mark_received("file", that file's hash)` as well — otherwise the
   watcher re-detects the single placed file and echoes it back as a
   `kind:"file"` clip. Aggregate and per-file hashes are recomputed
   from the decoded bytes, never trusted from the wire.

## Filename sanitization fix (all three, receive side)

Replace the alnum-whitelist with a cross-platform denylist. New rules,
identical semantics in Python / Swift / C#:

1. NFC-normalize (C# keeps its tolerant catch for ill-formed UTF-16).
2. Basename: split on both `/` and `\`, keep the last component.
3. Replace `\ / < > : " | ? *`, code points < U+0020, and U+007F
   with `_`.
4. Trim trailing dots and spaces (Windows compatibility).
5. If the result is empty, `.`, or `..` → `received.bin`.
6. Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`,
   `COM1`–`COM9`, `LPT1`–`LPT9`, case-insensitive, compared against
   the stem before the first dot) → prefix with `_`. Applied in all
   three for identical behavior.

`(E&S)_SCM 마스터플랜_20250915_공유6.pptx` now survives unchanged. This
also makes the ` (n)` uniquification scheme viable (parentheses no
longer mangled). Wire encoding is unaffected — sanitization is
write-side only, so no golden-vector change comes from this fix.

## Fixtures, interop, docs

- `gen-golden-vectors.py`: add `clip_files.bin` (two files, one Korean
  and one accented-Latin name, binary content bytes) and extend
  `manifest.json`; regenerate and commit. Add matching asserts to
  Swift `GoldenVectorTests` and C# `GoldenVectorTests`.
- `fake_peer.py`: add the ability to send a `kind:"files"` clip (it
  already records any received frame generically). New Swift and C#
  `InteropTests` cases in both directions.
- Update CLAUDE.md: remove the incorrect "multi-file/folder sync lives
  only in the Python build" claim; document the new lockstep surface
  (kind `"files"`, minor 1.1, sanitize rules). Mention multi-file in
  README "How it works".

## Error handling summary

- Invalid entry in a `files` frame → whole frame ignored (logged).
- Unreadable file at send time → that file skipped + notification,
  fingerprint recorded (no retry loop).
- Oversize total → greedy subset + notification.
- Old peer → first file + notification.

## Testing

Pure-core unit tests per implementation:

1. `kind:"files"` encode/decode round-trip; field order; nil-field
   omission; > 16 MiB frame rejected.
2. Aggregate-hash formula: same value in Python, Swift, C# for the same
   file set regardless of input order (golden value in the manifest).
3. Sanitizer: `(E&S)_...pptx` unchanged; traversal (`../x`, `..`),
   denylist chars, trailing dots/spaces, reserved names, empty →
   `received.bin`; NFD Korean input → NFC output.
4. Uniquify: collision within a batch → ` (2)` suffix before the
   extension; no collision → untouched.
5. Greedy budget selection: order preserved, cumulative cap, count cap,
   folder exclusion, single-survivor falls back to `kind:"file"`.
6. Echo suppression: receive-then-redetect of the same set is
   suppressed; a different set is not.
7. Watcher fingerprint-list gating: same selection twice → one send.
8. Old-peer fallback: peer minor 0 → `kind:"file"` with first file.

Golden vectors + interop (both directions) in Swift and C# suites;
Python `tests/` additions for watcher, wire, sanitizer, negotiator.
Windows-only `AnyClipApp.Tests` cover the clipboard watcher's
multi-path grab and `SetFileDropList` placement.
