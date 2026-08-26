# Folder Sync (protocol 1.3) — Design

Date: 2026-08-26 · Target release: v1.4.0 · Applies to all three implementations (Python, Swift/macOS, C#/Windows)

## Problem

Copying a folder does nothing today: every watcher deliberately skips directories with a
"folder on clipboard not synced (unsupported)" warning. Users who move document trees
between machines must flatten the selection by hand (open the folder, select the files) and
lose the subfolder structure on the receiving side. Folder sync was scoped out of 1.1–1.3;
this design scopes it in.

## Decision

Extend the existing `kind:"files"` clip: each file entry gains an OPTIONAL `"path"` field
carrying the file's relative path (top-level folder name included). No new frame kind, no
new codec. Receivers that understand `path` rebuild the tree under `received/`; receivers
that don't (≤ 1.3.0) ignore the unknown field — their strict decoder reads only `name` and
`content` — and write the same files flat, exactly like today's multi-file clip. Old peers
therefore degrade gracefully with **no send gate and no wire break**.

`PROTOCOL_MINOR` bumps 2 → 3. Minor semantics stay cumulative:
minor ≥ 1 accepts `kind:"files"`, ≥ 2 accepts 64 MiB frames, ≥ 3 rebuilds folder trees.
Minor 3 is a capability *marker* only (logging/UI); it gates nothing on the send path.

## Wire format

Entry today: `{"name": "<basename>", "content": "<base64>"}`.
Entry in a folder: `{"name": "<basename>", "content": "<base64>", "path": "<top>/<sub>/<basename>"}`.

Rules for `path` (sender MUST, receiver MUST verify):

- POSIX `/` separators only; NFC-normalized (same rule as `name` since v1.1.14).
- Relative: no leading `/`, no drive letters, no `.`/`..` segments, no empty segments,
  no backslashes.
- Last segment equals `name`.
- ≤ 32 segments; sanitized total length ≤ 240 characters.
- Files selected directly (not inside a copied folder) carry NO `path` field, so every
  frame that exists today stays byte-identical. Golden vectors for existing cases do not
  change; the hello vector changes (minor 3) and one NEW golden vector pins a
  files-with-path frame.

## Send side

Where watchers today skip a directory, they now expand it:

- Recursive walk, files only, deterministic sorted order (byte-wise on the relative path).
- Excluded, log-only: `.DS_Store`, `Thumbs.db`, `desktop.ini`; symlinks (never followed —
  also makes cycles impossible). Empty directories are not representable and are dropped.
- `path` = `<top-folder-name>/<relative path>`; multiple folders in one selection each get
  their own top name; loose files in the same selection are encoded as today (no `path`).

**Per-folder all-or-nothing** (user decision): before reading any content, walk the folder
and total raw sizes + file count. Selection items are processed in selection order, each
consuming the remaining budget/count: a folder is accepted only if its ENTIRE total fits
what remains — otherwise the WHOLE folder is skipped with toast
`folder too large to sync: <name>` — no partial trees. Loose files keep today's greedy
per-file behavior and the existing
"N file(s) skipped (too large to sync)" toast. An empty folder (nothing left after
exclusions) toasts `folder is empty; nothing to sync`.

Caps: `FILE_BUDGET` unchanged (49,466,572 bytes). `MAX_FILES_PER_CLIP` /
`maxFilesPerClip` / `MaxFilesPerClip` raises 100 → **500** for all selections (document
trees pass 100 easily; the budget is the real limit; worst-case extra JSON envelope stays
inside the existing 256 KiB reservation).

Echo suppression is unchanged: the aggregate hash is computed over decoded file bytes, so
tree vs. flat delivery of the same bytes suppresses identically. The watcher fingerprint
(`_last_file_fp` and equivalents) must cover the expanded file list so an unsyncable or
just-sent folder is not re-detected every poll.

Fallback matrix per link (evaluated in the existing per-link variant chooser):

| Peer minor | Folder entries | Loose files |
|---|---|---|
| ≥ 3 | full tree (`path` honored) | as today |
| 1–2 | same frame; receiver flattens benignly (log once per clip: `peer <name> will flatten folders (protocol < 1.3)`) | as today |
| 0 | folder entries EXCLUDED from the first-file `kind:"file"` fallback; folder-only clip sends nothing on that link (log only) | first loose file, as today |

The 1.3.0 per-link 64 MiB size gate is untouched and applies to whatever variant is chosen.

## Receive side

Entries without `path` (or from senders ≤ 1.3.0): exactly today's flat behavior.

Entries with `path`:

- Validate against the Wire-format rules above. On ANY violation, fall back to flat
  placement for THAT entry (sanitized `name` only) — never drop the frame, never write
  outside `received/`. Path traversal is the attack to kill: verify the resolved
  destination stays under `received/` after sanitization.
- Sanitize + NFC-normalize each segment with the existing per-name sanitizer; create
  intermediate directories.
- Top-level collision: if `received/<top>` already exists, uniquify the top segment
  (`<top>-2`, `<top>-3`, using the same comparison rule as the existing file uniquify) and apply
  the SAME replacement to every entry sharing that top segment within the clip, so one clip
  lands in one new folder. Loose entries keep per-file uniquify.
- Clipboard placement: the clip's top-level items in batch order — each top folder once
  (as a folder path) plus loose files. Swift: `NSPasteboard` file URLs (all items).
  C# and Python/Windows: `CF_HDROP` (all items). Python/macOS: first top-level item only
  (existing AppleScript limitation, unchanged).
- Received toast follows the existing wording pattern, naming the folder when the clip is
  folder-only (e.g. `<top> (N files)`).

## Not in scope

Empty directories, symlinks, folder delivery to protocol-1.0 peers, lifting the
Python/macOS single-item clipboard limitation, folder-size progress UI. Sparkle/WinSparkle
and `--headless` remain Python-only as before.

## Testing

- **Golden vectors**: regenerate (hello now minor 3) and ADD a files-with-path vector from
  the canonical Python encoder; both native `GoldenVectorTests` assert it.
- **Unit, all three implementations**: path validation/sanitization (traversal `..`,
  absolute, drive letter, backslash, deep/long paths, Korean NFC round-trip, reserved
  names); per-folder all-or-nothing at the budget and count boundaries; junk/symlink
  exclusion; deterministic walk order; top-folder uniquify incl. same-clip consistency;
  flat fallback when `path` is stripped (old-receiver simulation); minor-0 fallback matrix.
- **Interop** (fake_peer.py stays UNMODIFIED, still minor 0): folder-only clip to a
  minor-0 peer sends nothing; multi-file behavior against fake_peer unchanged.
- **Cross-implementation**: each suite writes a real tree into a temp `received/` and
  asserts structure + clipboard placement where the platform allows.
- Suites: `pytest tests/`, `swift test --package-path formacOS`,
  `dotnet test forwindows/tests/AnyClipCore.Tests` (App tests remain Windows-CI-only).
