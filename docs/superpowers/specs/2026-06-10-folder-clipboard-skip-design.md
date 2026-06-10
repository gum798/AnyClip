# Folder clipboard skip + retry-loop fix

**Date:** 2026-06-10
**Status:** Approved

## Problem

Users copy folders in Finder/Explorer expecting them to sync. The file
clipboard path (`ClipboardWatcher._check_file_clipboard`) cannot handle
directories:

1. `Path(path).read_bytes()` fails with `EISDIR`, logged only at DEBUG —
   the user gets zero feedback and assumes file sync is broken.
2. The early `return` on read failure skips the `_last_file_fp` update,
   so the watcher re-detects the same directory every poll cycle and
   retries forever (observed: 3,385 identical log lines for one copy).

Single-file sync itself works (verified inbound `.ovpn` receive in logs).

## Decision

Folders are explicitly **not synced** (scope-out, same as multi-file).
Instead of failing silently in a loop:

- Detect directories from the already-fetched `os.stat` result
  (`stat.S_ISDIR(st_mode)`) — no extra syscall.
- Log one WARNING and fire a desktop toast once per copy
  ("folder not synced — folders are not supported: <name>").
- Update `_last_file_fp` so the next poll early-returns; no retry loop,
  no repeated toast. Copying a *different* folder notifies again.
- Generalise the fix: any other `read_bytes` failure (permissions, file
  vanished) also updates the fingerprint so no failure mode can loop.

## Architecture

`ClipboardWatcher` does not know about `config.no_notify`, and must not.
Add an optional constructor callback `on_file_skipped(reason: str)`
(async), mirroring the existing `on_change` boundary. `run()` wires it
to `notify_async` only when notifications are enabled.

ZIP-and-send and auto-extract alternatives were considered and rejected
by the user: skip-only keeps scope minimal and protocol unchanged.

## Testing

New `tests/test_clipboard_watcher.py`, with module-level clipboard
helpers (`grab_clipboard_files`, `grab_clipboard_image`,
`pyperclip.paste`) monkeypatched so no real clipboard access happens:

1. Directory on clipboard → `on_change` NOT called, `on_file_skipped`
   called once, fingerprint updated.
2. Second poll with same directory → no further callback (no loop).
3. Regular file → `on_change("file", (name, bytes))` still fires.
4. Unreadable path (OSError) → fingerprint updated, no retry loop.
