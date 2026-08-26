# Folder Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Copying a folder syncs its full subfolder tree to the peer's `received/` directory, in all three implementations, with graceful flattening on older peers.

**Architecture:** Extend the existing `kind:"files"` clip with an optional per-entry `path` field (protocol minor 2 → 3, capability marker only — no send gate). Senders expand folders into path-tagged entries (per-folder all-or-nothing against the existing budget); receivers that understand `path` rebuild the tree under `received/` with strict traversal-proof validation, while older receivers ignore the unknown field and write flat exactly as today. Spec: `docs/superpowers/specs/2026-08-26-folder-sync-design.md`.

**Tech Stack:** Python 3.12 (asyncio, canonical wire), Swift 6 (AnyClipCore/AnyClipDaemon), C#/.NET 8 (AnyClipCore/AnyClipApp), shared golden vectors + fake_peer interop.

**Execution order:** Tasks 1→10 strictly in order (Python wire first — the golden-vector generator change it makes is consumed by Task 4's regeneration; C# golden tests consume Task 4's committed fixtures).

## Global Constraints

- Protocol minor 2 → 3: `PROTOCOL_MINOR` (anyclip.py) / `Wire.protocolMinor` (Swift) / `Wire.ProtocolMinor` (C#) = 3. Cumulative semantics comment: ≥1 files, ≥2 64 MiB frames, ≥3 rebuilds folder trees. Minor 3 gates NOTHING on the send path (capability marker only).
- File cap 100 → 500: `MAX_FILES_PER_CLIP` / `maxFilesPerClip` / `MaxFilesPerClip` = 500. `FILE_BUDGET`/`fileBudget`/`FileBudget` stays 49,466,572 (formula untouched). The v1.3.0 per-link 64 MiB legacy gate and size-scaled send timeout are untouched.
- Wire `path` rules (sender MUST emit, receiver MUST verify): POSIX `/` separators; NFC; relative; no leading `/`; no drive letters; no `.` or `..` segments; no empty segments; no backslashes; last segment equals the entry's `name`; ≤ 32 segments; sanitized total length ≤ 240 chars. Loose files carry NO `path` field — every existing frame stays byte-identical.
- Entry shapes (cross-task interface, pinned): Python entry = `(name: str, data: bytes, relpath: str | None)`; Swift `ClipPayload.files` associated value = `[(name: String, data: Data, relPath: String?)]`; C# = `record FileEntry(string Name, byte[] Data, string? RelPath)`.
- Sender folder expansion: recursive walk, files only, deterministic byte-wise sort on relative path; exclude `.DS_Store`, `Thumbs.db`, `desktop.ini` and symlinks (log only, never followed); empty dirs dropped. Selection items processed in selection order; per-folder ALL-OR-NOTHING against remaining budget/count; loose files keep today's greedy behavior.
- Pinned user-facing strings: toast `folder too large to sync: <name>`; toast `folder is empty; nothing to sync`; log once per clip per affected link `peer <name> will flatten folders (protocol < 1.3)`. The old `folder on clipboard not synced (unsupported)` warning path is REPLACED by expansion.
- Minor-0 (protocol 1.0) links: folder-derived entries are EXCLUDED from the first-file `kind:"file"` fallback; a folder-only clip sends nothing on that link (log only). Minor 1–2 links receive the SAME files frame (receiver flattens benignly).
- Receiver: entries without `path` behave exactly as today. Entries with `path`: validate ALL wire rules; on ANY violation fall back to flat placement (sanitized `name`) for THAT entry — never drop the frame, never write outside `received/`; verify the resolved destination stays under `received/` after sanitization; create intermediate dirs; sanitize + NFC each segment with the existing per-name sanitizer. Top-segment collision: uniquify (`<top>-2`, …) with the SAME replacement applied to every entry sharing that top segment within the clip. Clipboard placement: top-level items in batch order (folders once + loose files); Swift NSPasteboard all items; C# and Python/Windows CF_HDROP all items; Python/macOS first top-level item only (existing limitation).
- Golden vectors: `formacOS/Scripts/gen-golden-vectors.py` gains a files-with-path vector (Task 1, edit only — never run there); fixtures regenerated + committed ONCE in Task 4; both native `GoldenVectorTests` assert the new vector. `formacOS/Scripts/fake_peer.py` stays UNMODIFIED (minor 0).
- Never touch CLAUDE.md. Do not push. Commits end with:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- Suites: `.venv/bin/python -m pytest tests/ -q` · `swift test --package-path formacOS` · `dotnet test forwindows/tests/AnyClipCore.Tests` (PATH needs `$HOME/.dotnet`; AnyClipApp.Tests are Windows-CI-only). Target release v1.4.0.

---

### Task 1: Python wire — protocol minor 3, cap 500, optional entry `path`

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/anyclip.py`
  - constants block (lines 62–88: `PROTOCOL_MINOR`, `MAX_FILES_PER_CLIP`)
  - `decode_files_payload` (lines 384–414)
  - new helpers after `uniquify_names` (line 966)
  - `ClipboardWatcher._check_file_clipboard` accept site (line 1137)
  - `ClipboardWatcher.update_local_files` unpack sites (lines 1240, 1245–1247)
  - `build_clip_payload` `"files"` branch (lines 1686–1715)
  - `LinkManager.broadcast_files` fallback encode (lines 2103–2105)
  - `send_files_to_link` (lines 2563–2564), `emit_files_clip` (line 2576)
  - `on_remote_clip` files branch (line 2644), `on_local_change` files branch (line 2715)
- Modify: `/Users/seojeonghwa/project/AnyClip/formacOS/Scripts/gen-golden-vectors.py`
- Test (modify): `/Users/seojeonghwa/project/AnyClip/tests/test_wire_files.py`,
  `/Users/seojeonghwa/project/AnyClip/tests/test_golden_files.py`,
  `/Users/seojeonghwa/project/AnyClip/tests/test_large_frames.py` (1 assertion line),
  `/Users/seojeonghwa/project/AnyClip/tests/test_clipboard_watcher.py` (4 assertion lines)

**Interfaces:**

Consumes (verified present today):
- `sha256_bytes(data: bytes) -> str` (anyclip.py:371), `aggregate_files_hash(hashes: list) -> str` (:375)
- `decode_files_payload(msg: dict) -> Optional[list]` (:384)
- `sanitize_filename(name: str) -> str` (:917), `uniquify_names(names: list) -> list` (:943)
- `build_clip_payload(kind: str, content) -> Optional[dict]` (:1643)
- `files_variant_for_link(link) -> str` (:1381), `link_accepts_frame(link, nbytes: int) -> bool` (:1371)
- `LinkManager.broadcast_files(self, data) -> tuple` (:2087), `LinkManager._gate(self, link, frame, skipped) -> bool` (:2048)
- `send_files_to_link(link, data) -> tuple` (:2549), `emit_files_clip(link, suppressor, data) -> tuple` (:2568)
- `ClipboardWatcher.update_local_files(self, files: list) -> int` (:1231)
- `PeerLink.send_clip(self, kind: str, content) -> None` (:1631), `encode_frame(obj) -> bytes`

Produces (later tasks + the Swift/C# drafters rely on these):
- `PROTOCOL_MINOR = 3`, `MAX_FILES_PER_CLIP = 500` (`FILE_BUDGET` untouched at 49,466,572)
- `MAX_PATH_SEGMENTS = 32`, `MAX_PATH_CHARS = 240`
- `sanitize_relpath(path: str) -> str`
- `is_valid_wire_path(path, name: str) -> bool`
- `entry_relpath(entry) -> Optional[str]` — tolerates the legacy 2-tuple shape
- `decode_files_payload` now returns `[(name: str, data: bytes, relpath: str | None), ...]`
- `build_clip_payload("files", entries)` accepts 2- **or** 3-tuples; **pinned entry key order
  `["name", "content", "hash", "bytes", "path"]`** — `path` is appended last and omitted entirely
  for loose files, so every 1.3.0 frame stays byte-identical
- `gen-golden-vectors.py`: `FILES_WITH_PATH`, `vectors() -> dict`, `manifest() -> dict`,
  new fixture name `clip_files_path.bin`, manifest keys `files_path_names`, `files_path_paths`,
  `files_path_hashes`, `files_path_aggregate`, `files_path_total_bytes`, and `hello.bin` moving to
  `protocol_minor: 3` — the one EXISTING fixture whose bytes change, which obliges Task 4 (Swift wire)
  to update `formacOS/Tests/AnyClipCoreTests/GoldenVectorTests.swift:37` when it regenerates

Steps:

- [ ] **Step 1: Write the failing constant + validation tests.** In `tests/test_wire_files.py`, extend the imports to
  ```python
  from __future__ import annotations

  import asyncio
  import hashlib
  import unicodedata

  import pytest

  import anyclip
  ```
  then REPLACE `test_protocol_minor_covers_files_and_64mib_frames` (lines 29–32) with:
  ```python
  def test_protocol_minor_is_three_and_cap_is_five_hundred():
      # Cumulative feature level: >= 1 accepts kind:"files", >= 2 accepts 64 MiB
      # frames (tests/test_large_frames.py), >= 3 rebuilds folder trees from the
      # per-entry "path".
      assert anyclip.PROTOCOL_MINOR == 3
      assert anyclip.MAX_FILES_PER_CLIP == 500
      assert anyclip.FILE_BUDGET == 49_466_572  # formula untouched


  @pytest.mark.parametrize("path,name", [
      ("docs/a.txt", "a.txt"),
      ("docs/sub dir/a.txt", "a.txt"),
      ("보고서/1분기/요약.pdf", "요약.pdf"),
      ("a.txt", "a.txt"),                                # single segment is legal
      ("/".join(["d"] * 31 + ["a.txt"]), "a.txt"),       # exactly 32 segments
  ])
  def test_valid_wire_paths(path, name):
      assert anyclip.is_valid_wire_path(path, name)


  @pytest.mark.parametrize("path,name", [
      ("/docs/a.txt", "a.txt"),                          # absolute
      ("../a.txt", "a.txt"),                             # traversal
      ("docs/../a.txt", "a.txt"),
      ("docs/./a.txt", "a.txt"),
      ("docs//a.txt", "a.txt"),                          # empty segment
      ("docs\\a.txt", "a.txt"),                          # backslash
      ("C:/docs/a.txt", "a.txt"),                        # drive letter
      ("docs/a.txt", "b.txt"),                           # last segment != name
      ("", "a.txt"),
      (None, "a.txt"),
      (42, "a.txt"),
      ("/".join(["d"] * 32 + ["a.txt"]), "a.txt"),       # 33 segments
      ("d/" + "x" * 240 + ".txt", "x" * 240 + ".txt"),   # sanitized length > 240
  ])
  def test_invalid_wire_paths(path, name):
      assert not anyclip.is_valid_wire_path(path, name)


  def test_only_nfc_paths_are_accepted_on_the_wire():
      nfc = "보고서/요약.pdf"
      nfd = unicodedata.normalize("NFD", nfc)
      assert nfd != nfc
      assert anyclip.is_valid_wire_path(nfc, "요약.pdf")
      assert not anyclip.is_valid_wire_path(
          nfd, unicodedata.normalize("NFD", "요약.pdf"))


  def test_sanitize_relpath_is_per_segment():
      assert anyclip.sanitize_relpath("docs/con/a:b.txt") == "docs/_con/a_b.txt"
      assert anyclip.sanitize_relpath(
          unicodedata.normalize("NFD", "보고서/요약.pdf")) == "보고서/요약.pdf"
  ```
  `tests/test_wire_files.py` is not the only place the old minor is pinned. In
  `tests/test_large_frames.py`, replace line 99 inside
  `test_frame_caps_and_protocol_minor` (`assert anyclip.PROTOCOL_MINOR == 2`) with:
  ```python
      # Cumulative feature level: >= 1 files, >= 2 64 MiB frames (this file),
      # >= 3 rebuilds folder trees from the per-entry "path".
      assert anyclip.PROTOCOL_MINOR == 3
  ```
  Leave the two frame-cap assertions above it and
  `test_file_budget_formula_unchanged_against_new_cap` (which pins
  `FILE_BUDGET == 49466572` and its formula) untouched — the budget does not move.

- [ ] **Step 2: Run them, expect FAIL** — `is_valid_wire_path` / `sanitize_relpath` do not exist yet
  (`AttributeError: module 'anyclip' has no attribute 'is_valid_wire_path'`) and `PROTOCOL_MINOR` is still 2.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_wire_files.py -q
  ```

- [ ] **Step 3: Bump the constants.** In `anyclip.py`, replace the comment + constants at lines 65–73:
  ```python
  # with releases. PROTOCOL_MAJOR/MINOR are independent: PROTOCOL_MAJOR is the
  # wire-compat key (mismatch = refuse link), PROTOCOL_MINOR is a cumulative
  # feature level: minor >= 1 accepts kind:"files", minor >= 2 accepts frames
  # up to MAX_PAYLOAD (64 MiB) instead of the legacy 16 MiB, minor >= 3
  # rebuilds folder trees from the optional per-entry "path" field. Minor 3 is
  # a capability MARKER only -- it gates nothing on the send path.
  # CI exports ANYCLIP_BUILD_VERSION from the git tag (without leading "v");
  # local source runs default to a dev marker so handshake logs stay readable.
  APP_VERSION = os.environ.get("ANYCLIP_BUILD_VERSION", "0.0.0-dev")
  PROTOCOL_MAJOR = 1
  PROTOCOL_MINOR = 3
  ```
  and replace lines 86–87:
  ```python
  # Sender-side cap on files per clip; the receiver stays lenient. Raised
  # 100 -> 500 in 1.4.0 because document trees pass 100 easily; FILE_BUDGET
  # is the real limit and its formula is untouched.
  MAX_FILES_PER_CLIP = 500
  ```

- [ ] **Step 4: Add the path helpers.** In `anyclip.py`, insert after `uniquify_names` (after line 966, before `class ClipboardWatcher`):
  ```python
  # Wire "path" limits for folder entries (protocol 1.3). Keep in lockstep
  # with Swift WireMessage and C# WireMessage.
  MAX_PATH_SEGMENTS = 32
  MAX_PATH_CHARS = 240


  def sanitize_relpath(path: str) -> str:
      """Per-segment sanitize_filename() over a wire "path", rejoined with '/'.
      Used for the length rule on both sides and for the on-disk destination."""
      return "/".join(sanitize_filename(seg) for seg in path.split("/"))


  def is_valid_wire_path(path, name: str) -> bool:
      """True when ``path`` obeys EVERY folder-entry rule of protocol 1.3:

      POSIX '/' separators, NFC, relative (no leading '/', no drive letter, no
      '.'/'..' segments, no empty segments, no backslashes), last segment equal
      to the entry's ``name``, at most MAX_PATH_SEGMENTS segments, and a
      sanitized total length of at most MAX_PATH_CHARS characters.

      The sender emits nothing else; the receiver verifies before touching the
      filesystem and falls back to flat placement for a violating entry. Keep
      in lockstep with Swift isValidWirePath and C# IsValidWirePath."""
      if not isinstance(path, str) or not path:
          return False
      if path != unicodedata.normalize("NFC", path):
          return False
      if "\\" in path or path.startswith("/"):
          return False
      if len(path) > 1 and path[1] == ":" and path[0].isalpha():
          return False
      segments = path.split("/")
      if len(segments) > MAX_PATH_SEGMENTS:
          return False
      for seg in segments:
          if not seg or seg in (".", ".."):
              return False
      if segments[-1] != name:
          return False
      return len(sanitize_relpath(path)) <= MAX_PATH_CHARS


  def entry_relpath(entry) -> Optional[str]:
      """The folder path of a files-clip entry, or None for a loose file.

      Canonical entry shape is (name, data, relpath|None); the 2-tuple
      (name, data) built by older call sites is read as "loose"."""
      return entry[2] if len(entry) == 3 else None
  ```

- [ ] **Step 5: Run them, expect PASS.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_wire_files.py -q
  ```

- [ ] **Step 6: Write the failing decode/encode tests.** In `tests/test_wire_files.py`, replace
  `test_decode_files_payload_valid` (lines 43–48) with the 3-tuple version and append the rest:
  ```python
  def test_decode_files_payload_valid():
      msg = {"type": "clip", "kind": "files", "files": [
          {"name": "a", "content": "YWxwaGE=", "hash": "x", "bytes": 5},
          {"name": "b", "content": "YmV0YQ==", "hash": "y", "bytes": 4},
      ]}
      assert anyclip.decode_files_payload(msg) == [
          ("a", b"alpha", None), ("b", b"beta", None),
      ]


  def test_decode_files_payload_keeps_a_valid_path():
      msg = {"type": "clip", "kind": "files", "files": [
          {"name": "a.txt", "content": "YWxwaGE=", "hash": "x", "bytes": 5,
           "path": "docs/a.txt"},
          {"name": "b.txt", "content": "YmV0YQ==", "hash": "y", "bytes": 4},
      ]}
      assert anyclip.decode_files_payload(msg) == [
          ("a.txt", b"alpha", "docs/a.txt"),
          ("b.txt", b"beta", None),
      ]


  @pytest.mark.parametrize("bad", [
      "../evil.txt", "/etc/evil.txt", "C:/evil.txt", "docs\\evil.txt", 7,
  ])
  def test_decode_files_payload_falls_back_to_flat_on_a_bad_path(bad):
      """A violating path NEVER drops the frame -- that one entry goes flat."""
      msg = {"type": "clip", "kind": "files", "files": [
          {"name": "evil.txt", "content": "YWxwaGE=", "hash": "x", "bytes": 5,
           "path": bad},
      ]}
      assert anyclip.decode_files_payload(msg) == [("evil.txt", b"alpha", None)]


  def test_send_clip_files_emits_path_last_and_only_when_valid():
      async def go():
          link, sent = _capture_link()
          data = [
              ("a.txt", b"alpha", "docs/a.txt"),
              ("b.txt", b"beta", None),
              ("c.txt", b"gamma", "../c.txt"),   # invalid -> field omitted
          ]
          await link.send_clip("files", data)
          entries = sent[0]["files"]
          assert list(entries[0].keys()) == [
              "name", "content", "hash", "bytes", "path"]
          assert entries[0]["path"] == "docs/a.txt"
          assert list(entries[1].keys()) == ["name", "content", "hash", "bytes"]
          assert "path" not in entries[2]
      asyncio.run(go())


  def test_two_tuple_entries_still_encode_byte_identically():
      """Loose-file clips keep the exact 1.3.0 wire shape (golden vectors)."""
      async def go():
          link, sent = _capture_link()
          await link.send_clip("files", [("a.bin", b"alpha"), ("b.bin", b"beta")])
          payload = sent[0]
          assert list(payload.keys()) == [
              "type", "kind", "files", "hash", "ts", "bytes"]
          for ent in payload["files"]:
              assert list(ent.keys()) == ["name", "content", "hash", "bytes"]
      asyncio.run(go())
  ```

- [ ] **Step 7: Run them, expect FAIL** — decode still returns 2-tuples
  (`assert [('a', b'alpha')] == [('a', b'alpha', None)]`) and `build_clip_payload` rejects 3-tuples
  (`len(content) != 2` → returns None → `IndexError`/empty `sent`).
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_wire_files.py -q
  ```

- [ ] **Step 8: Decode the optional path.** In `anyclip.py`, replace the docstring + tail of
  `decode_files_payload` (lines 385–414):
  ```python
      """Decode a kind:"files" clip into [(name, raw_bytes, relpath|None), ...].

      Strict: any entry with a missing/non-str/non-strict-base64 ``content``,
      a non-object entry, or an empty/missing ``files`` array returns None so
      the caller drops the WHOLE frame (no partial apply). Names come straight
      off the wire (already NFC per the sender); sanitization happens on write.
      The optional ``path`` (protocol 1.3) is verified against every wire rule
      here: a violating path is downgraded to None -- that entry lands flat --
      and NEVER drops the frame. Wire hashes are never trusted -- recomputed
      downstream from decoded bytes."""
      entries = msg.get("files")
      if not isinstance(entries, list) or not entries:
          log.warning("ignoring files clip: empty or non-list 'files' array")
          return None
      decoded: list = []
      for ent in entries:
          if not isinstance(ent, dict):
              log.warning("ignoring files clip: non-object entry")
              return None
          content = ent.get("content")
          if not isinstance(content, str):
              log.warning("ignoring files clip: entry missing string 'content'")
              return None
          try:
              raw = base64.b64decode(content, validate=True)
          except Exception as exc:
              log.warning(f"ignoring files clip: bad base64 entry ({exc})")
              return None
          name = ent.get("name")
          if not isinstance(name, str) or not name:
              name = "received.bin"
          relpath = ent.get("path")
          if relpath is not None and not is_valid_wire_path(relpath, name):
              log.warning(
                  f"files clip: rejecting path {relpath!r} for {name!r}; "
                  "placing that file flat"
              )
              relpath = None
          decoded.append((name, raw, relpath))
      return decoded
  ```

- [ ] **Step 9: Encode the optional path.** In `anyclip.py`, replace the `"files"` branch of
  `build_clip_payload` (lines 1686–1707) with:
  ```python
      elif kind == "files":
          if not isinstance(content, list) or not content:
              return
          files_arr = []
          hashes = []
          total = 0
          for ent in content:
              if not isinstance(ent, tuple) or len(ent) not in (2, 3):
                  return
              name, raw = ent[0], ent[1]
              relpath = entry_relpath(ent)
              if not isinstance(name, str) or not isinstance(raw, (bytes, bytearray)):
                  return
              raw_b = bytes(raw)
              h = sha256_bytes(raw_b)
              entry = {
                  "name": name,
                  "content": base64.b64encode(raw_b).decode("ascii"),
                  "hash": h,
                  "bytes": len(raw_b),
              }
              # Optional folder path (protocol 1.3), appended LAST and emitted
              # only when it obeys every wire rule -- so a loose file's entry
              # stays byte-identical to what 1.3.0 sent and a malformed local
              # path degrades to flat placement instead of poisoning the frame.
              if relpath is not None:
                  if is_valid_wire_path(relpath, name):
                      entry["path"] = relpath
                  else:
                      log.warning(
                          f"dropping invalid folder path {relpath!r} for {name!r}")
              files_arr.append(entry)
              hashes.append(h)
              total += len(raw_b)
  ```

- [ ] **Step 10: Run them, expect PASS.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_wire_files.py -q
  ```

- [ ] **Step 11: Thread the 3-tuple shape through the remaining call sites.** Five call sites (six
  edits) in `anyclip.py`, all index-based so the legacy 2-tuple shape keeps working:
  1. `ClipboardWatcher._check_file_clipboard` — TWO edits that MUST land together, because
     widening `accepted` also widens what the single-file dispatch hands to `on_change`.
     Line 1137: `accepted.append((name, data))` →
     ```python
             accepted.append((name, data, None))
     ```
     and line 1160 (`await self.on_change("file", accepted[0])`), which would otherwise start
     emitting a 3-tuple on the ONE-file path — breaking
     `tests/test_clipboard_watcher.py:108` / `:298` / `:343` and tripping
     `assert isinstance(data, tuple) and len(data) == 2` in `on_local_change` (anyclip.py:2696)
     so no single file is ever sent again — becomes:
     ```python
                 await self.on_change("file", (accepted[0][0], accepted[0][1]))
     ```
     (the exact form Task 2 Step 7 keeps when it rewrites the whole method). The
     `self._last_file_hash = sha256_bytes(accepted[0][1])` line above it is already
     index-based and needs no change.
  2. `ClipboardWatcher.update_local_files` (lines 1240 and 1245–1247):
     ```python
          names = uniquify_names([sanitize_filename(ent[0]) for ent in files])
     ```
     ```python
              for safe, ent in zip(names, files):
                  target = target_dir / safe
                  target.write_bytes(bytes(ent[1]))
     ```
  3. `LinkManager.broadcast_files` fallback encode — replace lines 2103–2105
     (`first_name, first_raw = data[0]` and the two payload lines) with:
     ```python
                      payload = build_clip_payload(
                          "file", (data[0][0], bytes(data[0][1])))
     ```
  4. `send_files_to_link` — replace lines 2563–2564
     (`first_name, first_raw = data[0]` and the send) with:
     ```python
      await link.send_clip("file", (data[0][0], bytes(data[0][1])))
     ```
  5. the three aggregate-hash comprehensions — `emit_files_clip` (line 2576),
     `on_remote_clip` files branch (line 2644), `on_local_change` files branch (line 2715) — all become:
     ```python
      hashes = [sha256_bytes(bytes(ent[1])) for ent in data]
     ```

- [ ] **Step 12: Update the watcher's emitted-shape assertions.** In `tests/test_clipboard_watcher.py`,
  add the third tuple element to the four `("files", [...])` expectations
  (lines 205, 230, 253, 278):
  ```python
      assert changes == [("files", [("a.txt", b"one", None), ("b.txt", b"two", None)])]
  ```
  ```python
      assert changes == [("files", [("a.txt", b"one", None), ("b.txt", b"two", None)])]
  ```
  ```python
      assert changes == [("files", [("a.txt", b"123456", None), ("c.txt", b"XY", None)])]
  ```
  ```python
      assert changes == [("files", [("a.bin", b"a" * 6, None), ("b.bin", b"b" * 6, None)])]
  ```

- [ ] **Step 13: Run the whole suite, expect PASS** — baseline today is `145 passed, 1 skipped`,
  so expect that count plus the new cases. Both places that pinned the old minor
  (`tests/test_wire_files.py`, `tests/test_large_frames.py:99`) were updated in Step 1, and both
  `on_change` dispatch shapes were updated in Step 11, so nothing may fail here.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/ -q
  ```

- [ ] **Step 14: Write the failing golden-generator test.** Append to `tests/test_golden_files.py`
  (and extend its imports with `import importlib.util` and `import unicodedata`):
  ```python
  def _generator():
      """Import formacOS/Scripts/gen-golden-vectors.py as a module. Module-level
      code only computes constants (main() is behind __main__), so importing it
      never writes to the committed Fixtures directory."""
      path = (pathlib.Path(__file__).resolve().parent.parent
              / "formacOS" / "Scripts" / "gen-golden-vectors.py")
      spec = importlib.util.spec_from_file_location("gen_golden_vectors", path)
      mod = importlib.util.module_from_spec(spec)
      spec.loader.exec_module(mod)
      return mod


  def test_files_with_path_vector_matches_the_canonical_encoder():
      """The new golden frame must be exactly what anyclip's encoder produces --
      Python is the canonical source both native suites assert against."""
      gen = _generator()
      obj = gen.vectors()["clip_files_path.bin"]
      canonical = anyclip.build_clip_payload("files", [
          (unicodedata.normalize("NFC", n), b,
           None if p is None else unicodedata.normalize("NFC", p))
          for n, b, p in gen.FILES_WITH_PATH
      ])
      assert list(obj.keys()) == list(canonical.keys())
      assert obj["files"] == canonical["files"]
      assert obj["hash"] == canonical["hash"]
      assert obj["bytes"] == canonical["bytes"]
      # Folder entries carry "path" LAST; the loose entry carries none at all.
      assert list(obj["files"][0].keys()) == [
          "name", "content", "hash", "bytes", "path"]
      assert anyclip.is_valid_wire_path(
          obj["files"][0]["path"], obj["files"][0]["name"])
      assert obj["files"][1]["path"].count("/") == 2   # nested subdirectory
      assert "path" not in obj["files"][2]             # loose file in the clip


  def test_hello_vector_advertises_the_current_protocol_minor():
      """The spec calls for the hello fixture to move to minor 3 alongside the
      new files-with-path vector; pin it here so the regeneration in the Swift
      task cannot silently ship the stale 0."""
      assert _generator().vectors()["hello.bin"]["protocol_minor"] == 3
      assert anyclip.PROTOCOL_MINOR == 3


  def test_files_with_path_manifest_records_the_same_paths():
      gen = _generator()
      man = gen.manifest()
      obj = gen.vectors()["clip_files_path.bin"]
      assert man["files_path_paths"] == [
          ent.get("path") for ent in obj["files"]]
      assert man["files_path_names"] == [ent["name"] for ent in obj["files"]]
      assert man["files_path_aggregate"] == obj["hash"]
      assert man["files_path_total_bytes"] == obj["bytes"]
  ```

- [ ] **Step 15: Run it, expect FAIL** — `AttributeError: module 'gen_golden_vectors' has no attribute 'vectors'`.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_golden_files.py -q
  ```

- [ ] **Step 16: Add the files-with-path vector to the generator (do NOT run the script).**
  In `formacOS/Scripts/gen-golden-vectors.py`, add after `FILES` (line 27):
  ```python
  # One folder tree (Korean top folder + a nested subdir) PLUS one loose file in
  # the SAME clip, so the vector pins both entry shapes: with "path" and without.
  FILES_WITH_PATH = [
      ("메모.txt", b"golden tree one \x00", "보고서/메모.txt"),
      ("réport (v2).bin", b"golden tree two \x01", "보고서/sub dir/réport (v2).bin"),
      ("loose.txt", b"golden loose \x02", None),
  ]
  ```
  add after `frame()` (line 37):
  ```python
  def _entry(name: str, body: bytes, path) -> dict:
      """One files-clip entry in the canonical key order; "path" is appended
      last and omitted entirely for a loose file."""
      ent = {
          "name": unicodedata.normalize("NFC", name),
          "content": base64.b64encode(body).decode("ascii"),
          "hash": hashlib.sha256(body).hexdigest(),
          "bytes": len(body),
      }
      if path is not None:
          ent["path"] = unicodedata.normalize("NFC", path)
      return ent
  ```
  then replace `main()` (lines 39–101) with `vectors()` / `manifest()` / a thin `main()`:
  ```python
  def vectors() -> dict:
      """Every golden frame, keyed by fixture filename. Pure: main() writes."""
      return {
          # protocol_minor 0 -> 3: the spec moves the hello fixture to the
          # current feature level along with the new files-with-path vector.
          "hello.bin": {
              "type": "hello", "token": TOKEN_HASH, "node_id": NODE_ID,
              "name": "golden-mac", "version": 1, "app_version": "1.0.0",
              "protocol_major": 1, "protocol_minor": 3,
          },
          "clip_text.bin": {
              "type": "clip", "kind": "text", "content": TEXT,
              "hash": hashlib.sha256(TEXT.encode("utf-8")).hexdigest(), "ts": TS,
          },
          "clip_image.bin": {
              "type": "clip", "kind": "image",
              "content": base64.b64encode(IMAGE_BYTES).decode("ascii"),
              "hash": hashlib.sha256(IMAGE_BYTES).hexdigest(), "ts": TS,
              "bytes": len(IMAGE_BYTES),
          },
          "clip_file.bin": {
              "type": "clip", "kind": "file", "name": FILE_NAME,
              "content": base64.b64encode(FILE_BYTES).decode("ascii"),
              "hash": hashlib.sha256(FILE_BYTES).hexdigest(), "ts": TS,
              "bytes": len(FILE_BYTES),
          },
          "clip_files.bin": {
              "type": "clip", "kind": "files",
              "files": [_entry(n, b, None) for n, b in FILES],
              "hash": _agg([hashlib.sha256(b).hexdigest() for _n, b in FILES]),
              "ts": TS,
              "bytes": sum(len(b) for _n, b in FILES),
          },
          "clip_files_path.bin": {
              "type": "clip", "kind": "files",
              "files": [_entry(n, b, p) for n, b, p in FILES_WITH_PATH],
              "hash": _agg([hashlib.sha256(b).hexdigest()
                            for _n, b, _p in FILES_WITH_PATH]),
              "ts": TS,
              "bytes": sum(len(b) for _n, b, _p in FILES_WITH_PATH),
          },
          "ping.bin": {"type": "ping", "ts": TS},
      }


  def manifest() -> dict:
      return {
          "token": TOKEN, "token_hash": TOKEN_HASH, "node_id": NODE_ID,
          "text": TEXT,
          "text_hash": hashlib.sha256(TEXT.encode("utf-8")).hexdigest(),
          "image_b64": base64.b64encode(IMAGE_BYTES).decode("ascii"),
          "image_hash": hashlib.sha256(IMAGE_BYTES).hexdigest(),
          "file_name": FILE_NAME,
          "file_b64": base64.b64encode(FILE_BYTES).decode("ascii"),
          "file_hash": hashlib.sha256(FILE_BYTES).hexdigest(),
          "files_names": [unicodedata.normalize("NFC", n) for n, _ in FILES],
          "files_hashes": [hashlib.sha256(b).hexdigest() for _n, b in FILES],
          "files_aggregate": _agg(
              [hashlib.sha256(b).hexdigest() for _n, b in FILES]),
          "files_total_bytes": sum(len(b) for _n, b in FILES),
          "files_path_names": [unicodedata.normalize("NFC", n)
                               for n, _b, _p in FILES_WITH_PATH],
          "files_path_paths": [None if p is None
                               else unicodedata.normalize("NFC", p)
                               for _n, _b, p in FILES_WITH_PATH],
          "files_path_hashes": [hashlib.sha256(b).hexdigest()
                                for _n, b, _p in FILES_WITH_PATH],
          "files_path_aggregate": _agg([hashlib.sha256(b).hexdigest()
                                        for _n, b, _p in FILES_WITH_PATH]),
          "files_path_total_bytes": sum(len(b) for _n, b, _p in FILES_WITH_PATH),
          "ts": TS,
      }


  def main() -> None:
      OUT.mkdir(parents=True, exist_ok=True)
      frames = vectors()
      for fname, obj in frames.items():
          (OUT / fname).write_bytes(frame(obj))
      (OUT / "manifest.json").write_text(
          json.dumps(manifest(), ensure_ascii=False, indent=2), encoding="utf-8")
      print(f"wrote {len(frames) + 1} fixtures to {OUT}")
  ```
  The committed fixtures are NOT regenerated here — Task 4 (Swift wire) runs the script once and commits
  the new `clip_files_path.bin` plus the refreshed `manifest.json`.

  **Hand-off to Task 4 (Swift wire), because this generator edit changes an EXISTING fixture:**
  `hello.bin` moves from `protocol_minor` 0 to 3 (spec, "Wire format" and "Testing"), so its bytes
  change when the script runs. Task 4 must therefore also flip
  `formacOS/Tests/AnyClipCoreTests/GoldenVectorTests.swift:37`
  (`#expect(m.protocol_minor == 0)` → `== 3`) in the same commit as the regenerated fixtures, or
  `goldenHelloDecodes` fails. The C# counterpart needs NOTHING here:
  `forwindows/tests/AnyClipCore.Tests/GoldenVectorTests.cs` `GoldenHelloDecodes` (lines 31–42)
  asserts type/token/node_id/version/`ProtocolMajor` only — it never reads `protocol_minor`.
  All four clip vectors and `ping.bin` are byte-identical to what is committed today (the loose-file
  encoder path is unchanged), so `clip_files_path.bin` is the only ADDED fixture.
  `formacOS/Scripts/fake_peer.py` stays UNMODIFIED at minor 0 — it is a peer, not a fixture.

- [ ] **Step 17: Run the whole suite, expect PASS.** The generator now says `protocol_minor: 3`
  while the COMMITTED `hello.bin` still says 0; that divergence is deliberate and invisible to
  pytest — the new tests read `gen.vectors()` in memory, and `tests/test_golden_files.py` only ever
  opens `clip_files.bin` + `manifest.json`, never `hello.bin`. Task 4 closes the gap by
  regenerating.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/ -q
  ```

- [ ] **Step 18: Commit.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && git add anyclip.py formacOS/Scripts/gen-golden-vectors.py tests/test_wire_files.py tests/test_golden_files.py tests/test_large_frames.py tests/test_clipboard_watcher.py && git commit -m "$(cat <<'EOF'
feat(wire-py): protocol minor 3 + optional folder "path" on files entries

PROTOCOL_MINOR 2 -> 3 (cumulative: >=1 files, >=2 64 MiB frames, >=3 rebuilds
folder trees; minor 3 gates nothing on the send path) and MAX_FILES_PER_CLIP
100 -> 500. FILE_BUDGET and the per-link 64 MiB legacy gate are untouched.

Entries become (name, data, relpath|None). The encoder appends "path" last and
only when it obeys every wire rule, so loose-file frames stay byte-identical to
1.3.0; the decoder verifies it and downgrades a violating path to flat
placement for that entry instead of dropping the frame.

gen-golden-vectors.py gains a files-with-path vector (folder tree + loose file
in one clip) and moves the hello vector to minor 3; fixtures are regenerated in
the Swift wire task, which also flips the hello assertion in the Swift
GoldenVectorTests.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
  ```

---

### Task 2: Python sender — folder expansion, all-or-nothing, minor-0 exclusion

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/anyclip.py`
  - new `expand_folder` / `scan_selection` / `fingerprint_paths` / `folder_fits` before `class ClipboardWatcher` (line 969)
  - `ClipboardWatcher.__init__` fingerprint seed (lines 1006–1015)
  - `ClipboardWatcher._check_file_clipboard` (lines 1087–1167) — replaces the folder-skip path
  - new `clip_has_folder_entries` / `first_loose_entry` after `files_variant_for_link` (line 1385)
  - `LinkManager.broadcast_files` (lines 2087–2129)
  - `send_files_to_link` (lines 2549–2565) + `emit_files_clip` docstring (lines 2568–2575)
- Create (test): `/Users/seojeonghwa/project/AnyClip/tests/test_folder_walk.py`
- Test (modify): `/Users/seojeonghwa/project/AnyClip/tests/test_clipboard_watcher.py`,
  `/Users/seojeonghwa/project/AnyClip/tests/test_link_manager.py`,
  `/Users/seojeonghwa/project/AnyClip/tests/test_receive_files.py`

**Interfaces:**

Consumes: `entry_relpath(entry) -> Optional[str]`, `is_valid_wire_path(path, name) -> bool`,
`MAX_FILES_PER_CLIP`, `FILE_BUDGET` (Task 1); `grab_clipboard_files() -> list` (anyclip.py:447);
`ClipboardWatcher._notify_file_skipped(self, message: str) -> None` (:1169);
`files_variant_for_link(link) -> str` (:1381); `LinkManager._gate(self, link, frame, skipped) -> bool` (:2048);
`build_clip_payload(kind, content) -> Optional[dict]` (:1643); `encode_frame(obj) -> bytes`.

Produces (Task 3 consumes `fingerprint_paths`; Tasks 5/8 mirror the semantics):
- `FOLDER_JUNK_NAMES = {".DS_Store", "Thumbs.db", "desktop.ini"}`
- `expand_folder(path: str) -> list` → `[(abs_path: str, size: int, mtime_ns: int, relpath: str), ...]`,
  sorted byte-wise on `relpath`, `relpath` starting with the folder's own NFC name
- `folder_fits(entries: list, total: int, count: int) -> bool`
- `scan_selection(paths: list) -> tuple` → `(fp, items)` with
  `items = [(path, os.stat_result, entries | None), ...]`
- `fingerprint_paths(paths: list) -> list` → `[(path, size, mtime_ns), ...]`
- `clip_has_folder_entries(data: list) -> bool`, `first_loose_entry(data: list) -> Optional[tuple]`
- `send_files_to_link` gains the `("skipped", 0)` return for a folder-only clip on a minor-0 link

Steps:

- [ ] **Step 1: Write the failing walk tests.** Create `tests/test_folder_walk.py`:
  ```python
  """Sender-side folder expansion: recursive walk, junk/symlink exclusion,
  deterministic byte-wise order, and the per-folder all-or-nothing admission.
  Pure logic against a real tmp_path tree; no clipboard, no sockets."""
  from __future__ import annotations

  import os
  import sys
  import unicodedata

  import pytest

  import anyclip
  from anyclip import expand_folder, folder_fits


  def test_walk_is_recursive_sorted_and_relative(tmp_path):
      folder = tmp_path / "docs"
      (folder / "sub").mkdir(parents=True)
      (folder / "empty").mkdir()
      (folder / "b.txt").write_bytes(b"bb")
      (folder / "a.txt").write_bytes(b"a")
      (folder / "sub" / "c.txt").write_bytes(b"ccc")

      entries = expand_folder(str(folder))
      assert [rel for _p, _s, _m, rel in entries] == [
          "docs/a.txt", "docs/b.txt", "docs/sub/c.txt",
      ]
      assert [size for _p, size, _m, _rel in entries] == [1, 2, 3]
      assert all(os.path.isabs(p) for p, _s, _m, _rel in entries)
      assert all(anyclip.is_valid_wire_path(rel, os.path.basename(p))
                 for p, _s, _m, rel in entries)


  def test_junk_sidecar_files_are_excluded(tmp_path):
      folder = tmp_path / "docs"
      folder.mkdir()
      (folder / "keep.txt").write_bytes(b"k")
      for junk in (".DS_Store", "Thumbs.db", "desktop.ini"):
          (folder / junk).write_bytes(b"junk")
      assert [rel for _p, _s, _m, rel in expand_folder(str(folder))] == [
          "docs/keep.txt"]


  @pytest.mark.skipif(sys.platform == "win32",
                      reason="symlink creation needs admin on Windows")
  def test_symlinks_are_never_followed(tmp_path):
      outside = tmp_path / "outside.txt"
      outside.write_bytes(b"o")
      folder = tmp_path / "docs"
      folder.mkdir()
      (folder / "keep.txt").write_bytes(b"k")
      (folder / "link.txt").symlink_to(outside)
      (folder / "loop").symlink_to(tmp_path, target_is_directory=True)
      assert [rel for _p, _s, _m, rel in expand_folder(str(folder))] == [
          "docs/keep.txt"]


  def test_empty_folder_expands_to_nothing(tmp_path):
      folder = tmp_path / "hollow"
      (folder / "inner").mkdir(parents=True)
      assert expand_folder(str(folder)) == []


  def test_nfd_folder_and_file_names_travel_as_nfc(tmp_path):
      folder = tmp_path / unicodedata.normalize("NFD", "보고서")
      folder.mkdir()
      (folder / unicodedata.normalize("NFD", "요약.pdf")).write_bytes(b"x")
      rels = [rel for _p, _s, _m, rel in expand_folder(str(folder))]
      assert rels == ["보고서/요약.pdf"]
      assert anyclip.is_valid_wire_path(rels[0], "요약.pdf")


  def test_folder_fits_is_all_or_nothing_on_budget_and_count(monkeypatch):
      entries = [("/a", 6, 0, "d/a"), ("/b", 6, 0, "d/b")]
      monkeypatch.setattr(anyclip, "FILE_BUDGET", 12)
      assert folder_fits(entries, total=0, count=0)      # exactly the budget
      assert not folder_fits(entries, total=1, count=0)  # one byte over
      monkeypatch.setattr(anyclip, "MAX_FILES_PER_CLIP", 2)
      assert folder_fits(entries, total=0, count=0)
      assert not folder_fits(entries, total=0, count=1)  # count overflow
      assert not folder_fits([], total=0, count=0)       # empty folder
  ```

- [ ] **Step 2: Run them, expect FAIL** — `ImportError: cannot import name 'expand_folder' from 'anyclip'`.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_folder_walk.py -q
  ```

- [ ] **Step 3: Implement the walk + admission + scan helpers.** In `anyclip.py`, insert before
  `class ClipboardWatcher` (line 969):
  ```python
  # Sidecar files no user means to sync. Excluded from a folder walk (log only).
  # Keep in lockstep with Swift folderJunkNames and C# FolderJunkNames.
  FOLDER_JUNK_NAMES = {".DS_Store", "Thumbs.db", "desktop.ini"}


  def expand_folder(path: str) -> list:
      """Recursively list the syncable files under a copied folder.

      Returns [(abs_path, size, mtime_ns, relpath), ...] sorted BYTE-WISE on
      relpath. ``relpath`` is POSIX, NFC, and starts with the folder's own name
      so the receiver rebuilds received/<folder>/... Symlinks are never followed
      (log only, which also makes cycles impossible), junk sidecars are skipped,
      and empty directories simply vanish -- they are not representable on the
      wire. Keep in lockstep with Swift expandFolder and C# ExpandFolder."""
      root = str(path).rstrip("/\\")
      top = unicodedata.normalize("NFC", os.path.basename(root)) or "folder"
      entries: list = []
      for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
          keep_dirs = []
          for d in sorted(dirnames):
              if os.path.islink(os.path.join(dirpath, d)):
                  log.info(f"folder walk: skipping symlinked dir {d!r} (never followed)")
                  continue
              keep_dirs.append(d)
          dirnames[:] = keep_dirs
          for fname in sorted(filenames):
              full = os.path.join(dirpath, fname)
              if fname in FOLDER_JUNK_NAMES:
                  log.debug(f"folder walk: skipping junk file {full!r}")
                  continue
              if os.path.islink(full):
                  log.info(f"folder walk: skipping symlink {full!r} (never followed)")
                  continue
              try:
                  st = os.stat(full)
              except OSError as exc:
                  log.warning(f"folder walk: stat failed for {full!r}: {exc}; skipping")
                  continue
              rel = os.path.relpath(full, root).replace(os.sep, "/")
              entries.append((full, st.st_size, st.st_mtime_ns,
                              unicodedata.normalize("NFC", f"{top}/{rel}")))
      entries.sort(key=lambda e: e[3].encode("utf-8"))
      return entries


  def folder_fits(entries: list, total: int, count: int) -> bool:
      """Per-folder ALL-OR-NOTHING admission against what the clip has left.

      A folder is accepted only if its ENTIRE expansion fits the remaining
      budget and file count -- no partial trees. An empty expansion never
      "fits" (the caller toasts it separately)."""
      if not entries:
          return False
      if count + len(entries) > MAX_FILES_PER_CLIP:
          return False
      return total + sum(size for _p, size, _m, _rel in entries) <= FILE_BUDGET


  def scan_selection(paths: list) -> tuple:
      """Stat a clipboard selection ONCE, expanding any folder in it.

      Returns ``(fp, items)``:
        fp    -- ordered [(path, size, mtime_ns), ...] fingerprint. A folder
                 contributes its OWN entry plus one per file in its expanded
                 tree, so a folder we just sent (or just wrote into received/)
                 is not re-detected and a change INSIDE the tree is.
        items -- [(path, os.stat_result, entries | None), ...] in selection
                 order; ``entries`` is expand_folder()'s output for a folder and
                 None for a plain file, so no caller ever walks twice.
      A path that vanished between grab and stat drops out of both lists."""
      fp: list = []
      items: list = []
      for path in paths:
          try:
              st = os.stat(path)
          except OSError:
              continue
          fp.append((path, st.st_size, st.st_mtime_ns))
          if stat_mod.S_ISDIR(st.st_mode):
              entries = expand_folder(path)
              fp.extend((p, size, mtime) for p, size, mtime, _rel in entries)
              items.append((path, st, entries))
          else:
              items.append((path, st, None))
      return fp, items


  def fingerprint_paths(paths: list) -> list:
      """Just the fingerprint half of scan_selection() -- used to baseline the
      watcher against files/folders we placed on the clipboard ourselves."""
      return scan_selection(paths)[0]
  ```

- [ ] **Step 4: Run them, expect PASS.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_folder_walk.py -q
  ```

- [ ] **Step 5: Write the failing watcher tests.** In `tests/test_clipboard_watcher.py`, REPLACE
  `test_directory_skipped_with_single_notice` (lines 44–72) and `test_folder_mixed_with_files`
  (lines 212–231) — both ranges start at the `@pytest.mark.asyncio` decorator — and append the
  new cases:
  ```python
  @pytest.mark.asyncio
  async def test_empty_folder_toasts_and_sends_nothing(monkeypatch, tmp_path):
      """A folder with nothing syncable in it toasts once and is NOT retried
      on subsequent polls (regression: infinite EISDIR retry loop)."""
      changes, skipped = [], []

      async def on_change(kind, data):
          changes.append((kind, data))

      async def on_skip(message):
          skipped.append(message)

      watcher = _make_watcher(on_change, on_file_skipped=on_skip)
      folder = tmp_path / "Inbox"
      folder.mkdir()
      monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

      await watcher._check_file_clipboard()
      assert changes == []
      assert skipped == ["folder is empty; nothing to sync"]

      await watcher._check_file_clipboard()
      assert changes == []
      assert len(skipped) == 1


  @pytest.mark.asyncio
  async def test_folder_mixed_with_files(monkeypatch, tmp_path):
      """A folder now EXPANDS instead of being skipped; loose files in the same
      selection keep their flat (pathless) entries, in selection order."""
      changes, skipped = [], []

      async def on_change(kind, data):
          changes.append((kind, data))

      async def on_skip(message):
          skipped.append(message)

      watcher = _make_watcher(on_change, on_file_skipped=on_skip)
      folder = tmp_path / "docs"
      (folder / "sub").mkdir(parents=True)
      (folder / "one.txt").write_bytes(b"1")
      (folder / "sub" / "two.txt").write_bytes(b"2")
      f1 = tmp_path / "a.txt"; f1.write_bytes(b"one")
      monkeypatch.setattr(anyclip, "grab_clipboard_files",
                          lambda: [str(folder), str(f1)])

      await watcher._check_file_clipboard()
      assert changes == [("files", [
          ("one.txt", b"1", "docs/one.txt"),
          ("two.txt", b"2", "docs/sub/two.txt"),
          ("a.txt", b"one", None),
      ])]
      assert skipped == []


  @pytest.mark.asyncio
  async def test_folder_expansion_is_not_re_detected_but_edits_are(
      monkeypatch, tmp_path,
  ):
      changes = []

      async def on_change(kind, data):
          changes.append((kind, data))

      watcher = _make_watcher(on_change)
      folder = tmp_path / "docs"
      (folder / "sub").mkdir(parents=True)
      (folder / "sub" / "b.txt").write_bytes(b"two")
      monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

      await watcher._check_file_clipboard()
      assert len(changes) == 1
      # Unchanged selection -> the expanded fingerprint matches -> no re-send.
      await watcher._check_file_clipboard()
      assert len(changes) == 1
      # A change DEEP inside the tree does not move the top dir's mtime, but the
      # expanded fingerprint covers it, so it is picked up.
      (folder / "sub" / "b.txt").write_bytes(b"two-changed")
      await watcher._check_file_clipboard()
      assert len(changes) == 2
      assert changes[1] == ("files", [("b.txt", b"two-changed", "docs/sub/b.txt")])


  @pytest.mark.asyncio
  async def test_single_file_folder_stays_a_files_clip(monkeypatch, tmp_path):
      """A one-file folder must NOT collapse to the legacy kind:"file" frame --
      that frame has nowhere to carry the path."""
      changes = []

      async def on_change(kind, data):
          changes.append((kind, data))

      watcher = _make_watcher(on_change)
      folder = tmp_path / "one"
      folder.mkdir()
      (folder / "only.txt").write_bytes(b"x")
      monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

      await watcher._check_file_clipboard()
      assert changes == [("files", [("only.txt", b"x", "one/only.txt")])]


  @pytest.mark.asyncio
  async def test_folder_over_budget_is_skipped_whole(monkeypatch, tmp_path):
      changes, skipped = [], []

      async def on_change(kind, data):
          changes.append((kind, data))

      async def on_skip(message):
          skipped.append(message)

      monkeypatch.setattr(anyclip, "FILE_BUDGET", 10)
      watcher = _make_watcher(on_change, on_file_skipped=on_skip)
      folder = tmp_path / "docs"
      folder.mkdir()
      (folder / "a.txt").write_bytes(b"123456")
      (folder / "b.txt").write_bytes(b"789012")
      monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

      await watcher._check_file_clipboard()
      assert changes == []                       # no partial tree
      assert skipped == ["folder too large to sync: docs"]


  @pytest.mark.asyncio
  async def test_loose_files_stay_greedy_while_a_folder_is_all_or_nothing(
      monkeypatch, tmp_path,
  ):
      """Selection order decides: the loose file eats the budget first, then the
      folder no longer fits ENTIRELY and is dropped whole."""
      changes, skipped = [], []

      async def on_change(kind, data):
          changes.append((kind, data))

      async def on_skip(message):
          skipped.append(message)

      monkeypatch.setattr(anyclip, "FILE_BUDGET", 10)
      watcher = _make_watcher(on_change, on_file_skipped=on_skip)
      loose = tmp_path / "a.txt"; loose.write_bytes(b"123456")
      folder = tmp_path / "docs"
      folder.mkdir()
      (folder / "x.txt").write_bytes(b"111")
      (folder / "y.txt").write_bytes(b"222")
      monkeypatch.setattr(anyclip, "grab_clipboard_files",
                          lambda: [str(loose), str(folder)])

      await watcher._check_file_clipboard()
      assert changes == [("file", ("a.txt", b"123456"))]
      assert skipped == ["folder too large to sync: docs"]


  @pytest.mark.asyncio
  async def test_folder_over_the_file_count_cap_is_skipped_whole(
      monkeypatch, tmp_path,
  ):
      changes, skipped = [], []

      async def on_change(kind, data):
          changes.append((kind, data))

      async def on_skip(message):
          skipped.append(message)

      monkeypatch.setattr(anyclip, "MAX_FILES_PER_CLIP", 2)
      watcher = _make_watcher(on_change, on_file_skipped=on_skip)
      folder = tmp_path / "docs"
      folder.mkdir()
      for i in range(3):
          (folder / f"{i}.txt").write_bytes(b"x")
      monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

      await watcher._check_file_clipboard()
      assert changes == []
      assert skipped == ["folder too large to sync: docs"]
  ```

- [ ] **Step 6: Run them, expect FAIL** — the watcher still skips directories, so the folder tests get
  `assert [] == [('files', [...])]` and `skipped == ['folder not synced — folders are not supported: Inbox']`.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_clipboard_watcher.py -q
  ```

- [ ] **Step 7: Expand folders in the watcher.** In `anyclip.py`, replace the fingerprint seed
  (lines 1006–1015) with:
  ```python
          # Seed the baseline from whatever is already on the clipboard so we do
          # not fire a spurious initial send at startup. A folder contributes its
          # expanded tree, exactly like _check_file_clipboard computes it.
          self._last_file_fp = fingerprint_paths(grab_clipboard_files() or []) or None
  ```
  and replace the whole body of `_check_file_clipboard` (lines 1087–1167) with:
  ```python
      async def _check_file_clipboard(self) -> None:
          paths = await asyncio.to_thread(grab_clipboard_files)
          if not paths:
              return
          # One stat pass over the selection, expanding folders as we go, so a
          # tree is walked once per CHANGED selection, not once per poll.
          fp, items = await asyncio.to_thread(scan_selection, paths)
          if not fp:
              return
          if fp == self._last_file_fp:
              return  # unchanged selection
          # Record the fingerprint FIRST so a selection we cannot fully sync is
          # never re-detected and retried every poll cycle (folder-skip design).
          self._last_file_fp = fp

          # Selection order, each item consuming the remaining budget/count: a
          # folder is ALL-OR-NOTHING, loose files stay greedy per file.
          accepted = []  # (name, raw_bytes, relpath | None)
          skipped_count = 0
          total = 0
          for path, stat, entries in items:
              if entries is not None:
                  folder_name = os.path.basename(path.rstrip("/\\")) or path
                  if not entries:
                      await self._notify_file_skipped(
                          "folder is empty; nothing to sync")
                      continue
                  if not folder_fits(entries, total, len(accepted)):
                      await self._notify_file_skipped(
                          f"folder too large to sync: {folder_name}")
                      continue
                  for full, size, _mtime, rel in entries:
                      try:
                          data = await asyncio.to_thread(Path(full).read_bytes)
                      except OSError as exc:
                          # Admission already passed on the pre-flight sizes; a
                          # single unreadable file is logged and dropped from the
                          # tree rather than failing the whole folder.
                          log.warning(f"file read failed for {full!r}: {exc}; skipping")
                          continue
                      total += size
                      accepted.append((
                          unicodedata.normalize("NFC", os.path.basename(full)),
                          data, rel,
                      ))
                  continue
              if len(accepted) >= MAX_FILES_PER_CLIP:
                  skipped_count += 1
                  continue
              if total + stat.st_size > FILE_BUDGET:
                  skipped_count += 1
                  continue
              try:
                  data = await asyncio.to_thread(Path(path).read_bytes)
              except OSError as exc:
                  log.warning(f"file read failed for {path!r}: {exc}; skipping")
                  skipped_count += 1
                  continue
              total += stat.st_size
              # NFC on the wire (macOS reads filenames in NFD). Keep in lockstep
              # with Swift WireMessage and C# WireMessage.
              name = unicodedata.normalize("NFC", os.path.basename(path))
              accepted.append((name, data, None))

          if skipped_count:
              await self._notify_file_skipped(
                  f"{skipped_count} file(s) skipped (too large to sync)"
              )

          if not accepted:
              return
          # A lone LOOSE file keeps the legacy kind:"file" frame; a one-file
          # folder must stay kind:"files" or its path would be dropped.
          if len(accepted) == 1 and accepted[0][2] is None:
              self._last_file_hash = sha256_bytes(accepted[0][1])
              try:
                  await self.on_change("file", (accepted[0][0], accepted[0][1]))
              except Exception as exc:
                  log.exception(f"on_change(file) handler failed: {exc}")
          else:
              try:
                  await self.on_change("files", accepted)
              except Exception as exc:
                  log.exception(f"on_change(files) handler failed: {exc}")
  ```

- [ ] **Step 8: Run them, expect PASS.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_clipboard_watcher.py tests/test_folder_walk.py -q
  ```

- [ ] **Step 9: Write the failing fallback-matrix tests.** In `tests/test_link_manager.py` add
  `import logging` to the imports and append:
  ```python
  # ---- folder fallback matrix (protocol 1.3) -----------------------------

  def test_folder_entries_are_excluded_from_the_minor0_fallback(caplog):
      async def go():
          caplog.set_level(logging.INFO, logger="anyclip")
          mgr = LinkManager(_cfg(), "node-self", None)
          old = FakeLink(minor=0, name="old")
          mid = FakeLink(minor=1, name="mid")
          new = FakeLink(minor=3, name="new")
          mgr._links = {"o": old, "m": mid, "n": new}
          data = [("a.txt", b"one", "docs/a.txt"), ("loose.txt", b"two", None)]

          full, fallback, dropped, skipped = await mgr.broadcast_files(data)

          assert full == 2 and fallback == 1 and dropped == 1 and skipped == []
          kind, payload = old.sent[0]
          assert kind == "file" and payload["name"] == "loose.txt"  # NOT a.txt
          # Minor 1-2 peers get the SAME frame and flatten it benignly.
          kind, payload = mid.sent[0]
          assert kind == "files" and payload["files"][0]["path"] == "docs/a.txt"
          assert "peer mid will flatten folders (protocol < 1.3)" in caplog.text
          assert "peer new will flatten folders" not in caplog.text
      asyncio.run(go())


  def test_folder_only_clip_sends_nothing_to_a_minor0_peer(caplog):
      async def go():
          caplog.set_level(logging.INFO, logger="anyclip")
          mgr = LinkManager(_cfg(), "node-self", None)
          old = FakeLink(minor=0, name="old")
          new = FakeLink(minor=3, name="new")
          mgr._links = {"o": old, "n": new}
          data = [("a.txt", b"one", "docs/a.txt")]

          full, fallback, dropped, skipped = await mgr.broadcast_files(data)

          assert full == 1 and fallback == 0 and dropped == 0 and skipped == []
          assert old.sent == [] and old.active and not old.closed  # link kept
          assert "folder-only clip not sent to 'old'" in caplog.text
      asyncio.run(go())


  def test_no_flatten_log_for_a_clip_without_folders(caplog):
      async def go():
          caplog.set_level(logging.INFO, logger="anyclip")
          mgr = LinkManager(_cfg(), "node-self", None)
          mid = FakeLink(minor=1, name="mid")
          mgr._links = {"m": mid}
          await mgr.broadcast_files([("a.txt", b"one"), ("b.txt", b"two")])
          assert "will flatten folders" not in caplog.text
      asyncio.run(go())


  def test_first_loose_entry_and_folder_detection():
      folder_only = [("a.txt", b"one", "docs/a.txt")]
      mixed = folder_only + [("b.txt", b"two", None)]
      legacy = [("a.txt", b"one"), ("b.txt", b"two")]
      assert anyclip.first_loose_entry(folder_only) is None
      assert anyclip.first_loose_entry(mixed) == ("b.txt", b"two")
      assert anyclip.first_loose_entry(legacy) == ("a.txt", b"one")
      assert anyclip.clip_has_folder_entries(folder_only)
      assert anyclip.clip_has_folder_entries(mixed)
      assert not anyclip.clip_has_folder_entries(legacy)
  ```
  and in `tests/test_receive_files.py` append:
  ```python
  def test_emit_files_old_peer_skips_a_folder_only_clip():
      async def go():
          link = _FakeLink(minor=0)
          data = [("a.txt", b"one", "docs/a.txt")]
          assert await anyclip.emit_files_clip(
              link, EchoSuppressor(), data) == ("skipped", 0)
          assert link.sent == []
      asyncio.run(go())
  ```

- [ ] **Step 10: Run them, expect FAIL** — `AttributeError: module 'anyclip' has no attribute
  'first_loose_entry'`, and the minor-0 link still receives `payload["name"] == "a.txt"`.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_link_manager.py tests/test_receive_files.py -q
  ```

- [ ] **Step 11: Implement the fallback matrix.** In `anyclip.py`, insert after `files_variant_for_link`
  (after line 1385):
  ```python
  def clip_has_folder_entries(data: list) -> bool:
      """True when any entry of a files clip came out of a copied folder."""
      return any(entry_relpath(ent) for ent in data)


  def first_loose_entry(data: list) -> Optional[tuple]:
      """The (name, raw) a protocol-1.0 peer gets as the legacy first-file
      fallback. Folder-derived entries are EXCLUDED -- a stray fragment of
      someone's tree is worse than nothing -- so a folder-only clip yields None
      and that link is skipped (log only)."""
      for ent in data:
          if entry_relpath(ent) is None:
              return (ent[0], ent[1])
      return None
  ```
  replace `LinkManager.broadcast_files` (lines 2087–2129) with:
  ```python
      async def broadcast_files(self, data) -> tuple:
          """Fan out a multi-file selection with per-link minor gating. Returns
          (sent_full, sent_fallback, max_dropped, skipped_names) aggregated
          across links for a single toast each. The global echo check is done by
          the caller. Each distinct payload variant ("files" for a protocol >=
          1.1 peer, the first-file "file" fallback otherwise) is encoded at most
          once per broadcast and reused for both the size gate and the send.

          Folder-derived entries are excluded from the minor-0 fallback, so a
          folder-only clip sends NOTHING on such a link; a minor 1-2 peer gets
          the same frame and flattens it (logged once per clip per link)."""
          sent_full = sent_fallback = max_dropped = 0
          skipped: list = []
          frames: dict = {}
          folder_clip = clip_has_folder_entries(data)

          def frame_for(variant: str) -> Optional[bytes]:
              if variant not in frames:
                  if variant == "files":
                      payload = build_clip_payload("files", data)
                  else:
                      loose = first_loose_entry(data)
                      payload = None if loose is None else build_clip_payload(
                          "file", (loose[0], bytes(loose[1])))
                  frames[variant] = None if payload is None else encode_frame(payload)
              return frames[variant]

          for link in list(self._links.values()):
              if not link.active:
                  continue
              variant = files_variant_for_link(link)
              frame = frame_for(variant)
              if frame is None:
                  if variant == "file" and folder_clip:
                      log.info(
                          f"folder-only clip not sent to {link.peer_name!r} "
                          "(peer protocol 1.0)"
                      )
                  continue
              if (folder_clip and variant == "files"
                      and (link.peer_protocol_minor or 0) < 3):
                  log.info(
                      f"peer {link.peer_name} will flatten folders (protocol < 1.3)")
              if not self._gate(link, frame, skipped):
                  continue
              try:
                  await link.send_frame(frame)
              except Exception as exc:
                  log.info(f"send to {link.peer_name!r} failed: {exc}; dropping link")
                  await self._drop_link(link)
                  continue
              if variant == "files":
                  sent_full += 1
              else:  # legacy first-file fallback for a minor-0 peer
                  sent_fallback += 1
                  max_dropped = max(max_dropped, len(data) - 1)
          return sent_full, sent_fallback, max_dropped, skipped
  ```
  and replace `send_files_to_link` (lines 2549–2565) with:
  ```python
  async def send_files_to_link(link, data) -> tuple:
      """Per-link minor gating for a multi-file clip (NO echo check), on ONE
      link:
        minor >= 1 -> one kind:"files" clip, returns ("files", len(data)).
        minor 0    -> first LOOSE file as legacy kind:"file", returns
                      ("file", dropped); a folder-only clip has no loose file,
                      so nothing is sent and it returns ("skipped", 0).

      The mesh fan-out does not call this -- LinkManager.broadcast_files picks
      the same variant via files_variant_for_link() but encodes each variant
      once and applies the legacy size gate before sending. Keep the variant
      choice here in lockstep with that.
      """
      if files_variant_for_link(link) == "files":
          await link.send_clip("files", data)
          return ("files", len(data))
      loose = first_loose_entry(data)
      if loose is None:
          log.info(
              f"folder-only clip not sent to {link.peer_name!r} (peer protocol 1.0)")
          return ("skipped", 0)
      await link.send_clip("file", (loose[0], bytes(loose[1])))
      return ("file", len(data) - 1)
  ```
  and extend the `emit_files_clip` docstring (lines 2569–2575) with the new outcome:
  ```python
      """Single-link send decision + echo suppression. ``data`` is
      [(name, raw_bytes, relpath|None), ...] with len >= 2. Returns:
        ("suppressed", 0) -- echo of a just-received set; nothing sent.
        ("files", n)      -- sent all n files as one kind:"files" clip.
        ("file", dropped) -- peer protocol_minor 0; sent the first LOOSE file as
                             a legacy kind:"file" clip; ``dropped`` others not sent.
        ("skipped", 0)    -- peer protocol_minor 0 and the clip is folder-only;
                             nothing sent on this link.
      """
  ```

- [ ] **Step 12: Run the whole suite, expect PASS.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/ -q
  ```

- [ ] **Step 13: Commit.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && git add anyclip.py tests/test_folder_walk.py tests/test_clipboard_watcher.py tests/test_link_manager.py tests/test_receive_files.py && git commit -m "$(cat <<'EOF'
feat(watcher-py): expand copied folders into a files clip instead of skipping

The watcher walks a copied folder (files only, byte-wise sorted on the relative
path), excludes .DS_Store/Thumbs.db/desktop.ini and symlinks (never followed),
drops empty dirs, and tags each entry with <folder>/<relative path>. Admission
is per-folder ALL-OR-NOTHING against the remaining budget/count -- otherwise the
whole folder is skipped with "folder too large to sync: <name>"; an empty folder
toasts "folder is empty; nothing to sync". Loose files keep today's greedy
behavior and a one-file folder stays a kind:"files" frame so its path survives.

The selection fingerprint now covers the expanded tree, so a just-sent folder is
not re-detected while an edit deep inside it is.

Fan-out: folder entries are excluded from the minor-0 first-file fallback (a
folder-only clip sends nothing on that link, log only) and a minor 1-2 peer is
logged once per clip as "peer <name> will flatten folders (protocol < 1.3)".

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
  ```

---

### Task 3: Python receiver — rebuild the tree under `received/` and place it

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/anyclip.py`
  - `import shutil` in the stdlib import block (before line 17 `import signal`)
  - `clear_received_dir` (lines 309–324)
  - new `plan_received_layout` / `received_clip_message` / `placed_single_loose_file`
    before `class ClipboardWatcher` (line 969)
  - `ClipboardWatcher.update_local_files` (lines 1231–1273) — full rewrite
  - `on_remote_clip` files branch (lines 2641–2661) — note `on_remote_clip` is a CLOSURE defined
    inside `async def run(config)` (anyclip.py:2583), so it cannot be imported or called from a
    unit test; its one new decision is factored into the module-level
    `placed_single_loose_file` helper instead
- Create (test): `/Users/seojeonghwa/project/AnyClip/tests/test_received_tree.py`

**Interfaces:**

Consumes: `sanitize_filename` (anyclip.py:917), `uniquify_names` (:943),
`entry_relpath` / `is_valid_wire_path` (Task 1), `fingerprint_paths` (Task 2),
`set_clipboard_file(path: str) -> bool` (:485), `set_clipboard_files(paths: list) -> bool` (:521),
`decode_files_payload` output shape `[(name, data, relpath|None), ...]` (Task 1),
`EchoSuppressor.mark_received(self, kind: str, payload_hash: str) -> None` (:902),
`notify_async(title, message)`.

Produces:
- `plan_received_layout(files: list, existing) -> list` → `[(relative_destination: str, top_level_item: str), ...]`
  in batch order
- `received_clip_message(files: list) -> str`
- `placed_single_loose_file(files: list, placed: int) -> bool` — the `on_remote_clip` suppressor
  guard, factored out of the closure so it is directly testable
- `ClipboardWatcher.update_local_files(self, files: list) -> int` now returns the number of
  **top-level items** placed on the clipboard

Steps:

- [ ] **Step 1: Write the failing layout tests.** Create `tests/test_received_tree.py`:
  ```python
  """Receiver-side folder rebuild: destination planning (sanitize, top-segment
  uniquify, flat fallback), the actual writes under received/, clipboard
  placement, the received-clip toast wording, and the echo-suppressor guard
  lifted out of on_remote_clip."""
  from __future__ import annotations

  import unicodedata

  import pytest

  import anyclip
  from anyclip import ClipboardWatcher, plan_received_layout


  @pytest.fixture(autouse=True)
  def quiet_clipboard(monkeypatch):
      monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [])
      monkeypatch.setattr(anyclip, "grab_clipboard_image", lambda: None)
      monkeypatch.setattr(anyclip.pyperclip, "paste", lambda: "")


  def _make_watcher() -> ClipboardWatcher:
      async def _noop(kind, data) -> None:
          return None

      return ClipboardWatcher(0.01, _noop)


  # ---- destination planning ---------------------------------------------

  def test_plan_rebuilds_a_tree_and_keeps_loose_files_flat():
      files = [
          ("a.txt", b"", "docs/a.txt"),
          ("b.txt", b"", "docs/sub/b.txt"),
          ("loose.txt", b"", None),
      ]
      assert plan_received_layout(files, set()) == [
          ("docs/a.txt", "docs"),
          ("docs/sub/b.txt", "docs"),
          ("loose.txt", "loose.txt"),
      ]


  def test_top_segment_uniquify_is_shared_by_every_entry_of_the_clip():
      files = [("a.txt", b"", "docs/a.txt"), ("b.txt", b"", "docs/sub/b.txt")]
      assert plan_received_layout(files, {"docs"}) == [
          ("docs-2/a.txt", "docs-2"), ("docs-2/sub/b.txt", "docs-2"),
      ]
      assert plan_received_layout(files, {"docs", "docs-2"}) == [
          ("docs-3/a.txt", "docs-3"), ("docs-3/sub/b.txt", "docs-3"),
      ]


  def test_two_folders_in_one_clip_get_independent_tops():
      files = [("a", b"", "x/a"), ("b", b"", "y/b")]
      assert plan_received_layout(files, {"x"}) == [("x-2/a", "x-2"), ("y/b", "y")]


  def test_every_segment_goes_through_the_per_name_sanitizer():
      files = [("a:b.txt", b"", "보고서/con/a:b.txt")]
      assert plan_received_layout(files, set()) == [
          ("보고서/_con/a_b.txt", "보고서"),
      ]


  def test_loose_duplicates_keep_the_existing_uniquify():
      files = [("dup.txt", b"", None), ("dup.txt", b"", None)]
      assert plan_received_layout(files, set()) == [
          ("dup.txt", "dup.txt"), ("dup (2).txt", "dup (2).txt"),
      ]


  def test_a_folder_top_never_collides_with_a_loose_file_of_the_same_clip():
      files = [("docs", b"", None), ("a.txt", b"", "docs/a.txt")]
      assert plan_received_layout(files, set()) == [
          ("docs", "docs"), ("docs-2/a.txt", "docs-2"),
      ]


  @pytest.mark.parametrize("bad", [
      "../../evil.txt", "/etc/evil.txt", "C:/evil.txt", "docs\\evil.txt",
      "docs/other.txt",                                  # last segment != name
      unicodedata.normalize("NFD", "보고서") + "/evil.txt",  # not NFC
  ])
  def test_a_violating_path_falls_back_to_flat_placement(bad):
      """Defense in depth: decode already rejects these, but the writer never
      trusts its caller -- the entry goes flat, it is never dropped."""
      assert plan_received_layout([("evil.txt", b"", bad)], set()) == [
          ("evil.txt", "evil.txt"),
      ]
  ```

- [ ] **Step 2: Run them, expect FAIL** —
  `ImportError: cannot import name 'plan_received_layout' from 'anyclip'`.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_received_tree.py -q
  ```

- [ ] **Step 3: Implement the planner + toast helper.** In `anyclip.py`, insert before
  `class ClipboardWatcher` (line 969, next to the other pure helpers):
  ```python
  def _writable_relpath(ent) -> Optional[str]:
      """The wire path of an entry, re-verified at the write boundary. Decode
      already rejected violators; the writer never trusts its caller either."""
      rel = entry_relpath(ent)
      if rel and is_valid_wire_path(rel, ent[0]):
          return rel
      return None


  def plan_received_layout(files: list, existing) -> list:
      """Map a decoded files clip onto destinations under received/.

      Returns [(relative_destination, top_level_item), ...] in batch order, one
      per entry: ``relative_destination`` is '<top>/<sub>/<name>' for a folder
      entry and '<name>' for a loose file (or for any entry whose path violates
      the wire rules -- flat fallback, never a drop). Each segment goes through
      the existing per-name sanitizer (NFC + denylist + reserved names).

      ``existing`` is the set of names already present in received/. A colliding
      top segment becomes '<top>-2', '<top>-3', ... and the SAME replacement is
      applied to every entry sharing that top, so one clip lands in ONE folder.
      Loose entries keep the per-file ' (2)' uniquify."""
      loose_idx = [i for i, ent in enumerate(files) if _writable_relpath(ent) is None]
      loose = uniquify_names([sanitize_filename(files[i][0]) for i in loose_idx])
      used = set(existing) | set(loose)
      tops: dict = {}
      plan: list = [None] * len(files)
      for pos, i in enumerate(loose_idx):
          plan[i] = (loose[pos], loose[pos])
      for i, ent in enumerate(files):
          rel = _writable_relpath(ent)
          if rel is None:
              continue
          segments = [sanitize_filename(seg) for seg in rel.split("/")]
          raw_top = segments[0]
          if raw_top not in tops:
              candidate, n = raw_top, 2
              while candidate in used:
                  candidate = f"{raw_top}-{n}"
                  n += 1
              tops[raw_top] = candidate
              used.add(candidate)
          segments[0] = tops[raw_top]
          plan[i] = ("/".join(segments), segments[0])
      return plan


  def received_clip_message(files: list) -> str:
      """Toast body for an inbound kind:"files" clip: a clip that is entirely
      ONE folder names that folder, anything else keeps the count wording."""
      tops = set()
      for ent in files:
          rel = entry_relpath(ent)
          if rel is None:
              return f"{len(files)} files"
          tops.add(rel.split("/", 1)[0])
      if len(tops) == 1:
          return f"{tops.pop()} ({len(files)} files)"
      return f"{len(files)} files"
  ```

- [ ] **Step 4: Run them, expect PASS.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_received_tree.py -q
  ```

- [ ] **Step 5: Write the failing write/placement tests.** Append to `tests/test_received_tree.py`:
  ```python
  # ---- writes + clipboard placement --------------------------------------

  def test_tree_is_written_and_the_top_folder_is_placed(monkeypatch, tmp_path):
      monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
      monkeypatch.setattr(anyclip.sys, "platform", "darwin")  # places FIRST only
      placed = {}

      def fake_set(path):
          placed["path"] = path
          return True

      monkeypatch.setattr(anyclip, "set_clipboard_file", fake_set)
      watcher = _make_watcher()

      n = watcher.update_local_files([
          ("a.txt", b"one", "docs/a.txt"),
          ("b.txt", b"two", "docs/sub/b.txt"),
      ])

      received = tmp_path / "received"
      assert (received / "docs" / "a.txt").read_bytes() == b"one"
      assert (received / "docs" / "sub" / "b.txt").read_bytes() == b"two"
      assert n == 1
      assert placed["path"] == str(received / "docs")
      # Baseline covers the placed folder AND its tree, so the next poll does
      # not echo what we just wrote.
      assert watcher._last_file_fp is not None
      assert len(watcher._last_file_fp) == 3


  def test_windows_places_every_top_level_item_in_batch_order(monkeypatch, tmp_path):
      monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
      monkeypatch.setattr(anyclip.sys, "platform", "win32")
      placed = {}

      def fake_set_many(paths):
          placed["paths"] = list(paths)
          return True

      monkeypatch.setattr(anyclip, "set_clipboard_files", fake_set_many)
      watcher = _make_watcher()

      n = watcher.update_local_files([
          ("a.txt", b"1", "docs/a.txt"),
          ("b.txt", b"2", "docs/sub/b.txt"),
          ("loose.txt", b"3", None),
          ("c.txt", b"4", "other/c.txt"),
      ])

      received = tmp_path / "received"
      assert n == 3
      assert placed["paths"] == [
          str(received / "docs"),
          str(received / "loose.txt"),
          str(received / "other"),
      ]


  def test_a_second_clip_of_the_same_folder_lands_beside_the_first(
      monkeypatch, tmp_path,
  ):
      monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
      monkeypatch.setattr(anyclip.sys, "platform", "darwin")
      monkeypatch.setattr(anyclip, "set_clipboard_file", lambda p: True)
      watcher = _make_watcher()
      clip = [("a.txt", b"one", "docs/a.txt"), ("b.txt", b"two", "docs/sub/b.txt")]

      watcher.update_local_files(clip)
      watcher.update_local_files(clip)

      received = tmp_path / "received"
      assert (received / "docs" / "a.txt").exists()
      assert (received / "docs-2" / "a.txt").read_bytes() == b"one"
      assert (received / "docs-2" / "sub" / "b.txt").read_bytes() == b"two"


  def test_writer_never_escapes_received(monkeypatch, tmp_path):
      monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path / "home")
      monkeypatch.setattr(anyclip.sys, "platform", "darwin")
      monkeypatch.setattr(anyclip, "set_clipboard_file", lambda p: True)
      watcher = _make_watcher()

      watcher.update_local_files([("evil.txt", b"x", "../../evil.txt")])

      assert not (tmp_path / "evil.txt").exists()
      assert (tmp_path / "home" / "received" / "evil.txt").read_bytes() == b"x"


  def test_received_clip_message_names_a_folder_only_clip():
      folder = [("a", b"", "docs/a"), ("b", b"", "docs/sub/b")]
      assert anyclip.received_clip_message(folder) == "docs (2 files)"
      assert anyclip.received_clip_message(
          folder + [("c", b"", None)]) == "3 files"
      assert anyclip.received_clip_message(
          [("a", b"", "x/a"), ("b", b"", "y/b")]) == "2 files"


  def test_clear_received_dir_removes_trees(monkeypatch, tmp_path):
      monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
      received = tmp_path / "received"
      (received / "docs" / "sub").mkdir(parents=True)
      (received / "docs" / "sub" / "a.txt").write_bytes(b"x")
      (received / "loose.txt").write_bytes(b"y")

      anyclip.clear_received_dir()

      assert received.exists()
      assert list(received.iterdir()) == []
  ```

- [ ] **Step 6: Run them, expect FAIL** — today's `update_local_files` writes `docs/a.txt` as the flat
  name `a.txt` (`FileNotFoundError` on `received/docs/a.txt`) and `clear_received_dir` leaves the
  `docs` directory behind (`assert [PosixPath('.../docs')] == []`).
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_received_tree.py -q
  ```

- [ ] **Step 7: Rebuild the tree on write.** In `anyclip.py`, add `import shutil` to the stdlib block
  (immediately before `import signal`, line 17), replace the loop body of `clear_received_dir`
  (lines 319–324):
  ```python
      for entry in target.iterdir():
          try:
              if entry.is_dir() and not entry.is_symlink():
                  shutil.rmtree(entry)
              else:
                  entry.unlink()
          except OSError as exc:
              log.debug(f"could not remove {entry}: {exc}")
  ```
  and replace `ClipboardWatcher.update_local_files` (lines 1231–1273) with:
  ```python
      def update_local_files(self, files: list) -> int:
          """Write a received clip under ~/.anyclip/received/ and place its
          TOP-LEVEL items on the clipboard in one operation. ``files`` is
          [(name, raw_bytes, relpath|None), ...] as decoded from the wire.

          Entries carrying a path rebuild their folder tree (protocol 1.3);
          entries without one keep the flat behavior. Every destination is
          re-checked to stay under received/ after sanitization -- an entry that
          would escape is written flat instead. macOS places only the FIRST
          top-level item (AppleScript furl limit); Windows places all. Baselines
          the fingerprint (folders expanded) to what was actually PLACED so the
          files we just wrote are not re-detected. Returns the number of items
          placed on the clipboard (0 on failure)."""
          target_dir = LOG_DIR / "received"
          try:
              target_dir.mkdir(parents=True, exist_ok=True)
              root = target_dir.resolve()
              existing = {p.name for p in target_dir.iterdir()}
          except OSError as exc:
              log.warning(f"file write to {target_dir} failed: {exc}")
              return 0
          plan = plan_received_layout(files, existing)
          written: list = []  # absolute top-level paths, first appearance order
          try:
              for ent, (rel, top) in zip(files, plan):
                  dest = target_dir / rel
                  try:
                      dest.parent.mkdir(parents=True, exist_ok=True)
                      dest.resolve().relative_to(root)
                  except (OSError, ValueError):
                      # Never write outside received/: fall back to flat.
                      log.warning(
                          f"received path {rel!r} escapes received/; placing flat")
                      dest = target_dir / sanitize_filename(ent[0])
                      top = dest.name
                  dest.write_bytes(bytes(ent[1]))
                  top_path = str(target_dir / top)
                  if top_path not in written:
                      written.append(top_path)
          except OSError as exc:
              log.warning(f"file write to {target_dir} failed: {exc}")
              return 0
          if not written:
              return 0
          if sys.platform == "darwin":
              placed = written[:1]
              ok = set_clipboard_file(placed[0])
          elif sys.platform == "win32":
              placed = list(written)
              ok = set_clipboard_files(placed)
          else:
              placed, ok = [], False
          if not ok:
              log.warning("clipboard write (files) failed or unsupported on this OS")
              placed = []
          self._last_file_fp = fingerprint_paths(placed) or None
          return len(placed)
  ```

- [ ] **Step 8: Run them, expect PASS.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_received_tree.py -q
  ```

- [ ] **Step 9: Write the failing suppressor-guard test.** The only NEW decision in the
  `on_remote_clip` files branch is when to seed the single-file suppressor slot, and
  `on_remote_clip` is a closure inside `run()` (anyclip.py:2583) that no test can call. Factor the
  condition into a module-level helper and pin it. Append to `tests/test_received_tree.py`:
  ```python
  # ---- on_remote_clip's suppressor guard ---------------------------------

  def test_only_a_lone_placed_loose_file_seeds_the_single_file_slot():
      """Python-macOS places only the FIRST top-level item. A lone placed LOOSE
      file re-surfaces on the next poll as kind:"file", so its slot must be
      seeded; a placed FOLDER re-surfaces as kind:"files" and must NOT seed it
      (seeding would suppress a genuine later single-file copy)."""
      loose = [("a.txt", b"one", None)]
      folder = [("a.txt", b"one", "docs/a.txt"), ("b.txt", b"two", "docs/sub/b.txt")]
      assert anyclip.placed_single_loose_file(loose, 1)
      assert not anyclip.placed_single_loose_file(loose, 0)   # nothing placed
      assert not anyclip.placed_single_loose_file(folder, 1)  # folder placed
      assert not anyclip.placed_single_loose_file([], 1)      # no entries
      # Legacy 2-tuples read as loose, exactly like entry_relpath().
      assert anyclip.placed_single_loose_file([("a.txt", b"one")], 1)
  ```

- [ ] **Step 10: Run it, expect FAIL** —
  `AttributeError: module 'anyclip' has no attribute 'placed_single_loose_file'`.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/test_received_tree.py -q
  ```

- [ ] **Step 11: Add the guard helper and wire the receive log + toast.** In `anyclip.py`, add next to
  `received_clip_message` (the pure-helper block before `class ClipboardWatcher`):
  ```python
  def placed_single_loose_file(files: list, placed: int) -> bool:
      """True when a received files clip ended up as exactly ONE placed
      top-level item and that item is a LOOSE file rather than a folder.

      Lifted out of on_remote_clip (a closure inside run(), untestable in
      isolation) so the decision itself has a unit test. Entries are in batch
      order and plan_received_layout preserves it, so the first placed item
      corresponds to files[0]."""
      return placed == 1 and bool(files) and entry_relpath(files[0]) is None
  ```
  then replace the tail of the `"files"` branch of `on_remote_clip` (lines 2647–2661):
  ```python
              placed = await asyncio.to_thread(watcher.update_local_files, data)
              # Python-macOS places only the FIRST top-level item; a re-detection
              # of a lone placed LOOSE file surfaces as kind:"file", so also seed
              # the single-file suppressor slot with that file's hash. A placed
              # FOLDER re-surfaces as kind:"files" and needs no extra seeding.
              if placed_single_loose_file(data, placed):
                  suppressor.mark_received("file", hashes[0])
              log.info(
                  f"<- received {len(data)} files from {peer!r} "
                  f"({placed} top-level item(s) placed on clipboard)"
              )
              if notify_enabled:
                  await notify_async(
                      title=f"AnyClip ← {peer}",
                      message=received_clip_message(data),
                  )
  ```
  The reworded log line and the `received_clip_message` toast are inside the closure and are
  covered only by the full-suite run in Step 12 (`received_clip_message` itself is pinned by
  `test_received_clip_message_names_a_folder_only_clip` in Step 5); the `hashes` comprehension
  above them is already index-based after Task 1 Step 11.

- [ ] **Step 12: Run the whole suite, expect PASS.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/ -q
  ```

- [ ] **Step 13: Commit.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && git add anyclip.py tests/test_received_tree.py && git commit -m "$(cat <<'EOF'
feat(receive-py): rebuild folder trees under received/ and place the top items

A files clip whose entries carry a path is written as a real tree under
~/.anyclip/received/: every segment sanitized + NFC-normalized, intermediate
dirs created, and the resolved destination re-checked to stay under received/ --
an entry that violates any wire rule (or would escape) is written flat instead,
never dropped. A colliding top segment becomes <top>-2 and the SAME replacement
is applied to every entry of that clip, so one clip lands in one folder; loose
files keep the per-file " (2)" uniquify.

Clipboard placement is per top-level item in batch order (macOS still places the
first item only), the fingerprint baseline expands the placed folders, the toast
names a folder-only clip as "<top> (N files)", and clear_received_dir now
removes trees, not just files.

The single-file echo-suppressor seed now only fires for a lone placed LOOSE
file; the condition moved into placed_single_loose_file() so it is unit-tested
instead of buried in the on_remote_clip closure.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
  ```

---

### Task 4: Swift wire — protocol minor 3, optional `path` on `kind:"files"`, golden fixtures

**Files:**
- Modify `formacOS/Sources/AnyClipCore/WireProtocol.swift` (Wire constants L4–L60, `ClipPayload` L82–L105, `WireFileEntry` L111–L122, `clipFiles` L196–L215, `decodeFileEntries` L293–L305 — line numbers are the PRE-task state; the later steps address these by symbol name because the earlier steps shift them)
- Modify `formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift` (compile ripple only: L152, L175, L217–L218, L226)
- Modify (regenerate, do not hand-edit) `formacOS/Tests/AnyClipCoreTests/Fixtures/hello.bin`, `.../manifest.json`, create `.../clip_files_path.bin`
- Test `formacOS/Tests/AnyClipCoreTests/WireProtocolTests.swift` (edit L41, L124–L129, L162–L168, L170–L182, L184–L192, L194–L198; add 4 tests)
- Test `formacOS/Tests/AnyClipCoreTests/LargeFrameTests.swift` (edit L16 — `frameCapsAndProtocolMinor` also pins the minor)
- Test `formacOS/Tests/AnyClipCoreTests/GoldenVectorTests.swift` (edit the hello vector's `protocol_minor` assertion — L37, the line AFTER `#expect(m.protocol_major == 1)`; add `goldenClipFilesTreeDecodes`)
- Test (compile ripple only, add `relPath: nil`): `formacOS/Tests/AnyClipDaemonTests/DaemonTests.swift` L59, L66 · `.../InteropTests.swift` L111–L112 · `.../LinkManagerTests.swift` L290 · `.../LargeFrameGateTests.swift` L208, L239, L263 · `.../ClipboardWatcherTests.swift` L331–L334

**Interfaces:**
- Consumes (verified present today): `Wire.protocolMinor = 2`, `Wire.maxPayload`, `Wire.legacyMaxPayload`, `WireFileEntry.init(name:content:hash:bytes:)`, `ClipPayload.files([(name: String, data: Data)])`, `WireMessage.clipFiles(files:ts:)`, `WireMessage.clip(_:ts:)`, `decodeFileEntries(_:) -> [(name: String, data: Data)]?`, `sanitizeFilename(_:) -> String`, `aggregateFilesHash(_:) -> String`, `sha256Hex(_:)`, `AnyLog.shared`, `strictBase64Decode(_:)`, `EncodedFrame`.
- Consumes from **Task 1** (Python wire): `formacOS/Scripts/gen-golden-vectors.py` emits the fixture `clip_files_path.bin` and manifest keys `files_path_names` `[String]`, `files_path_paths` `[String]`, `files_path_hashes` `[String]`, `files_path_aggregate` `String`, `files_path_total_bytes` `Int`, and the `hello.bin` vector now carries `"protocol_minor": 3`.
- Produces (Tasks 5 and 6 depend on these exact names):
  - `Wire.protocolMinor = 3`, `Wire.maxPathSegments = 32`, `Wire.maxPathLength = 240`
  - `WireFileEntry.path: String?` + `WireFileEntry.init(name: String, content: String, hash: String, bytes: Int, path: String? = nil)`
  - `ClipPayload.files([(name: String, data: Data, relPath: String?)])`
  - `WireMessage.clipFiles(files: [(name: String, data: Data, relPath: String?)], ts: Double) -> WireMessage`
  - `decodeFileEntries(_ files: [WireFileEntry]?) -> [(name: String, data: Data, relPath: String?)]?`
  - `public func isValidWirePath(_ path: String, name: String) -> Bool`
  - `public func sanitizeWirePath(_ path: String) -> String`
- **Shared decisions this task pins for Tasks 1 (Python) and 7 (C#)** — both must match or the same clip rebuilds a tree on one receiver and flat-places on another:
  - **The `<= 240 chars` length rule is counted in UNICODE SCALARS (code points), i.e. Python's `len()`.** Swift's `String.count` counts grapheme clusters and C#'s `string.Length` counts UTF-16 units, so all three disagree on any path with emoji or non-BMP characters (verified: 130 `🇰🇷` + `/a.txt` is 136 graphemes / 266 scalars / 526 UTF-16 units). Swift therefore uses `sanitizeWirePath(path).unicodeScalars.count`; C# must count runes (`EnumerateRunes().Count()`), NOT `.Length`.
  - **NFC is normalized, never rejected.** The contract lists NFC among the receiver's `path` rules, but it is a *normalization* rule, not a rejection rule: an NFD path is accepted and composed on the way to disk (`sanitizeWirePath` → `sanitizeFilename` NFC-normalizes every segment; Swift's `String ==` is canonical so NFD == NFC there anyway). Tasks 1 and 7 must ALSO accept NFD and normalize — a `path` rejected for being decomposed would flat-place on Python/C# what Swift rebuilds. Senders still emit NFC (`clipFiles` composes before validating).

Steps:

- [ ] Step 1: Precondition — confirm Task 1 has landed (the generator is the canonical encoder; Task 4 only runs it).
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && grep -n "clip_files_tree\|files_path_paths\|files_path_aggregate\|protocol_minor" formacOS/Scripts/gen-golden-vectors.py
  ```
  Expected: lines showing `"clip_files_path.bin"`, the `files_path_paths` / `files_path_aggregate` manifest keys, and `"protocol_minor": 3` in the hello vector. If any is missing, Task 1 has not landed — stop and run Task 1 first.

- [ ] Step 2: Write the failing wire tests. Edit `formacOS/Tests/AnyClipCoreTests/WireProtocolTests.swift`: change L41 to `#expect(body?["protocol_minor"] as? Int == 3)   // our live hello now advertises minor 3`, replace the body of `protocolMinorCoversFilesAnd64MiBFrames` (L194–L198) with the block below, add `relPath:` to the existing `.files`/`clipFiles` tuple literals at L125–L128, L166, L185–L188 (this is the ONLY place the `relPath:` ripple in this file is specified — Step 6 covers the other test files), add `#expect(ok?[0].relPath == nil)` after L181, and append the four new tests. Also bump the second live-minor assertion, `formacOS/Tests/AnyClipCoreTests/LargeFrameTests.swift` L16, to `#expect(Wire.protocolMinor == 3)` — `frameCapsAndProtocolMinor` pins the minor alongside the frame caps and would otherwise fail from Step 4 onward:
  ```swift
  @Test func protocolMinorCoversFilesFramesAndFolderTrees() {
      // Cumulative feature level: >= 1 accepts kind:"files", >= 2 accepts frames
      // up to 64 MiB (see LargeFrameTests), >= 3 rebuilds folder trees from the
      // optional per-entry "path". Minor 3 gates NOTHING on the send path.
      #expect(Wire.protocolMinor == 3)
  }

  @Test func clipFilesCarriesPathOnlyForFolderEntries() throws {
      let files: [(name: String, data: Data, relPath: String?)] = [
          (name: "a.txt", data: Data("one".utf8), relPath: "docs/a.txt"),
          (name: "loose.bin", data: Data([0, 1, 2]), relPath: nil),
      ]
      let msg = WireMessage.clipFiles(files: files, ts: 5.0)
      let entries = try #require(msg.files)
      #expect(entries[0].path == "docs/a.txt")
      #expect(entries[1].path == nil)
      // A nil path is OMITTED from the JSON, so every frame that exists today
      // stays byte-identical (loose files carry no path field at all).
      let frame = try msg.encodeFrame()
      let json = try JSONSerialization.jsonObject(with: frame.dropFirst(4)) as! [String: Any]
      let raw = json["files"] as! [[String: Any]]
      #expect(raw[0]["path"] as? String == "docs/a.txt")
      #expect(raw[1]["path"] == nil)
      #expect(raw[1].keys.sorted() == ["bytes", "content", "hash", "name"])
  }

  @Test func clipFilesNormalizesPathToNFCAndDropsInvalidPaths() {
      let nfd = "결과".decomposedStringWithCanonicalMapping
      let nfc = "결과".precomposedStringWithCanonicalMapping
      let m1 = WireMessage.clipFiles(
          files: [(name: nfd + ".txt", data: Data([1]), relPath: nfd + "/" + nfd + ".txt")],
          ts: 0)
      #expect(Array((m1.files?[0].path ?? "").utf8) == Array((nfc + "/" + nfc + ".txt").utf8))
      // The sender MUST emit only valid paths: a traversal path degrades to a
      // flat entry instead of a frame the receiver would have to reject.
      let m2 = WireMessage.clipFiles(
          files: [(name: "a.txt", data: Data([1]), relPath: "../a.txt")], ts: 0)
      #expect(m2.files?[0].path == nil)
      #expect(m2.files?[0].name == "a.txt")
  }

  @Test func wirePathValidationRules() {
      #expect(isValidWirePath("docs/a.txt", name: "a.txt"))
      #expect(isValidWirePath("docs/sub dir/a.txt", name: "a.txt"))
      #expect(!isValidWirePath("/docs/a.txt", name: "a.txt"))      // absolute
      #expect(!isValidWirePath("../a.txt", name: "a.txt"))         // traversal
      #expect(!isValidWirePath("docs/./a.txt", name: "a.txt"))     // dot segment
      #expect(!isValidWirePath("docs//a.txt", name: "a.txt"))      // empty segment
      #expect(!isValidWirePath("docs\\a.txt", name: "a.txt"))      // backslash
      #expect(!isValidWirePath("C:/docs/a.txt", name: "a.txt"))    // drive letter
      #expect(!isValidWirePath("docs/a.txt", name: "b.txt"))       // last segment != name
      #expect(!isValidWirePath("", name: "a.txt"))
      #expect(!isValidWirePath(String(repeating: "d/", count: 33) + "a.txt", name: "a.txt"))
      let deep = String(repeating: "0123456789/", count: 22) + "a.txt"   // 247 scalars
      #expect(deep.unicodeScalars.count > Wire.maxPathLength)
      #expect(!isValidWirePath(deep, name: "a.txt"))
      // Length is counted in UNICODE SCALARS (== Python len()), never in
      // graphemes or UTF-16 units. This path is 136 graphemes but 266 scalars
      // (and 526 UTF-16 units): a grapheme count would ACCEPT it while Python
      // rejects it, which is exactly the silent cross-implementation split the
      // 240 cap exists to prevent. Keep this case non-ASCII.
      let flags = String(repeating: "🇰🇷", count: 130) + "/a.txt"
      #expect(flags.count <= Wire.maxPathLength)                 // graphemes: 136
      #expect(flags.unicodeScalars.count > Wire.maxPathLength)   // scalars: 266
      #expect(!isValidWirePath(flags, name: "a.txt"))
      // NFD is normalized, never rejected (see the shared decision in
      // Interfaces — Tasks 1 and 7 must match): Swift's String == is canonical
      // and every segment goes through sanitizeFilename (NFC) on the way to disk.
      let nfd = "결과".decomposedStringWithCanonicalMapping
      #expect(isValidWirePath(nfd + "/" + nfd + ".txt", name: nfd + ".txt"))
      #expect(isValidWirePath(nfd + "/" + nfd + ".txt",
                              name: "결과".precomposedStringWithCanonicalMapping + ".txt"))
  }

  @Test func sanitizeWirePathSanitizesEverySegment() {
      #expect(sanitizeWirePath("docs/CON/a?b.txt") == "docs/_CON/a_b.txt")
      #expect(sanitizeWirePath("docs/sub/a.txt") == "docs/sub/a.txt")
  }

  @Test func decodeFileEntriesCarriesPathThroughRaw() {
      let tree = WireFileEntry(
          name: "a.txt", content: Data("x".utf8).base64EncodedString(),
          hash: sha256Hex(Data("x".utf8)), bytes: 1, path: "docs/a.txt")
      #expect(decodeFileEntries([tree])?[0].relPath == "docs/a.txt")
      // No path field -> relPath nil -> exactly today's flat behavior.
      let flat = WireFileEntry(
          name: "b.txt", content: Data("y".utf8).base64EncodedString(),
          hash: sha256Hex(Data("y".utf8)), bytes: 1)
      #expect(decodeFileEntries([flat])?[0].relPath == nil)
  }
  ```

- [ ] Step 3: Run them, expect FAIL — `isValidWirePath`, `sanitizeWirePath`, `Wire.maxPathLength` and `WireFileEntry.path` do not exist yet, so `AnyClipCoreTests` fails to compile ("cannot find 'isValidWirePath' in scope", "extra argument 'path' in call", "extra argument 'relPath' in call").
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter WirePath
  ```

- [ ] Step 4: Minimal implementation — `formacOS/Sources/AnyClipCore/WireProtocol.swift`. Replace L12–L15 with the bumped minor plus the two path caps:
  ```swift
      public static let protocolMajor = 1
      /// Cumulative feature level: minor >= 1 accepts kind:"files", minor >= 2
      /// accepts frames up to maxPayload (64 MiB) instead of legacyMaxPayload,
      /// minor >= 3 rebuilds folder trees from each entry's optional "path".
      /// Minor 3 is a capability MARKER only: it gates nothing on the send path.
      public static let protocolMinor = 3
      /// Wire-path caps for a kind:"files" entry's optional "path" field.
      /// Keep in lockstep with anyclip.MAX_PATH_SEGMENTS / MAX_PATH_LENGTH and
      /// C# Wire.MaxPathSegments / Wire.MaxPathLength.
      public static let maxPathSegments = 32
      public static let maxPathLength = 240
  ```
  Replace the `WireFileEntry` struct — L111–L122 in the PRE-task file, but the replacement above turned 4 lines into 11, so it now sits 7 lines lower; locate it by name, not by number — with the path-carrying entry:
  ```swift
  public struct WireFileEntry: Codable, Sendable, Equatable {
      public var name: String
      public var content: String
      public var hash: String
      public var bytes: Int
      /// Relative path INCLUDING the top folder name ("<top>/<sub>/<name>"),
      /// present only for files that came from a copied folder. Optional, so
      /// the synthesized encoder omits it (encodeIfPresent) and a loose file's
      /// entry is byte-identical to protocol 1.2. Peers below minor 3 ignore it.
      public var path: String?
      public init(name: String, content: String, hash: String, bytes: Int,
                  path: String? = nil) {
          self.name = name
          self.content = content
          self.hash = hash
          self.bytes = bytes
          self.path = path
      }
  }
  ```
  Append the two path helpers at the end of the file (after `decodeFileEntries`):
  ```swift
  /// Sanitized POSIX form of a wire path: every segment through sanitizeFilename
  /// (NFC + denylist + trailing dot/space trim + Windows reserved names),
  /// rejoined with "/". Used for the length rule below and by the receiver when
  /// it rebuilds the tree, so both judge the same string.
  public func sanitizeWirePath(_ path: String) -> String {
      path.split(separator: "/", omittingEmptySubsequences: false)
          .map { sanitizeFilename(String($0)) }
          .joined(separator: "/")
  }

  /// True when `path` satisfies EVERY wire rule for a folder entry's optional
  /// "path": POSIX "/" separators, relative (no leading "/", no drive letter),
  /// no "." / ".." / empty segments, no backslashes, last segment equals `name`,
  /// <= Wire.maxPathSegments segments, sanitized length <= Wire.maxPathLength.
  /// Senders MUST only emit paths that pass; receivers MUST verify before
  /// rebuilding a tree and fall back to FLAT placement for that ONE entry when
  /// they do not. NFC is not a rejection rule: Swift's String == is canonical
  /// (NFC == NFD) and sanitizeFilename normalizes every segment on the way to
  /// disk — Python and C# accept-and-normalize too, they do not reject NFD.
  /// LENGTH IS COUNTED IN UNICODE SCALARS (code points), matching Python's
  /// len(). String.count would count grapheme clusters and C# string.Length
  /// UTF-16 units, so those three disagree on any emoji/non-BMP path and the
  /// same clip would rebuild a tree on one receiver and flat-place on another;
  /// C# must count runes, not .Length. Keep in lockstep with
  /// anyclip.is_valid_wire_path and C# Wire.IsValidWirePath.
  public func isValidWirePath(_ path: String, name: String) -> Bool {
      guard !path.isEmpty, !path.contains(where: { $0 == "\\" }) else { return false }
      let segments = path.split(separator: "/", omittingEmptySubsequences: false)
          .map(String.init)
      guard !segments.isEmpty, segments.count <= Wire.maxPathSegments else { return false }
      for segment in segments where segment.isEmpty || segment == "." || segment == ".." {
          return false
      }
      let first = Array(segments[0])
      if first.count >= 2, first[1] == ":", first[0].isASCII, first[0].isLetter {
          return false   // drive letter ("C:/...")
      }
      guard segments[segments.count - 1] == name else { return false }
      return sanitizeWirePath(path).unicodeScalars.count <= Wire.maxPathLength
  }
  ```

- [ ] Step 5: Minimal implementation — the entry-shape change and its in-source ripple. Step 4's own edits shifted every line below `Wire` by +14, so address these BY SYMBOL, not by the pre-task line numbers: in `WireProtocol.swift` change `case files([(name: String, data: Data)])` inside `enum ClipPayload` to `case files([(name: String, data: Data, relPath: String?)])`, then replace the whole of `WireMessage.clipFiles(files:ts:)` and the whole of `decodeFileEntries(_:)` (its doc comment included) with:
  ```swift
      public static func clipFiles(
          files: [(name: String, data: Data, relPath: String?)], ts: Double
      ) -> WireMessage {
          var m = WireMessage(type: "clip")
          m.kind = "files"
          var entries: [WireFileEntry] = []
          var hashes: [String] = []
          var total = 0
          for f in files {
              let h = sha256Hex(f.data)
              let name = f.name.precomposedStringWithCanonicalMapping  // NFC on the wire
              var path: String?
              if let rel = f.relPath {
                  let nfc = rel.precomposedStringWithCanonicalMapping  // NFC on the wire
                  if isValidWirePath(nfc, name: name) {
                      path = nfc
                  } else {
                      AnyLog.shared.warning(
                          "invalid wire path '\(rel)' for \(name); sending it flat")
                  }
              }
              entries.append(WireFileEntry(
                  name: name, content: f.data.base64EncodedString(),
                  hash: h, bytes: f.data.count, path: path))
              hashes.append(h)
              total += f.data.count
          }
          m.files = entries
          m.hash = aggregateFilesHash(hashes)  // top-level hash = aggregate
          m.ts = ts
          m.bytes = total                       // sum of raw byte counts
          return m
      }
  ```
  ```swift
  /// Decode a kind:"files" message's entries into (name, rawBytes, relPath).
  /// Returns nil if the array is empty/nil OR ANY entry has non-strict base64
  /// content — the caller drops the WHOLE frame (no partial apply). Names AND
  /// paths pass through raw; sanitize/validate/uniquify happen write-side, so a
  /// bad path degrades that ONE entry to flat placement instead of killing the
  /// frame. Hashes are never trusted from the wire.
  public func decodeFileEntries(
      _ files: [WireFileEntry]?
  ) -> [(name: String, data: Data, relPath: String?)]? {
      guard let files, !files.isEmpty else { return nil }
      var out: [(name: String, data: Data, relPath: String?)] = []
      for e in files {
          guard let data = strictBase64Decode(e.content) else { return nil }
          out.append((name: e.name, data: data, relPath: e.path))
      }
      return out
  }
  ```
  Then the two compile ripples in `formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift` (folder logic itself is Task 5, flat placement is Task 6 — here only the shapes move):
  - L152: `var sendable: [(name: String, data: Data, relPath: String?)] = []`
  - L175: `sendable.append((name: url.lastPathComponent, data: data, relPath: nil))`
  - L218: `!updateLocalFiles([(name: name, data: data, relPath: nil)]).isEmpty`
  - L226: `public func updateLocalFiles(_ files: [(name: String, data: Data, relPath: String?)]) -> [(name: String, data: Data)] {`
  No other source file changes: `ClipPayload.payloadHash` and `WireMessage.clip(_:ts:)` (WireProtocol.swift), `PeerLink.handleClip` (PeerLink.swift), `Daemon.applyClip`'s `.files` branch, the `Daemon` log/toast switches and `downgradeForPeer` (Daemon.swift) all bind `let fs` and read `.name` / `.data` only.

- [ ] Step 6: Fix the test-side ripple so both suites compile — add `relPath: nil` to every 2-tuple literal that feeds `.files` / `clipFiles` / `updateLocalFiles`. `WireProtocolTests.swift` is NOT in this list: its literals were already widened in Step 2, do not edit them twice.
  - `Tests/AnyClipDaemonTests/DaemonTests.swift` L59 and L66: `ClipPayload.files([(name: "a", data: Data([1]), relPath: nil), (name: "b", data: Data([2]), relPath: nil)])`
  - `Tests/AnyClipDaemonTests/InteropTests.swift` L111–L112: `let mf1 = (name: "노트-multi.txt", data: Data("files body one".utf8), relPath: String?.none)` and `let mf2 = (name: "(E&S) plan.txt", data: Data("files body two".utf8), relPath: String?.none)`
  - `Tests/AnyClipDaemonTests/LinkManagerTests.swift` L290–L291 and `Tests/AnyClipDaemonTests/LargeFrameGateTests.swift` L208–L211, L239–L242, L263–L266: widen each `let files: [(name: String, data: Data)]` annotation to `[(name: String, data: Data, relPath: String?)]` and add `relPath: nil` to every element
  - `Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift` L331–L334: `(name: "dup.txt", data: Data("1".utf8), relPath: nil)` / `(name: "dup.txt", data: Data("2".utf8), relPath: nil)`

- [ ] Step 7: Run the full suite, expect the FULL suite to PASS — the golden work starts in Step 8, so nothing golden has been touched yet.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS
  ```
  Expected: everything green, `goldenHelloDecodes` included — it asserts against the FIXTURE's `protocol_minor` (still 0, Python has not regenerated it yet), not against `Wire.protocolMinor`. The tree vector does not exist yet, and no test references it.

- [ ] Step 8: Write the failing golden test. In `formacOS/Tests/AnyClipCoreTests/GoldenVectorTests.swift`, inside `goldenHelloDecodes`, change the `protocol_minor` assertion — L37, the line AFTER `#expect(m.protocol_major == 1)` — from `#expect(m.protocol_minor == 0)` to `#expect(m.protocol_minor == 3)`, leaving the `protocol_major` assertion in place, and append:
  ```swift
  @Test func goldenClipFilesTreeDecodes() throws {
      let m = try decodeGoldenFrame("clip_files_path.bin")
      let man = try manifest()
      #expect(m.kind == "files")
      let entries = try #require(m.files)
      let names = man["files_path_names"] as! [String]
      let paths = man["files_path_paths"] as! [String]
      let hashes = man["files_path_hashes"] as! [String]
      #expect(entries.count == paths.count)
      for (i, e) in entries.enumerated() {
          #expect(e.name == names[i])
          #expect(e.path == paths[i])                       // Python-canonical path
          let path = try #require(e.path)
          #expect(isValidWirePath(path, name: e.name))      // our rules accept Python's
          let data = try #require(strictBase64Decode(e.content))
          #expect(sha256Hex(data) == hashes[i])
          #expect(e.bytes == data.count)
      }
      // Aggregate + total match the Python-canonical manifest values: adding
      // "path" must not change how a files clip hashes.
      #expect(m.hash == man["files_path_aggregate"] as? String)
      #expect(aggregateFilesHash(hashes) == man["files_path_aggregate"] as? String)
      #expect(m.bytes == man["files_path_total_bytes"] as? Int)
  }
  ```

- [ ] Step 9: Run it, expect FAIL — `goldenClipFilesTreeDecodes` crashes on the force-unwrapped fixture lookup (`clip_files_path.bin` is not in the bundle) and `goldenHelloDecodes` fails with `m.protocol_minor == 0`.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter golden
  ```

- [ ] Step 10: Regenerate the fixtures with the canonical Python encoder (this is the ONE place fixtures are regenerated for the whole feature) and eyeball the new vector.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && python3 formacOS/Scripts/gen-golden-vectors.py && git status --short formacOS/Tests/AnyClipCoreTests/Fixtures && python3 -c "import json,pathlib; b=pathlib.Path('formacOS/Tests/AnyClipCoreTests/Fixtures/clip_files_path.bin').read_bytes(); print(json.dumps(json.loads(b[4:].decode()), ensure_ascii=False)[:400])"
  ```
  Expected: `hello.bin`, `manifest.json` modified, `clip_files_path.bin` untracked; the printed frame shows `"kind": "files"` with a `"path"` on each entry.

- [ ] Step 11: Run the full suite, expect PASS (both native suites assert the same vector; C# is Task 7).
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS
  ```

- [ ] Step 12: Commit.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && git add formacOS/Sources/AnyClipCore/WireProtocol.swift formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift formacOS/Tests/AnyClipCoreTests formacOS/Tests/AnyClipDaemonTests && git commit -m "$(cat <<'EOF'
  feat(wire-swift): protocol minor 3 + optional per-entry path on kind:"files"

  Wire.protocolMinor 2 -> 3 (cumulative: >=1 files, >=2 64 MiB frames, >=3
  folder trees; a capability marker only, it gates nothing on send).
  WireFileEntry gains an optional "path"; loose entries omit the field, so
  every frame that exists today stays byte-identical. ClipPayload.files now
  carries (name, data, relPath) and decodeFileEntries passes the path through
  raw -- validation is write-side, so a bad path degrades one entry to flat
  placement instead of dropping the frame. Adds isValidWirePath /
  sanitizeWirePath (POSIX, relative, no dot segments/backslashes/drive
  letters, last segment == name, <=32 segments, <=240 sanitized chars).
  Golden fixtures regenerated from the canonical Python encoder: hello now
  minor 3, plus a new files-with-path vector.

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  EOF
  )"
  ```

---

### Task 5: Swift sender — folder expansion, per-folder all-or-nothing, minor-0 exclusion

**Files:**
- Create `formacOS/Sources/AnyClipDaemon/FolderExpander.swift`
- Modify `formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift` (`maxFilesPerClip` L53, init baseline L85, `checkFileClipboard` L143–L195, new `fingerprints(for:)` helper)
- Modify `formacOS/Sources/AnyClipDaemon/Daemon.swift` (`downgradeForPeer` L51–L62)
- Modify `formacOS/Sources/AnyClipDaemon/LinkManager.swift` (`broadcast` L403–L450)
- Create test `formacOS/Tests/AnyClipDaemonTests/FolderExpanderTests.swift`
- Test `formacOS/Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift` (rewrite L106–L122, L242–L260, L262–L283, L307–L324; add 3 tests)
- Test `formacOS/Tests/AnyClipDaemonTests/DaemonTests.swift` (append 2 tests — the file ends at L78)
- Test `formacOS/Tests/AnyClipDaemonTests/LargeFrameGateTests.swift` (add 1 test — it owns the only `AnyLog.shared` capture, see Step 9)

**Interfaces:**
- Consumes: `ClipPayload.files([(name: String, data: Data, relPath: String?)])` (Task 4), `ClipboardWatcher.fileBudget = Int(Double(Wire.maxPayload - 256 * 1024) * 0.74)`, `ClipboardWatcher.maxFilesPerClip`, `FileFingerprint.init?(url:)` with fields `path/size/mtimeNs/isDirectory`, `ClipboardWatcher.grabFileURLs(_:)`, `ClipboardWatcher.Callbacks.onFileSkipped: ((String) async -> Void)?`, `downgradeForPeer(_:peerMinor:) -> (payload: ClipPayload?, dropped: Int)`, `LinkManager.broadcast(_:) -> BroadcastResult`, `PeerLink.peerProtocolMinor`, `PeerLink.peerName`, `Wire.linkAcceptsFrame(bytes:peerMinor:)`, `AnyLog.shared`.
- Produces (Task 6 depends on `FolderExpander.walk` and `fingerprints(for:)`):
  - `struct FolderFile: Equatable, Sendable { let url: URL; let relPath: String; let size: Int }`
  - `enum FolderExpander { static let junkNames: Set<String>; static func walk(_ folder: URL) -> [FolderFile] }`
  - `ClipboardWatcher.fingerprints(for urls: [URL]) -> [FileFingerprint]` (static, internal)
  - `ClipboardWatcher.maxFilesPerClip = 500`

Steps:

- [ ] Step 1: Write the failing walker tests — create `formacOS/Tests/AnyClipDaemonTests/FolderExpanderTests.swift`:
  ```swift
  import Testing
  import Foundation
  @testable import AnyClipDaemon
  @testable import AnyClipCore

  private func tempDir() -> URL {
      let url = FileManager.default.temporaryDirectory
          .appendingPathComponent("anyclip-walk-\(UUID().uuidString)")
      try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
      return url
  }

  private func write(_ dir: URL, _ rel: String, _ body: String) throws {
      let target = rel.split(separator: "/").reduce(dir) { $0.appendingPathComponent(String($1)) }
      try FileManager.default.createDirectory(
          at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
      try Data(body.utf8).write(to: target)
  }

  @Test func walkSortsByRelPathBytesAndPrefixesTheTopName() throws {
      let root = tempDir()
      let top = root.appendingPathComponent("docs", isDirectory: true)
      try write(top, "c.txt", "c")
      try write(top, "a.txt", "a")
      try write(top, "sub/b.txt", "b")
      try write(top, "sub/a.txt", "sa")
      let got = FolderExpander.walk(top)
      #expect(got.map(\.relPath)
          == ["docs/a.txt", "docs/c.txt", "docs/sub/a.txt", "docs/sub/b.txt"])
      #expect(got.map(\.size) == [1, 1, 2, 1])
  }

  @Test func walkExcludesJunkAndNeverFollowsSymlinks() throws {
      let root = tempDir()
      let top = root.appendingPathComponent("docs", isDirectory: true)
      try write(top, "keep.txt", "k")
      try write(top, ".DS_Store", "junk")
      try write(top, "sub/Thumbs.db", "junk")
      try write(top, "sub/desktop.ini", "junk")
      let outside = root.appendingPathComponent("outside.txt")
      try Data("secret".utf8).write(to: outside)
      try FileManager.default.createSymbolicLink(
          at: top.appendingPathComponent("link.txt"), withDestinationURL: outside)
      try FileManager.default.createSymbolicLink(
          at: top.appendingPathComponent("linkdir"), withDestinationURL: root)
      #expect(FolderExpander.walk(top).map(\.relPath) == ["docs/keep.txt"])
  }

  @Test func walkDropsEmptyDirectories() throws {
      let root = tempDir()
      let top = root.appendingPathComponent("docs", isDirectory: true)
      try FileManager.default.createDirectory(
          at: top.appendingPathComponent("empty/deeper"), withIntermediateDirectories: true)
      #expect(FolderExpander.walk(top).isEmpty)
      try write(top, "empty/deeper/x.txt", "x")
      #expect(FolderExpander.walk(top).map(\.relPath) == ["docs/empty/deeper/x.txt"])
  }

  @Test func walkEmitsNFCRelPathsForKoreanNames() throws {
      let root = tempDir()
      let nfd = "결과".decomposedStringWithCanonicalMapping
      let nfc = "결과".precomposedStringWithCanonicalMapping
      let top = root.appendingPathComponent(nfd, isDirectory: true)
      try write(top, nfd + ".txt", "x")
      let got = FolderExpander.walk(top)
      #expect(got.count == 1)
      // Swift's == is canonical, so assert on the actual UTF-8 bytes.
      #expect(Array(got[0].relPath.utf8) == Array((nfc + "/" + nfc + ".txt").utf8))
      #expect(FileManager.default.fileExists(atPath: got[0].url.path))   // read path unchanged
  }
  ```

- [ ] Step 2: Run them, expect FAIL — `AnyClipDaemonTests` does not compile: "cannot find 'FolderExpander' in scope".
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter walk
  ```

- [ ] Step 3: Minimal implementation — create `formacOS/Sources/AnyClipDaemon/FolderExpander.swift`:
  ```swift
  import Foundation
  import AnyClipCore

  /// One file found inside a copied folder: where to read it from, the relative
  /// wire path it travels under (top folder name included, NFC, POSIX "/"), and
  /// its raw size for the budget arithmetic.
  public struct FolderFile: Equatable, Sendable {
      public let url: URL
      public let relPath: String
      public let size: Int
      public init(url: URL, relPath: String, size: Int) {
          self.url = url
          self.relPath = relPath
          self.size = size
      }
  }

  /// Recursive expansion of a copied folder into wire entries.
  /// Keep in lockstep with anyclip.expand_folder and C# FolderExpander.Walk.
  public enum FolderExpander {
      /// Never synced, never counted, log-only.
      public static let junkNames: Set<String> = [".DS_Store", "Thumbs.db", "desktop.ini"]

      /// Files only, symlinks never followed (which also makes cycles
      /// impossible), junk excluded, empty directories dropped (they are not
      /// representable on the wire). Sorted byte-wise on relPath so the same
      /// tree always produces the same clip.
      public static func walk(_ folder: URL) -> [FolderFile] {
          let top = folder.lastPathComponent.precomposedStringWithCanonicalMapping
          var out: [FolderFile] = []
          collect(dir: folder, prefix: top, into: &out)
          out.sort { Array($0.relPath.utf8).lexicographicallyPrecedes(Array($1.relPath.utf8)) }
          return out
      }

      private static func collect(dir: URL, prefix: String, into out: inout [FolderFile]) {
          let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey]
          guard let children = try? FileManager.default.contentsOfDirectory(
              at: dir, includingPropertiesForKeys: keys, options: [])
          else {
              AnyLog.shared.warning("folder read failed for \(dir.path); skipping")
              return
          }
          for child in children {
              let name = child.lastPathComponent.precomposedStringWithCanonicalMapping
              let values = try? child.resourceValues(forKeys: Set(keys))
              // Symlink check FIRST: isDirectory follows the link, isSymbolicLink
              // does not, so this is what keeps us off the far side of a link.
              if values?.isSymbolicLink == true {
                  AnyLog.shared.info("symlink not synced (never followed): \(child.path)")
                  continue
              }
              if junkNames.contains(name) {
                  AnyLog.shared.debug("junk excluded from folder sync: \(child.path)")
                  continue
              }
              if values?.isDirectory == true {
                  collect(dir: child, prefix: prefix + "/" + name, into: &out)
                  continue
              }
              out.append(FolderFile(
                  url: child, relPath: prefix + "/" + name, size: values?.fileSize ?? 0))
          }
      }
  }
  ```

- [ ] Step 4: Run them, expect PASS.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter walk
  ```

- [ ] Step 5: Write the failing watcher tests. In `formacOS/Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift` replace `folderOnClipboardIsSkippedWithToastOnce` (L106–L122), `folderMixedWithFilesSkipsFolderSyncsFiles` (L242–L260), `multipleFoldersEmitOneAggregatedSkip` (L262–L283) and `maxFilesCapEmitsAtMostOneHundred` (L307–L324) with:
  ```swift
  /// Materialize `rel` under `dir` (creating intermediate dirs) with `body`.
  private func writeFile(_ dir: URL, _ rel: String, _ body: String) throws -> URL {
      let target = rel.split(separator: "/").reduce(dir) { $0.appendingPathComponent(String($1)) }
      try FileManager.default.createDirectory(
          at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
      try Data(body.utf8).write(to: target)
      return target
  }

  @Test @MainActor func folderOnClipboardExpandsIntoFilesWithPaths() async throws {
      let pb = privatePasteboard()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
      let root = tempDir()
      let folder = root.appendingPathComponent("docs", isDirectory: true)
      _ = try writeFile(folder, "a.txt", "one")
      _ = try writeFile(folder, "sub/b.txt", "two")
      pb.clearContents()
      pb.writeObjects([folder as NSURL])
      await watcher.pollOnceForTesting()
      let got = changes.get()
      #expect(got.count == 1)
      if case .files(let fs) = got[0] {
          #expect(fs.map(\.name) == ["a.txt", "b.txt"])
          #expect(fs.map(\.relPath) == ["docs/a.txt", "docs/sub/b.txt"])
          #expect(fs[0].data == Data("one".utf8))
      } else { Issue.record("expected .files, got \(got)") }
      #expect(skipped.get().isEmpty)
      // Same copy is never re-detected (fingerprints cover the expanded tree).
      await watcher.pollOnceForTesting()
      #expect(changes.get().count == 1)
  }

  @Test @MainActor func emptyFolderToastsAndSendsNothing() async throws {
      let pb = privatePasteboard()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
      let folder = tempDir()   // a real, empty directory
      pb.clearContents()
      pb.writeObjects([folder as NSURL])
      await watcher.pollOnceForTesting()
      #expect(changes.get().isEmpty)
      #expect(skipped.get() == ["folder is empty; nothing to sync"])
      await watcher.pollOnceForTesting()
      #expect(skipped.get().count == 1)          // not re-detected
  }

  @Test @MainActor func folderMixedWithLooseFilesKeepsSelectionOrder() async throws {
      let pb = privatePasteboard()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
      let dir = tempDir()
      let folder = dir.appendingPathComponent("sub", isDirectory: true)
      _ = try writeFile(folder, "inner.txt", "i")
      let loose = try writeFile(dir, "a.txt", "one")
      pb.clearContents()
      pb.writeObjects([folder as NSURL, loose as NSURL])
      await watcher.pollOnceForTesting()
      let got = changes.get()
      #expect(got.count == 1)
      if case .files(let fs) = got[0] {
          // Selection order: the folder's entries first, then the loose file.
          #expect(fs.map(\.relPath) == ["sub/inner.txt", nil])
          #expect(fs.map(\.name) == ["inner.txt", "a.txt"])
      } else { Issue.record("expected .files, got \(got)") }
      #expect(skipped.get().isEmpty)
  }

  @Test @MainActor func twoEmptyFoldersEmitOneEmptyToastAndTheLooseFileStillSyncs() async throws {
      let pb = privatePasteboard()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
      let dir = tempDir()
      let d1 = dir.appendingPathComponent("one", isDirectory: true)
      let d2 = dir.appendingPathComponent("two", isDirectory: true)
      try FileManager.default.createDirectory(at: d1, withIntermediateDirectories: true)
      try FileManager.default.createDirectory(at: d2, withIntermediateDirectories: true)
      let keep = try writeFile(dir, "keep.txt", "k")
      pb.clearContents()
      pb.writeObjects([d1 as NSURL, d2 as NSURL, keep as NSURL])
      await watcher.pollOnceForTesting()
      let got = changes.get()
      #expect(got.count == 1)
      if case .file(let name, _) = got[0] { #expect(name == "keep.txt") }
      else { Issue.record("expected single-file payload, got \(got)") }
      #expect(skipped.get() == ["folder is empty; nothing to sync"])
  }

  @Test @MainActor func oversizeFolderIsAllOrNothingWithItsOwnToast() async throws {
      let pb = privatePasteboard()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
      let dir = tempDir()
      let folder = dir.appendingPathComponent("huge", isDirectory: true)
      try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
      // Two sparse halves: each fits alone, the TOTAL does not -> the WHOLE
      // folder is skipped before a single byte is read (no partial trees).
      let each = ClipboardWatcher.fileBudget / 2 + 1
      _ = try sparseFile(folder.appendingPathComponent("p1.bin"), size: each)
      _ = try sparseFile(folder.appendingPathComponent("p2.bin"), size: each)
      let loose = try writeFile(dir, "small.txt", "s")
      pb.clearContents()
      pb.writeObjects([folder as NSURL, loose as NSURL])
      await watcher.pollOnceForTesting()
      let got = changes.get()
      #expect(got.count == 1)
      if case .file(let name, _) = got[0] { #expect(name == "small.txt") }
      else { Issue.record("expected the loose file only, got \(got)") }
      #expect(skipped.get() == ["folder too large to sync: huge"])
  }

  @Test @MainActor func folderOverTheFileCountCapIsSkippedWholly() async throws {
      let pb = privatePasteboard()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
      let dir = tempDir()
      let folder = dir.appendingPathComponent("many", isDirectory: true)
      try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
      for i in 0..<(ClipboardWatcher.maxFilesPerClip + 1) {
          try Data("x".utf8).write(to: folder.appendingPathComponent("f\(i).txt"))
      }
      pb.clearContents()
      pb.writeObjects([folder as NSURL])
      await watcher.pollOnceForTesting()
      #expect(changes.get().isEmpty)
      #expect(skipped.get() == ["folder too large to sync: many"])
  }

  @Test @MainActor func maxFilesCapEmitsAtMostFiveHundred() async throws {
      let pb = privatePasteboard()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
      let dir = tempDir()
      var urls: [NSURL] = []
      for i in 0..<(ClipboardWatcher.maxFilesPerClip + 1) {
          let u = dir.appendingPathComponent("f\(i).txt")
          try Data("x".utf8).write(to: u)
          urls.append(u as NSURL)
      }
      pb.clearContents(); pb.writeObjects(urls)
      await watcher.pollOnceForTesting()
      let got = changes.get()
      #expect(got.count == 1)
      if case .files(let fs) = got[0] { #expect(fs.count == 500) }
      else { Issue.record("expected .files") }
      #expect(ClipboardWatcher.maxFilesPerClip == 500)   // in lockstep with Python
      #expect(skipped.get() == ["1 file(s) skipped (too large to sync)"])
  }
  ```

- [ ] Step 6: Run them, expect FAIL — `folderOnClipboardExpandsIntoFilesWithPaths` records "expected .files" (the watcher still logs "folder on clipboard not synced (unsupported)" and emits nothing), the toast assertions fail against `"folder not synced — folders are not supported: …"`, and `maxFilesCapEmitsAtMostFiveHundred` fails on `fs.count == 100`.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter ClipboardWatcher
  ```

- [ ] Step 7: Minimal implementation — `formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift`. Replace L52–L53 with:
  ```swift
      /// Sender-side cap; the receiver stays lenient. Matches MAX_FILES_PER_CLIP.
      /// 100 -> 500 for folder sync (protocol 1.3): a document tree passes 100
      /// trivially and fileBudget is the real limit — worst-case extra JSON
      /// envelope still fits the 256 KB reservation baked into fileBudget.
      static let maxFilesPerClip = 500
  ```
  Replace the init baseline at L85 with `lastFileFingerprints = Self.fingerprints(for: Self.grabFileURLs(pasteboard))`, replace `checkFileClipboard` (L143–L195) with the expanding version, and add the fingerprint helper next to `grabFileURLs`:
  ```swift
      private func checkFileClipboard() async {
          let urls = Self.grabFileURLs(pasteboard)
          guard !urls.isEmpty else { return }
          let fingerprints = Self.fingerprints(for: urls)
          guard fingerprints != lastFileFingerprints else { return }
          // Record FIRST so the same selection is never re-detected (no retry loop),
          // regardless of what we end up sending.
          lastFileFingerprints = fingerprints

          var sendable: [(name: String, data: Data, relPath: String?)] = []
          var running = 0
          var skippedForSize = 0
          // Folders that did not fit are named one toast each (the pinned string
          // carries the folder name); empty folders share ONE generic toast.
          var oversizeFolders: [String] = []
          var emptyFolders = 0
          for url in urls {
              guard let fp = FileFingerprint(url: url) else { continue }
              if fp.isDirectory {
                  let top = url.lastPathComponent.precomposedStringWithCanonicalMapping
                  let entries = FolderExpander.walk(url)
                  if entries.isEmpty {
                      AnyLog.shared.info("folder \(top) has nothing syncable; skipping")
                      emptyFolders += 1
                      continue
                  }
                  // Per-folder ALL-OR-NOTHING against what the selection has left:
                  // total the sizes BEFORE reading a single byte. No partial trees.
                  let total = entries.reduce(0) { $0 + $1.size }
                  guard sendable.count + entries.count <= Self.maxFilesPerClip,
                        running + total <= Self.fileBudget
                  else {
                      AnyLog.shared.info(
                          "folder \(top) skipped: \(entries.count) file(s) / \(total) bytes "
                          + "do not fit the remaining budget")
                      oversizeFolders.append(top)
                      continue
                  }
                  running += total
                  for entry in entries {
                      guard let data = try? Data(contentsOf: entry.url) else {
                          // A read failure is per-file, exactly like a loose file:
                          // the rest of the tree still goes.
                          AnyLog.shared.warning("file read failed for \(entry.url.path); skipping")
                          continue
                      }
                      sendable.append((
                          name: entry.url.lastPathComponent.precomposedStringWithCanonicalMapping,
                          data: data, relPath: entry.relPath))
                  }
                  continue
              }
              // Loose files keep today's greedy per-file behaviour.
              if sendable.count >= Self.maxFilesPerClip || running + fp.size > Self.fileBudget {
                  skippedForSize += 1
                  continue
              }
              guard let data = try? Data(contentsOf: url) else {
                  AnyLog.shared.warning("file read failed for \(url.path); skipping")
                  continue
              }
              running += fp.size
              sendable.append((name: url.lastPathComponent, data: data, relPath: nil))
          }
          if let onSkipped = callbacks.onFileSkipped {
              for name in oversizeFolders {
                  await onSkipped("folder too large to sync: \(name)")
              }
              if emptyFolders > 0 {
                  await onSkipped("folder is empty; nothing to sync")
              }
              if skippedForSize > 0 {
                  await onSkipped("\(skippedForSize) file(s) skipped (too large to sync)")
              }
          }
          // 0 sendable -> nothing. Exactly 1 LOOSE file -> legacy .file. Anything
          // else (>= 2 files, or a single file that must carry its path) -> .files.
          if sendable.count == 1, sendable[0].relPath == nil {
              await callbacks.onChange(.file(name: sendable[0].name, data: sendable[0].data))
          } else if !sendable.isEmpty {
              await callbacks.onChange(.files(sendable))
          }
      }
  ```
  ```swift
      /// Fingerprint of a whole selection: every top-level item PLUS every file
      /// inside a copied folder, so an edit deep in a tree re-triggers a send and
      /// a just-placed tree cannot echo. The comparison and both baselines
      /// (startup, inbound placement) go through here, so the two sides always
      /// see the same shape.
      static func fingerprints(for urls: [URL]) -> [FileFingerprint] {
          var out: [FileFingerprint] = []
          for url in urls {
              guard let fp = FileFingerprint(url: url) else { continue }
              out.append(fp)
              if fp.isDirectory {
                  for entry in FolderExpander.walk(url) {
                      if let sub = FileFingerprint(url: entry.url) { out.append(sub) }
                  }
              }
          }
          return out
      }
  ```

- [ ] Step 8: Run the watcher tests, expect PASS.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter ClipboardWatcher
  ```

- [ ] Step 9: Write the failing fan-out tests. Append to `formacOS/Tests/AnyClipDaemonTests/DaemonTests.swift`:
  ```swift
  @Test func downgradeExcludesFolderEntriesForAMinorZeroPeer() {
      let payload = ClipPayload.files([
          (name: "a.txt", data: Data([1]), relPath: "docs/a.txt"),
          (name: "loose.txt", data: Data([2]), relPath: nil),
      ])
      let (out, dropped) = downgradeForPeer(payload, peerMinor: 0)
      #expect(dropped == 1)
      if case .file(let name, let data)? = out {
          #expect(name == "loose.txt")            // the first LOOSE file, not the tree entry
          #expect(data == Data([2]))
      } else { Issue.record("expected the first loose file, got \(String(describing: out))") }
  }

  @Test func folderOnlyClipSendsNothingToAMinorZeroPeer() {
      let payload = ClipPayload.files([
          (name: "a.txt", data: Data([1]), relPath: "docs/a.txt"),
          (name: "b.txt", data: Data([2]), relPath: "docs/b.txt"),
      ])
      let (out, dropped) = downgradeForPeer(payload, peerMinor: 0)
      #expect(out == nil)         // nothing a kind:"file" frame could carry
      #expect(dropped == 0)       // and therefore no "files not synced" toast
  }
  ```
  Append to `formacOS/Tests/AnyClipDaemonTests/LargeFrameGateTests.swift` (this file owns the ONLY `AnyLog.shared` capture — a second file calling `AnyLog.shared.configure` would re-point the shared logger under the parallel test runner, so the log-line assertion has to live here):
  ```swift
  // ---- folder fan-out (protocol 1.3) --------------------------------------

  @Test func folderClipFansOutWithFlattenAndMinorZeroLogs() async throws {
      _ = sharedGateLogURL
      let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
      let a = await makeManager(token: "tok", port: 28559, name: "a", clips: clips, events: events)
      let serve = Task { try await a.serve() }; defer { serve.cancel() }
      #expect(await waitUntil { await a.isServing })

      let old = try await rawPeer(port: 28559, token: "tok", nodeID: "o", name: "flat-old", minor: 0)
      let mid = try await rawPeer(port: 28559, token: "tok", nodeID: "m", name: "flat-mid", minor: 2)
      defer { old.cancel(); mid.cancel() }
      #expect(await waitUntil { await a.activeLinkCount() == 2 })

      let files: [(name: String, data: Data, relPath: String?)] = [
          (name: "a.txt", data: Data("one".utf8), relPath: "docs/a.txt"),
          (name: "b.txt", data: Data("two".utf8), relPath: "docs/sub/b.txt"),
      ]
      let result = await a.broadcast(.files(files))
      // minor 2 gets the SAME frame, paths intact (it flattens them itself);
      // minor 0 gets nothing at all, and neither link is dropped.
      #expect(result.delivered.map(\.peerName) == ["flat-mid"])
      #expect(result.maxDropped == 0)
      #expect(result.sizeSkipped.isEmpty)
      let got = try await withTimeout(seconds: 5) { try await mid.receiveMessage() }
      #expect(got?.kind == "files")
      #expect(got?.files?.count == 2)
      #expect(got?.files?[0].path == "docs/a.txt")
      #expect(sharedLogText().contains("peer flat-mid will flatten folders (protocol < 1.3)"))
      #expect(sharedLogText().contains("folder clip not sent to 'flat-old'"))
      #expect(await a.activeLinkCount() == 2)
      await a.shutdown()
  }
  ```

- [ ] Step 10: Run them, expect FAIL — BOTH commands, run separately. Chaining them with `&&` would swallow the second run, because the first is expected to exit non-zero.
  `downgradeForPeer` still returns `.file(name: "a.txt")` for the mixed clip and a non-nil payload for the folder-only clip:
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter "downgrade"
  ```
  and `folderClipFansOutWithFlattenAndMinorZeroLogs` fails because neither log line is emitted (and the minor-0 link is still handed the first tree entry):
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter folderClipFansOut
  ```

- [ ] Step 11: Minimal implementation. Replace `downgradeForPeer` in `formacOS/Sources/AnyClipDaemon/Daemon.swift` (L51–L62):
  ```swift
  /// Decide what to actually send given the peer's protocol minor. Minor >= 1
  /// understands kind:"files" (pass through, dropped == 0) — including each
  /// entry's optional path, which a peer below minor 3 simply ignores and writes
  /// flat. Minor 0 predates multi-file sync: degrade to the first LOOSE file as
  /// legacy kind:"file". Folder-derived entries are EXCLUDED from that fallback
  /// (a tree cannot be expressed in one kind:"file" frame), so a folder-only
  /// clip sends NOTHING on a minor-0 link — logged, never toasted. `dropped`
  /// counts the entries left behind for the notification.
  /// Returns a nil payload for an empty .files batch and for a folder-only clip
  /// to a minor-0 peer. Keep in lockstep with anyclip.downgrade_for_peer.
  public func downgradeForPeer(
      _ payload: ClipPayload, peerMinor: Int
  ) -> (payload: ClipPayload?, dropped: Int) {
      guard case .files(let fs) = payload, peerMinor < 1 else { return (payload, 0) }
      guard let first = fs.first(where: { $0.relPath == nil }) else { return (nil, 0) }
      return (.file(name: first.name, data: first.data), fs.count - 1)
  }
  ```
  In `formacOS/Sources/AnyClipDaemon/LinkManager.swift`, extend the `broadcast` doc block (the two new bullets go after the existing `minor < 2` bullet, L396–L398) and rewrite the loop head (L403–L415, `public func broadcast` through the `guard let outPayload = maybe else { continue }`):
  ```swift
      ///  - minor 1-2: folder entries are sent AS IS (the peer ignores "path" and
      ///    writes the files flat); logged once per clip per affected link.
      ///  - minor 0 + folder-only clip: nothing to send on that link (log only).
      public func broadcast(_ payload: ClipPayload) async -> BroadcastResult {
          var delivered: [(peerName: String, payload: ClipPayload)] = []
          var sizeSkipped: [String] = []
          var maxDropped = 0
          let ts = Date().timeIntervalSince1970
          // Variant kind ("text"/"image"/"file"/"files") -> its encoded frame, or
          // nil when the payload does not fit even the 64 MiB cap.
          var frames: [String: EncodedFrame?] = [:]
          // Evaluated ONCE per clip: does this payload carry folder entries?
          var hasFolders = false
          if case .files(let fs) = payload { hasFolders = fs.contains { $0.relPath != nil } }

          for entry in links.values {
              let link = entry.link
              let (maybe, dropped) = downgradeForPeer(payload, peerMinor: link.peerProtocolMinor)
              guard let outPayload = maybe else {
                  if hasFolders {
                      AnyLog.shared.info(
                          "folder clip not sent to '\(link.peerName)' "
                          + "(peer protocol 1.0 cannot receive folders)")
                  }
                  continue
              }
              if hasFolders, (1...2).contains(link.peerProtocolMinor) {
                  AnyLog.shared.info(
                      "peer \(link.peerName) will flatten folders (protocol < 1.3)")
              }
  ```
  (the rest of the loop — variant caching, `Wire.linkAcceptsFrame` gate, `sendEncoded`, `delivered.append` — is unchanged).

- [ ] Step 12: Run the whole Swift suite, expect PASS.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS
  ```

- [ ] Step 13: Commit.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && git add formacOS/Sources/AnyClipDaemon formacOS/Tests/AnyClipDaemonTests && git commit -m "$(cat <<'EOF'
  feat(watcher-swift): expand copied folders into path-carrying files clips

  Where the watcher used to skip a directory it now walks it: files only,
  symlinks never followed, .DS_Store/Thumbs.db/desktop.ini excluded, empty
  dirs dropped, sorted byte-wise on the relative path. Each folder is
  all-or-nothing against the REMAINING budget/count (toast "folder too large
  to sync: <name>"); loose files keep today's greedy behaviour. Empty folders
  toast "folder is empty; nothing to sync". maxFilesPerClip 100 -> 500;
  fileBudget untouched. Fingerprints now cover the expanded tree so a folder
  is neither re-detected nor echoed. Fan-out: folder entries are excluded from
  the minor-0 first-file fallback (folder-only clip sends nothing there, log
  only) and minor 1-2 peers get the same frame with one flatten log per clip.

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  EOF
  )"
  ```

---

### Task 6: Swift receiver — rebuild the tree under `received/`, place top-level items, interop guard

**Files:**
- Create `formacOS/Sources/AnyClipCore/ReceivedTree.swift`
- Modify `formacOS/Sources/AnyClipCore/TextHelpers.swift` (`uniquifyNames` L56–L89, doc comment included — this file is untouched by Tasks 4/5, so the numbers still hold)
- **Anchors below are BY SYMBOL on purpose**: Tasks 4 and 5 already rewrote these files (Task 5 alone grows `checkFileClipboard` from 53 to ~85 lines, adds `fingerprints(for:)`, and grows `downgradeForPeer`'s doc block from 5 to 10 lines), so every pre-task line number in them is stale. Locate by name.
- Modify `formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift` (`updateLocalFile(name:data:)`, `updateLocalFiles(_:)`)
- Modify `formacOS/Sources/AnyClipDaemon/Daemon.swift` (the `.files` branch of `applyClip`; new `receivedFilesBody` beside `sizeSkipMessage`)
- Create test `formacOS/Tests/AnyClipCoreTests/ReceivedTreeTests.swift`
- Test `formacOS/Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift` (rewrite `updateLocalFilesWritesUniquifiedAndDoesNotEcho` — Task 5 rewrote four other tests in this file, so find it by name; add 4 tests)
- Test `formacOS/Tests/AnyClipDaemonTests/InteropTests.swift` (add `folderOnlyClipIsNotSentToTheMinorZeroFakePeer`)

**Interfaces:**
- Consumes: `isValidWirePath(_:name:)`, `sanitizeWirePath(_:)`, `sanitizeFilename(_:)`, `uniquifyNames(_:)`, `ClipPayload.files([(name: String, data: Data, relPath: String?)])` (Task 4); `ClipboardWatcher.fingerprints(for:)`, `FolderExpander.walk(_:)` (Task 5); `ClipboardWatcher.receivedDir` (private stored property), `ClipboardWatcher.lastFileFingerprints`, `ClipboardWatcher.lastChangeCount`, `ClipboardWatcher.grabFileURLs(_:)`, `AnyLog.shared`, `sha256Hex(_:)`, and in `InteropTests.swift` the file-private helpers `startFakePeer(port:token:)`, `recvClipCount(_:)`, `fileContains(_:_:)`.
- Produces:
  - `public func uniquifyName(_ name: String, used: inout Set<String>) -> String` (AnyClipCore)
  - `public struct TreePlacement: Equatable, Sendable { let relativePath: String; let top: String; let inTree: Bool }`
  - `public enum ReceivedTree { static func plan(_ entries: [(name: String, data: Data, relPath: String?)], exists: (String) -> Bool) -> [TreePlacement] }`
  - `public struct PlacedFiles: Sendable { var files: [(name: String, data: Data)]; var topLevelItems: [String]; var folderTops: [String] }`
  - `ClipboardWatcher.updateLocalFiles(_:) -> PlacedFiles`
  - `public func receivedFilesBody(_ placed: PlacedFiles) -> String` (AnyClipDaemon)

Steps:

- [ ] Step 1: Write the failing planner tests — create `formacOS/Tests/AnyClipCoreTests/ReceivedTreeTests.swift`:
  ```swift
  import Testing
  import Foundation
  @testable import AnyClipCore

  private func entry(_ name: String, _ rel: String?) -> (name: String, data: Data, relPath: String?) {
      (name: name, data: Data(name.utf8), relPath: rel)
  }

  @Test func planRebuildsATreeAndKeepsLooseFilesFlat() {
      let got = ReceivedTree.plan([
          entry("a.txt", "docs/a.txt"),
          entry("b.txt", "docs/sub/b.txt"),
          entry("loose.txt", nil),
      ], exists: { _ in false })
      #expect(got.map(\.relativePath) == ["docs/a.txt", "docs/sub/b.txt", "loose.txt"])
      #expect(got.map(\.top) == ["docs", "docs", "loose.txt"])
      #expect(got.map(\.inTree) == [true, true, false])
  }

  @Test func planFallsBackToFlatOnEveryPathViolation() {
      let bad = [
          entry("evil.txt", "../../evil.txt"),      // traversal
          entry("abs.txt", "/etc/abs.txt"),         // absolute
          entry("win.txt", "C:\\tmp\\win.txt"),     // drive letter + backslashes
          entry("lie.txt", "docs/other.txt"),       // last segment != name
          entry("empty.txt", "docs//empty.txt"),    // empty segment
          entry("deep.txt", String(repeating: "d/", count: 33) + "deep.txt"),
      ]
      let got = ReceivedTree.plan(bad, exists: { _ in false })
      #expect(got.map(\.relativePath)
          == ["evil.txt", "abs.txt", "win.txt", "lie.txt", "empty.txt", "deep.txt"])
      #expect(got.allSatisfy { !$0.inTree })
  }

  @Test func planTreatsASingleSegmentPathAsALooseFile() {
      let got = ReceivedTree.plan([entry("a.txt", "a.txt")], exists: { _ in false })
      #expect(got == [TreePlacement(relativePath: "a.txt", top: "a.txt", inTree: false)])
  }

  @Test func planUniquifiesTheTopOnceForTheWholeClip() {
      let got = ReceivedTree.plan([
          entry("a.txt", "docs/a.txt"),
          entry("b.txt", "docs/sub/b.txt"),
          entry("c.txt", "notes/c.txt"),
      ], exists: { $0 == "docs" })
      // ONE clip lands in ONE new folder: every entry under "docs" moves together.
      #expect(got.map(\.relativePath) == ["docs-2/a.txt", "docs-2/sub/b.txt", "notes/c.txt"])
      #expect(got.map(\.top) == ["docs-2", "docs-2", "notes"])
  }

  @Test func planBumpsThroughSuccessiveTopCollisions() {
      let got = ReceivedTree.plan([entry("a.txt", "docs/a.txt")],
                                  exists: { ["docs", "docs-2", "docs-3"].contains($0) })
      #expect(got[0].relativePath == "docs-4/a.txt")
  }

  @Test func planKeepsLooseNamesOffTheReservedTops() {
      let got = ReceivedTree.plan([
          entry("a.txt", "docs/a.txt"),
          entry("docs", nil),          // a loose file literally named "docs"
          entry("docs", nil),
      ], exists: { _ in false })
      #expect(got.map(\.relativePath) == ["docs/a.txt", "docs (2)", "docs (3)"])
  }

  @Test func planSanitizesEverySegmentAndNormalizesToNFC() {
      let nfd = "결과".decomposedStringWithCanonicalMapping
      let nfc = "결과".precomposedStringWithCanonicalMapping
      let got = ReceivedTree.plan([
          entry(nfd + ".txt", nfd + "/" + nfd + ".txt"),
          entry("q?.txt", "docs/CON/q?.txt"),
      ], exists: { _ in false })
      #expect(Array(got[0].relativePath.utf8) == Array((nfc + "/" + nfc + ".txt").utf8))
      #expect(got[1].relativePath == "docs/_CON/q_.txt")
  }
  ```

- [ ] Step 2: Run them, expect FAIL — "cannot find 'ReceivedTree' in scope" / "cannot find 'TreePlacement' in scope".
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter plan
  ```

- [ ] Step 3: Minimal implementation. First, in `formacOS/Sources/AnyClipCore/TextHelpers.swift` split one name's step out of `uniquifyNames` (behaviour identical; every existing caller keeps compiling) by replacing L56–L89 — the existing doc comment starts at L56 and is superseded by the two below, so do not leave it stranded above `uniquifyName`:
  ```swift
  /// One name's uniquify step against the names already taken: the first
  /// occurrence keeps its name, later duplicates get " (2)", " (3)" … inserted
  /// before the LAST extension (a leading dot is not an extension:
  /// ".env" -> ".env (2)"). `used` is updated with whatever is returned, so the
  /// caller can seed it with names that are already spoken for (the rebuilt
  /// folder tops of the same clip). Keep in lockstep with the Python/C# receivers.
  public func uniquifyName(_ name: String, used: inout Set<String>) -> String {
      if !used.contains(name) {
          used.insert(name)
          return name
      }
      let stem: String
      let ext: String
      if let dot = name.lastIndex(of: "."), dot != name.startIndex {
          stem = String(name[..<dot])
          ext = String(name[dot...])
      } else {
          stem = name
          ext = ""
      }
      var n = 2
      var candidate = "\(stem) (\(n))\(ext)"
      while used.contains(candidate) {
          n += 1
          candidate = "\(stem) (\(n))\(ext)"
      }
      used.insert(candidate)
      return candidate
  }

  /// De-duplicate names WITHIN one received batch, after sanitization.
  public func uniquifyNames(_ names: [String]) -> [String] {
      var used = Set<String>()
      return names.map { uniquifyName($0, used: &used) }
  }
  ```
  Then create `formacOS/Sources/AnyClipCore/ReceivedTree.swift`:
  ```swift
  import Foundation

  /// Where ONE received entry lands under received/.
  public struct TreePlacement: Equatable, Sendable {
      /// POSIX-relative path under received/ ("<top>/<sub>/<name>", or just the
      /// file name for a loose entry). Every segment is already sanitized.
      public let relativePath: String
      /// First component of relativePath — the item this entry contributes to the
      /// clipboard (a rebuilt folder, or the loose file itself).
      public let top: String
      /// True when the entry landed inside a rebuilt folder.
      public let inTree: Bool
      public init(relativePath: String, top: String, inTree: Bool) {
          self.relativePath = relativePath
          self.top = top
          self.inTree = inTree
      }
  }

  /// Placement plan for one inbound kind:"files" batch. Pure — no IO: the caller
  /// supplies `exists` (is there already a received/<name>?). Keep in lockstep
  /// with anyclip.plan_received_tree and C# ReceivedTree.Plan.
  public enum ReceivedTree {
      /// Rules:
      ///  - No path, or a path that violates ANY wire rule -> FLAT placement under
      ///    the sanitized name. An entry is never dropped and never escapes
      ///    received/ (validation rejects "..", and sanitizeFilename would map a
      ///    surviving ".." to "received.bin" anyway).
      ///  - A valid single-segment path is a loose file, not a tree.
      ///  - Tops are reserved in first-appearance order and uniquified as
      ///    "<top>-2", "<top>-3" …; every entry sharing a top gets the SAME
      ///    replacement, so one clip lands in one new folder.
      ///  - Loose names then uniquify (" (2)") against those reserved tops.
      public static func plan(
          _ entries: [(name: String, data: Data, relPath: String?)],
          exists: (String) -> Bool
      ) -> [TreePlacement] {
          var used = Set<String>()               // reserved top-level names
          var topMap: [String: String] = [:]     // sanitized wire top -> placed top
          var segmentsByIndex: [Int: [String]] = [:]

          // Pass 1: reserve the tree tops, in first-appearance order.
          for (i, e) in entries.enumerated() {
              guard let raw = e.relPath, isValidWirePath(raw, name: e.name) else { continue }
              let segments = sanitizeWirePath(raw).split(separator: "/").map(String.init)
              guard segments.count >= 2 else { continue }
              segmentsByIndex[i] = segments
              let wireTop = segments[0]
              guard topMap[wireTop] == nil else { continue }
              var candidate = wireTop
              var n = 2
              while used.contains(candidate) || exists(candidate) {
                  candidate = "\(wireTop)-\(n)"
                  n += 1
              }
              used.insert(candidate)
              topMap[wireTop] = candidate
          }

          // Pass 2: emit placements in batch order.
          var out: [TreePlacement] = []
          for (i, e) in entries.enumerated() {
              if let segments = segmentsByIndex[i], let top = topMap[segments[0]] {
                  let rel = ([top] + segments.dropFirst()).joined(separator: "/")
                  out.append(TreePlacement(relativePath: rel, top: top, inTree: true))
                  continue
              }
              let flat = uniquifyName(sanitizeFilename(e.name), used: &used)
              out.append(TreePlacement(relativePath: flat, top: flat, inTree: false))
          }
          return out
      }
  }
  ```

- [ ] Step 4: Run them, expect PASS.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter plan
  ```

- [ ] Step 5: Write the failing placement tests. In `formacOS/Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift` replace `updateLocalFilesWritesUniquifiedAndDoesNotEcho` (find it by name — Task 5 rewrote four other tests in this file, so its line numbers have moved) with:
  ```swift
  @Test @MainActor func updateLocalFilesWritesUniquifiedAndDoesNotEcho() async throws {
      let pb = privatePasteboard()
      let received = tempDir()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: received, changes: changes, skipped: skipped)
      let placed = watcher.updateLocalFiles([
          (name: "dup.txt", data: Data("1".utf8), relPath: nil),
          (name: "dup.txt", data: Data("2".utf8), relPath: nil),
      ])
      #expect(placed.files.count == 2)
      #expect(placed.files.map(\.name) == ["dup.txt", "dup (2).txt"])
      #expect(placed.folderTops.isEmpty)
      #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("dup.txt").path))
      #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("dup (2).txt").path))
      // Placement baselines the fingerprint list, so the next poll does not echo.
      await watcher.pollOnceForTesting()
      #expect(changes.get().isEmpty)
  }

  @Test @MainActor func receivedTreeIsRebuiltAndOnlyTopItemsAreOnTheClipboard() async throws {
      let pb = privatePasteboard()
      let received = tempDir()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: received, changes: changes, skipped: skipped)
      let placed = watcher.updateLocalFiles([
          (name: "a.txt", data: Data("one".utf8), relPath: "docs/a.txt"),
          (name: "b.txt", data: Data("two".utf8), relPath: "docs/sub/b.txt"),
          (name: "loose.txt", data: Data("three".utf8), relPath: nil),
      ])
      #expect(placed.files.map(\.name) == ["docs/a.txt", "docs/sub/b.txt", "loose.txt"])
      #expect(placed.topLevelItems == ["docs", "loose.txt"])
      #expect(placed.folderTops == ["docs"])
      let deep = received.appendingPathComponent("docs/sub/b.txt")
      #expect(try Data(contentsOf: deep) == Data("two".utf8))
      // The clipboard carries the TOP-LEVEL items in batch order: the folder
      // once, then the loose file.
      #expect(ClipboardWatcher.grabFileURLs(pb).map(\.lastPathComponent) == ["docs", "loose.txt"])
      // A rebuilt tree must not echo back out on the next poll.
      await watcher.pollOnceForTesting()
      #expect(changes.get().isEmpty)
  }

  @Test @MainActor func receivedTopFolderCollisionUniquifiesTheWholeClip() async throws {
      let pb = privatePasteboard()
      let received = tempDir()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: received, changes: changes, skipped: skipped)
      try FileManager.default.createDirectory(
          at: received.appendingPathComponent("docs"), withIntermediateDirectories: true)
      let placed = watcher.updateLocalFiles([
          (name: "a.txt", data: Data("1".utf8), relPath: "docs/a.txt"),
          (name: "b.txt", data: Data("2".utf8), relPath: "docs/b.txt"),
      ])
      #expect(placed.topLevelItems == ["docs-2"])
      #expect(placed.files.map(\.name) == ["docs-2/a.txt", "docs-2/b.txt"])
      #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("docs-2/b.txt").path))
  }

  @Test @MainActor func traversalPathIsPlacedFlatInsideReceivedDir() async throws {
      let pb = privatePasteboard()
      let received = tempDir()
      let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
      let watcher = makeWatcher(pb, received: received, changes: changes, skipped: skipped)
      let placed = watcher.updateLocalFiles([
          (name: "evil.txt", data: Data("x".utf8), relPath: "../../evil.txt"),
      ])
      #expect(placed.files.map(\.name) == ["evil.txt"])
      #expect(placed.folderTops.isEmpty)
      #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("evil.txt").path))
      let escaped = received.deletingLastPathComponent().appendingPathComponent("evil.txt")
      #expect(!FileManager.default.fileExists(atPath: escaped.path))
  }

  @Test func receivedToastNamesTheFolderOnlyForAFolderOnlyClip() {
      var folderOnly = PlacedFiles()
      folderOnly.files = [(name: "docs/a.txt", data: Data()), (name: "docs/b.txt", data: Data())]
      folderOnly.topLevelItems = ["docs"]
      folderOnly.folderTops = ["docs"]
      #expect(receivedFilesBody(folderOnly) == "docs (2 files)")
      var mixed = folderOnly
      mixed.topLevelItems = ["docs", "loose.txt"]
      mixed.files.append((name: "loose.txt", data: Data()))
      #expect(receivedFilesBody(mixed) == "3 files")
  }
  ```

- [ ] Step 6: Run them, expect FAIL — `AnyClipDaemonTests` does not compile: "value of type '[(name: String, data: Data)]' has no member 'files'", "cannot find 'PlacedFiles' in scope", "cannot find 'receivedFilesBody' in scope".
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter received
  ```

- [ ] Step 7: Minimal implementation (address every site BY SYMBOL — Tasks 4 and 5 rewrote both of these files). In `formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift` replace `updateLocalFile(name:data:)` + `updateLocalFiles(_:)` (contiguous, `@discardableResult` attributes and doc comment included) with:
  ```swift
      @discardableResult
      public func updateLocalFile(name: String, data: Data) -> Bool {
          !updateLocalFiles([(name: name, data: data, relPath: nil)]).files.isEmpty
      }

      /// Rebuild one inbound batch under receivedDir — folder entries into their
      /// tree, everything else flat — then place the TOP-LEVEL items (each folder
      /// once, plus every loose file) on the clipboard in ONE writeObjects, in
      /// batch order. Returns what actually landed so the caller can baseline echo
      /// suppression and word the toast.
      @discardableResult
      public func updateLocalFiles(
          _ files: [(name: String, data: Data, relPath: String?)]
      ) -> PlacedFiles {
          let fm = FileManager.default
          do {
              try fm.createDirectory(at: receivedDir, withIntermediateDirectories: true)
          } catch {
              AnyLog.shared.warning("received dir create failed: \(error)")
              return PlacedFiles()
          }
          let root = receivedDir.standardizedFileURL
          let plan = ReceivedTree.plan(files) { top in
              fm.fileExists(atPath: root.appendingPathComponent(top).path)
          }
          var placed = PlacedFiles()
          var topURLs: [NSURL] = []
          var seenTops = Set<String>()
          for (i, item) in plan.enumerated() {
              // Fold the components by hand: appendingPathComponent would treat
              // "docs/sub/b.txt" as one component to escape.
              let target = item.relativePath.split(separator: "/")
                  .reduce(root) { $0.appendingPathComponent(String($1)) }
                  .standardizedFileURL
              // Traversal guard: the RESOLVED destination must stay under received/.
              guard target.path.hasPrefix(root.path + "/") else {
                  AnyLog.shared.warning("refusing to write outside received/: \(target.path)")
                  continue
              }
              if item.inTree {
                  do {
                      try fm.createDirectory(at: target.deletingLastPathComponent(),
                                             withIntermediateDirectories: true)
                  } catch {
                      AnyLog.shared.warning("received subdir create failed: \(error)")
                      continue
                  }
              }
              do {
                  try files[i].data.write(to: target)
              } catch {
                  AnyLog.shared.warning("file write to \(target.path) failed: \(error)")
                  continue
              }
              placed.files.append((name: item.relativePath, data: files[i].data))
              if seenTops.insert(item.top).inserted {
                  topURLs.append(root.appendingPathComponent(item.top) as NSURL)
                  placed.topLevelItems.append(item.top)
                  if item.inTree { placed.folderTops.append(item.top) }
              }
          }
          guard !topURLs.isEmpty else { return PlacedFiles() }
          // Baseline the fingerprints (tree files included) to the placed items
          // BEFORE the clipboard write so a racing poll cannot echo.
          lastFileFingerprints = Self.fingerprints(for: topURLs.map { $0 as URL })
          pasteboard.clearContents()
          let ok = pasteboard.writeObjects(topURLs)
          lastChangeCount = pasteboard.changeCount
          if !ok { AnyLog.shared.warning("clipboard write (files) failed") }
          return placed
      }
  ```
  Add `PlacedFiles` at file scope just above the `ClipboardWatcher` class (i.e. after the `FileFingerprint` struct):
  ```swift
  /// What one inbound batch put on disk and on the clipboard.
  public struct PlacedFiles: Sendable {
      /// Every file written, in batch order; `name` is the path RELATIVE to
      /// received/ ("<top>/<sub>/<leaf>" for tree entries).
      public var files: [(name: String, data: Data)] = []
      /// The items placed on the pasteboard, in batch order: each rebuilt folder
      /// once, plus every loose file.
      public var topLevelItems: [String] = []
      /// The subset of topLevelItems that are rebuilt folders.
      public var folderTops: [String] = []
      public init() {}
  }
  ```
  In `formacOS/Sources/AnyClipDaemon/Daemon.swift` add the toast helper at file scope immediately after `sizeSkipMessage(_:)` (Task 5 grew `downgradeForPeer`'s doc block above it, so do not trust a line number here):
  ```swift
  /// Toast body for an inbound kind:"files" batch. A folder-only clip names the
  /// folder ("<top> (N files)"); anything else keeps today's "N files".
  /// Keep in lockstep with anyclip.received_files_body.
  public func receivedFilesBody(_ placed: PlacedFiles) -> String {
      if placed.folderTops.count == 1, placed.topLevelItems.count == 1 {
          return "\(placed.folderTops[0]) (\(placed.files.count) files)"
      }
      return "\(placed.files.count) files"
  }
  ```
  and rewrite the `case .files(let fs):` branch of `applyClip` (the one that calls `watcherBox.get()?.updateLocalFiles(fs)`):
  ```swift
              case .files(let fs):
                  let placed = await MainActor.run {
                      watcherBox.get()?.updateLocalFiles(fs) ?? PlacedFiles()
                  }
                  // If exactly one file landed the watcher re-detects it as a
                  // single-file copy (kind "file"), so also suppress that hash.
                  if placed.files.count == 1 {
                      await coordinator.markReceived(
                          kind: "file", hash: sha256Hex(placed.files[0].data))
                  }
                  AnyLog.shared.info(
                      "<- received \(fs.count) files from \(peer) "
                      + "(\(placed.files.count) written, \(placed.folderTops.count) folder(s))")
                  notify("AnyClip ← \(peer)", receivedFilesBody(placed))
  ```

- [ ] Step 8: Run the whole suite, expect PASS.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS
  ```

- [ ] Step 9: Add the end-to-end interop guard against the UNMODIFIED `fake_peer.py` (still minor 0). Append to `formacOS/Tests/AnyClipDaemonTests/InteropTests.swift`:
  ```swift
  @Test func folderOnlyClipIsNotSentToTheMinorZeroFakePeer() async throws {
      let port: UInt16 = 28561
      let (proc, out) = try await startFakePeer(port: port, token: "folder-token")
      defer { if proc.isRunning { proc.terminate() } }

      let clips = Locked<[ClipPayload]>([])
      let manager = LinkManager(
          config: LinkManager.LinkConfig(
              token: "folder-token", port: 28562, name: "swift-folder",
              appVersion: "0.0.0-test"),
          nodeID: UUID().uuidString.lowercased())
      await manager.setHandlers(
          onClip: { payload, _ in clips.set(clips.get() + [payload]) }, emit: { _ in })

      func waitUntil(_ timeout: Double, _ cond: @escaping () async -> Bool) async -> Bool {
          let deadline = monotonicNow() + timeout
          while monotonicNow() < deadline {
              if await cond() { return true }
              try? await Task.sleep(nanoseconds: 50_000_000)
          }
          return await cond()
      }

      let outcome = await manager.tryConnect(
          to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!),
          label: "127.0.0.1:\(port)")
      #expect(outcome == .routed)
      #expect(await waitUntil(5) { await manager.activeLinkCount() == 1 })

      // Folder-only clip to a protocol-1.0 peer: nothing is sent, the link stays.
      let tree: [(name: String, data: Data, relPath: String?)] = [
          (name: "a.txt", data: Data("tree one".utf8), relPath: "docs/a.txt"),
          (name: "b.txt", data: Data("tree two".utf8), relPath: "docs/sub/b.txt"),
      ]
      let folderResult = await manager.broadcast(.files(tree))
      #expect(folderResult.delivered.isEmpty)
      #expect(folderResult.maxDropped == 0)          // nothing delivered -> no toast
      try await Task.sleep(nanoseconds: 1_000_000_000)
      #expect(recvClipCount(out) == 0)
      #expect(await manager.activeLinkCount() == 1)

      // Mixed clip: the old peer still gets the first LOOSE file, never a tree entry.
      let mixed: [(name: String, data: Data, relPath: String?)] = [
          (name: "a.txt", data: Data("tree one".utf8), relPath: "docs/a.txt"),
          (name: "loose.txt", data: Data("loose body".utf8), relPath: nil),
      ]
      let mixedResult = await manager.broadcast(.files(mixed))
      #expect(mixedResult.delivered.count == 1)
      #expect(mixedResult.maxDropped == 1)
      #expect(await waitUntil(5) {
          fileContains(out, "loose.txt") && !fileContains(out, "docs/a.txt")
      })
      #expect(recvClipCount(out) == 1)
      await manager.shutdown()
  }
  ```

- [ ] Step 10: Run it — expect PASS on the first run: it pins Task 5's minor-0 exclusion end to end against the real Python peer (fake_peer.py is untouched, still advertising minor 0). A FAIL here means the fan-out regressed and is sending folder entries to a 1.0 peer.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS --filter folderOnlyClipIsNotSentToTheMinorZeroFakePeer && git diff --stat formacOS/Scripts/fake_peer.py
  ```
  Expected: the test passes and `git diff --stat` prints nothing (fake_peer.py unmodified).

- [ ] Step 11: Run the full Swift suite one last time, expect PASS.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && swift test --package-path formacOS
  ```

- [ ] Step 12: Commit.
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && git add formacOS/Sources/AnyClipCore formacOS/Sources/AnyClipDaemon formacOS/Tests && git commit -m "$(cat <<'EOF'
  feat(receiver-swift): rebuild folder trees under received/ and place the tops

  ReceivedTree.plan (pure, Core) turns one kind:"files" batch into placements:
  a path that violates ANY wire rule falls back to flat placement for THAT
  entry -- never a dropped frame, never a write outside received/ -- and the
  top segment is uniquified once per clip ("<top>-2"), with the same
  replacement applied to every entry sharing it, so one clip lands in one
  folder. Loose names keep the " (2)" uniquify and now also dodge the reserved
  tops (uniquifyName extracted from uniquifyNames). The watcher creates the
  intermediate dirs, re-verifies the resolved destination under received/, and
  puts only the TOP-LEVEL items (each folder once + loose files) on
  NSPasteboard in batch order; the toast names the folder for a folder-only
  clip. Interop guard added against the unmodified minor-0 fake_peer.

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 7: C# wire — protocol minor 3, `FileEntry`, optional per-entry `path`

**Files:**
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/Wire.cs` (constants block lines 13–16; new predicate after `AcceptsFrameLength`, line 52)
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/TextHelpers.cs` (append after `SanitizeFilename`, line 57)
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/ClipPayload.cs` (line 27, `FilesClip`)
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/WireMessage.cs` (`WireFileEntry` lines 24–33 — the 3-line `///` doc comment **and** the record; `ClipFiles` lines 94–119)
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/PeerLink.cs` (`HandleClipAsync` `"files"` case, lines 119–141)
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/LinkManager.cs` (lines 398–403, tuple deconstruction of `fc.Files[0]`)
- Test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/WireMessageTests.cs` (line 61 minor assertion + new facts)
- Test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/LargeFrameTests.cs` (line 23 minor assertion)
- Test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/GoldenVectorTests.cs` (new fact + one line in `GoldenClipFilesDecodes`)

**Interfaces:**

Consumes (verified present today):
- `Wire.ProtocolMinor` (`const int` = 2), `Wire.ProtocolMajor`, `Wire.LegacyVersion`, `Wire.MaxPayload`, `Wire.LegacyMaxPayload`, `Wire.LinkAcceptsFrame(int, int)`, `Wire.AcceptsFrameLength(int)`, `Wire.SendTimeoutFor(int, double)`
- `TextHelpers.ToNfc(string) -> string`, `TextHelpers.SanitizeFilename(string) -> string`, `TextHelpers.UniquifyNames(IReadOnlyList<string>) -> IReadOnlyList<string>`
- `Hashing.Sha256Hex(byte[])`, `Hashing.Sha256Hex(string)`, `Hashing.AggregateFilesHash(IEnumerable<string>)`
- `readonly record struct EncodedFrame(byte[] Bytes, int BodyCount)`; `WireMessage.Encode()`, `WireMessage.EncodeFrame()`, `WireMessage.DecodeBody(byte[])`, `WireMessage.StrictBase64Decode(string)`, `WireMessage.FrameLength(byte[])`
- `sealed record WireFileEntry { Name, Content, Hash, Bytes }`; `FilesClip(IReadOnlyList<(string Name, byte[] Data)> Files)`
- Golden fixture `clip_files_path.bin` — regenerated and committed by **Task 4** (generator edited in **Task 1**), read from `formacOS/Tests/AnyClipCoreTests/Fixtures/` by the existing `GoldenVectorTests.FixturesDir()` helper.

  **Required vector composition (input contract on Task 1 — this task cannot edit `gen-golden-vectors.py`):** the `clip_files_path.bin` frame MUST contain, in one `kind:"files"` clip,
  1. at least ONE entry WITH `path`, whose path has **≥ 2 segments** (i.e. contains a `/`, so real subdirectory depth is pinned, not just a bare filename), and
  2. at least ONE entry WITHOUT `path` (a directly-selected loose file, proving the mixed shape and that loose entries stay byte-identical to pre-1.3).

  Step 2's `GoldenClipFilesWithPathDecodes` asserts exactly this shape. A folder-**only** vector, or one whose paths are single-segment, makes that fact unsatisfiable from this task — the only fix would be editing a Python file Task 7 is not allowed to touch. If Task 1 ships a different composition, Task 7 is blocked, not adjusted.

Produces (later tasks depend on these exact names):
- `Wire.ProtocolMinor == 3`; `Wire.MaxPathSegments == 32`; `Wire.MaxSanitizedPathLength == 240`
- `static bool Wire.IsValidRelPath(string? path, string name)`
- `static IReadOnlyList<string> TextHelpers.SanitizePathSegments(string path)`
- `sealed record FileEntry(string Name, byte[] Data, string? RelPath = null)` — the pinned C# entry shape
- `sealed record FilesClip(IReadOnlyList<FileEntry> Files)` plus a loose-file convenience ctor taking `IReadOnlyList<(string Name, byte[] Data)>`
- `WireMessage.ClipFiles(IReadOnlyList<FileEntry>, double)` plus a tuple overload
- `WireFileEntry.Path` → wire field `"path"`, serialized **last** (`name, content, hash, bytes, path`)

**Wire-order contract (must match Task 1's Python dict order):** `path` is appended **after** `bytes` in the entry object. Loose entries omit it (`DefaultIgnoreCondition = WhenWritingNull`), so every frame that exists today stays byte-identical.

**Cross-task decision — NFC is a SENDER rule, not a receiver REJECTION rule (deviation from the global constraint, pinned here for all three wire tasks):**

The global constraint lists NFC among the rules the "receiver MUST verify". This plan deliberately does **not** reject an NFD path. Rationale: v1.1.14 fixed Mac→Windows filename garbling by NFC-normalizing on the wire *and* on write; if a receiver rejected an NFD `path` outright it would fall back to **flat** placement, silently re-breaking folder sync for exactly the peers that fix exists for (and for any future peer that forgets to normalize). Instead:

- Senders MUST still emit NFC (`WireMessage.ClipFiles` normalizes both `name` and `path`).
- `IsValidRelPath` compares the last segment against `name` on **NFC forms**, so an NFD path whose segments are otherwise legal is ACCEPTED.
- The receiver NFC-normalizes every segment while sanitizing (`SanitizePathSegments` → `SanitizeFilename`), so the bytes that hit disk are composed either way. Nothing is lost by accepting.

This is **binding on all three wire tasks**: Task 1 (`anyclip.py`) and Task 4 (Swift `Wire`) MUST make the identical choice, or the same frame rebuilds a tree on one platform and lands flat on another. It is stated here as a contract, not only as a code comment. If Task 1 or Task 4 instead makes NFC a rejection rule, this task must be revisited rather than left divergent.

- [ ] **Step 1: Write the failing wire tests.** In `WireMessageTests.cs`, replace lines 59–61

```csharp
        // Cumulative feature level: >= 1 accepts kind:"files", >= 2 accepts
        // frames up to 64 MiB (see LargeFrameTests).
        Assert.Equal(2, root.GetProperty("protocol_minor").GetInt32());
```

  with

```csharp
        // Cumulative feature level: >= 1 accepts kind:"files", >= 2 accepts
        // frames up to 64 MiB (see LargeFrameTests), >= 3 rebuilds folder trees
        // from the optional per-entry "path".
        Assert.Equal(3, root.GetProperty("protocol_minor").GetInt32());
```

  and append these facts to `WireMessageTests` (before the closing brace):

```csharp
    [Fact]
    public void RelPathValidationMatchesTheWireRules()
    {
        // Accepted shapes.
        Assert.True(Wire.IsValidRelPath("docs/a.txt", "a.txt"));
        Assert.True(Wire.IsValidRelPath("docs/sub dir/a.txt", "a.txt"));
        Assert.True(Wire.IsValidRelPath("보고서/메모.txt", "메모.txt"));
        // A single segment is legal (it just places flat) — the rule list
        // constrains separators/segments, not a minimum depth.
        Assert.True(Wire.IsValidRelPath("a.txt", "a.txt"));
        // NFC is a SENDER rule: an NFD path from a macOS peer must still
        // rebuild its tree, or the v1.1.14 filename fix regresses to flat.
        var nfcPath = "보고서/메모.txt".Normalize(NormalizationForm.FormC);
        var nfdPath = nfcPath.Normalize(NormalizationForm.FormD);
        var nfcName = "메모.txt".Normalize(NormalizationForm.FormC);
        Assert.NotEqual(nfcPath, nfdPath);
        Assert.True(Wire.IsValidRelPath(nfdPath, nfcName));

        // Rejected shapes -> the receiver places THAT entry flat.
        Assert.False(Wire.IsValidRelPath(null, "a.txt"));
        Assert.False(Wire.IsValidRelPath("", "a.txt"));
        Assert.False(Wire.IsValidRelPath("/docs/a.txt", "a.txt"));   // absolute
        Assert.False(Wire.IsValidRelPath("C:/docs/a.txt", "a.txt")); // drive letter
        Assert.False(Wire.IsValidRelPath("docs\\a.txt", "a.txt"));   // backslash
        Assert.False(Wire.IsValidRelPath("docs/../a.txt", "a.txt")); // traversal
        Assert.False(Wire.IsValidRelPath("../a.txt", "a.txt"));
        Assert.False(Wire.IsValidRelPath("./docs/a.txt", "a.txt"));  // dot segment
        Assert.False(Wire.IsValidRelPath("docs//a.txt", "a.txt"));   // empty segment
        Assert.False(Wire.IsValidRelPath("docs/a.txt/", "a.txt"));   // trailing separator
        Assert.False(Wire.IsValidRelPath("docs/b.txt", "a.txt"));    // last segment != name

        // Segment-count boundary: 32 in, 33 out.
        var deep32 = string.Join("/",
            Enumerable.Repeat("d", Wire.MaxPathSegments - 1)) + "/a.txt";
        var deep33 = string.Join("/",
            Enumerable.Repeat("d", Wire.MaxPathSegments)) + "/a.txt";
        Assert.True(Wire.IsValidRelPath(deep32, "a.txt"));
        Assert.False(Wire.IsValidRelPath(deep33, "a.txt"));

        // Sanitized-length boundary: 240 in, 241 out ("/a.txt" is 6 chars).
        Assert.True(Wire.IsValidRelPath(new string('x', 234) + "/a.txt", "a.txt"));
        Assert.False(Wire.IsValidRelPath(new string('x', 235) + "/a.txt", "a.txt"));
    }

    [Fact]
    public void SanitizePathSegmentsCleansEverySegmentIndependently()
    {
        Assert.Equal(new[] { "docs", "q3", "a.txt" },
            TextHelpers.SanitizePathSegments("docs/q3/a.txt").ToArray());
        // Per-segment denylist + reserved-name guard + NFC, same rules as the
        // flat receive path.
        Assert.Equal(new[] { "a_b", "_CON", "x_y" },
            TextHelpers.SanitizePathSegments("a:b/CON/x|y").ToArray());
        var nfd = "결과보고서".Normalize(NormalizationForm.FormD);
        Assert.Equal(new[] { "결과보고서".Normalize(NormalizationForm.FormC), "a.txt" },
            TextHelpers.SanitizePathSegments(nfd + "/a.txt").ToArray());
    }

    [Fact]
    public void ClipFilesEmitsPathLastAndOmitsItForLooseFiles()
    {
        var frame = WireMessage.ClipFiles(new List<FileEntry>
        {
            new("a.txt", "one"u8.ToArray(), "docs/q3/a.txt"),
            new("loose.txt", "two"u8.ToArray()),
        }, 7.5).EncodeFrame();
        using var doc = JsonDocument.Parse(frame.AsSpan(4).ToArray());
        var arr = doc.RootElement.GetProperty("files");
        // Folder entry: "path" is the LAST field of the entry object.
        Assert.Equal(new[] { "name", "content", "hash", "bytes", "path" },
            arr[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("docs/q3/a.txt", arr[0].GetProperty("path").GetString());
        // Loose entry: byte-identical to a pre-1.3 entry — no "path" at all.
        Assert.Equal(new[] { "name", "content", "hash", "bytes" },
            arr[1].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.False(arr[1].TryGetProperty("path", out _));
    }

    [Fact]
    public void ClipFilesNormalizesThePathToNFCAndRoundTrips()
    {
        var nfd = "보고서/메모.txt".Normalize(NormalizationForm.FormD);
        var nfc = "보고서/메모.txt".Normalize(NormalizationForm.FormC);
        Assert.NotEqual(nfd, nfc);
        var frame = WireMessage.ClipFiles(new List<FileEntry>
        {
            new("메모.txt".Normalize(NormalizationForm.FormD), new byte[] { 1 }, nfd),
        }, 1.0).EncodeFrame();
        var msg = WireMessage.DecodeBody(frame.AsSpan(4).ToArray())!;
        Assert.Equal(nfc, msg.Files![0].Path);
        Assert.Equal("메모.txt".Normalize(NormalizationForm.FormC), msg.Files[0].Name);
    }

    [Fact]
    public void FilesClipCarriesRelPathAndTupleCtorLeavesItNull()
    {
        var withPath = new FilesClip(new List<FileEntry>
        {
            new("a.txt", "one"u8.ToArray(), "docs/a.txt"),
        });
        Assert.Equal("docs/a.txt", withPath.Files[0].RelPath);
        // Loose-file convenience ctor: every entry gets RelPath null, so the
        // existing (name, bytes) call sites keep working unchanged.
        var loose = new FilesClip(new List<(string, byte[])> { ("a.txt", "one"u8.ToArray()) });
        Assert.Null(loose.Files[0].RelPath);
        // Aggregate hash is over CONTENT only — tree vs flat delivery of the
        // same bytes must suppress identically.
        Assert.Equal(loose.PayloadHash, withPath.PayloadHash);
    }
```

  Change `LargeFrameTests.cs` line 23 from `Assert.Equal(2, Wire.ProtocolMinor);` to `Assert.Equal(3, Wire.ProtocolMinor);`.

- [ ] **Step 2: Write the failing golden-vector test.** In `GoldenVectorTests.cs`, add `Assert.Null(entry.Path);` immediately after `Assert.Equal(entry.Bytes, data.Length);` inside `GoldenClipFilesDecodes` (line 96) — the pre-1.3 vector must stay path-free — and append this fact:

```csharp
    /// The 1.3 vector: ONE kind:"files" frame carrying both shapes — entries
    /// derived from a copied folder (with "path") and a file the user selected
    /// directly (no "path"). Asserted structurally rather than against pinned
    /// literals so the Python generator owns the sample data. Byte-exact
    /// re-encoding is NOT asserted: Python's json.dumps writes ", "/": "
    /// separators, System.Text.Json writes them compact — the frames are
    /// JSON-equivalent, not byte-equal.
    [Fact]
    public void GoldenClipFilesWithPathDecodes()
    {
        var m = DecodeGolden("clip_files_path.bin");
        Assert.Equal("files", m.Kind);
        Assert.NotNull(m.Files);
        var entries = m.Files!;
        Assert.Contains(entries, e => e.Path is not null);   // folder entries
        Assert.Contains(entries, e => e.Path is null);       // a loose file
        var hashes = new List<string>();
        int total = 0;
        foreach (var e in entries)
        {
            var data = WireMessage.StrictBase64Decode(e.Content!)!;
            Assert.Equal(Hashing.Sha256Hex(data), e.Hash);
            Assert.Equal(data.Length, e.Bytes);
            hashes.Add(e.Hash!);
            total += data.Length;
            if (e.Path is null) continue;
            Assert.True(Wire.IsValidRelPath(e.Path, e.Name!),
                $"golden path rejected by the validator: {e.Path}");
            Assert.Contains("/", e.Path);               // real subdirectory depth
            Assert.EndsWith("/" + e.Name, e.Path);      // last segment == name
        }
        Assert.Equal(Hashing.AggregateFilesHash(hashes), m.Hash);
        Assert.Equal(total, m.Bytes);
    }
```

- [ ] **Step 3: Run the new tests, expected FAIL.** Compile errors are the red here: `Wire.IsValidRelPath`, `Wire.MaxPathSegments`, `Wire.MaxSanitizedPathLength`, `TextHelpers.SanitizePathSegments`, `FileEntry` and `WireFileEntry.Path` do not exist yet (CS0117/CS0246/CS1061); `LargeFrameTests.FrameCapsAndProtocolMinor` and `HelloCarriesAllProtocolFieldsInSnakeCase` fail on `Expected: 3 / Actual: 2`.

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet test /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests
```

- [ ] **Step 4: `Wire.cs` — minor 3 + the path rules.** Replace lines 13–16 with:

```csharp
    public const int ProtocolMajor = 1;
    /// Cumulative feature level: minor >= 1 accepts kind:"files", >= 2 accepts
    /// frames up to MaxPayload (64 MiB) instead of LegacyMaxPayload, >= 3
    /// rebuilds folder trees from the optional per-entry "path". Minor 3 is a
    /// capability MARKER only — it gates nothing on the send path.
    public const int ProtocolMinor = 3;
```

  and append inside the class, after `AcceptsFrameLength` (line 52):

```csharp
    /// Folder-tree limits for the optional per-entry "path" (protocol 1.3).
    public const int MaxPathSegments = 32;
    public const int MaxSanitizedPathLength = 240;

    /// True when `path` is a legal wire "path" for an entry named `name`:
    /// POSIX '/' separators, relative, no drive letter, no '.'/'..'/empty
    /// segment, no backslash, last segment == name, <= MaxPathSegments
    /// segments, sanitized total <= MaxSanitizedPathLength characters.
    ///
    /// Senders MUST emit only paths that pass. Receivers MUST verify and fall
    /// back to FLAT placement for the failing ENTRY — never drop the frame.
    ///
    /// NFC is a sender rule, not a rejection rule: an NFD path from a macOS
    /// peer still rebuilds its tree, because the receiver NFC-normalizes every
    /// segment while sanitizing. Rejecting it would regress the v1.1.14
    /// filename fix to flat placement, so the name comparison runs on NFC
    /// forms instead. Keep in lockstep with anyclip.py and Swift Wire.
    public static bool IsValidRelPath(string? path, string name)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Contains('\\')) return false;                  // POSIX separators only
        if (path[0] == '/') return false;                       // must be relative
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return false;                                       // "C:/..." drive letter
        var segments = path.Split('/');
        if (segments.Length > MaxPathSegments) return false;
        foreach (var s in segments)
            if (s.Length == 0 || s == "." || s == "..") return false;
        if (!string.Equals(TextHelpers.ToNfc(segments[^1]), TextHelpers.ToNfc(name),
                StringComparison.Ordinal))
            return false;
        int sanitized = -1;                                     // n segments -> n-1 separators
        foreach (var s in TextHelpers.SanitizePathSegments(path))
            sanitized += s.Length + 1;
        return sanitized <= MaxSanitizedPathLength;
    }
```

- [ ] **Step 5: `TextHelpers.cs` — per-segment sanitizer.** Insert after `SanitizeFilename` (after line 57, before `UniquifyNames`):

```csharp
    /// Split a wire "path" on '/' and run every segment through
    /// SanitizeFilename (NFC + denylist + trailing dot/space trim + Windows
    /// reserved-name guard). The caller joins the result under received/ with
    /// the platform separator. Empty/'.'/'..' segments never reach here —
    /// Wire.IsValidRelPath rejects them first — and SanitizeFilename maps them
    /// to "received.bin" anyway, so no segment can ever become a traversal.
    /// Keep in lockstep with Swift sanitizePathSegments and anyclip.py.
    public static IReadOnlyList<string> SanitizePathSegments(string path)
    {
        var parts = path.Split('/');
        var result = new List<string>(parts.Length);
        foreach (var p in parts) result.Add(SanitizeFilename(p));
        return result;
    }
```

- [ ] **Step 6: `ClipPayload.cs` — `FileEntry` + the new `FilesClip`.** Replace lines 27–32 with:

```csharp
/// One file in a kind:"files" clip. RelPath is the wire "path": the file's
/// POSIX-separated path relative to the copied selection, top folder name
/// INCLUDED (e.g. "docs/q3/report.txt"), or null for a file the user selected
/// directly — those stay byte-identical to a pre-1.3 entry on the wire.
/// Pinned cross-implementation shape: Python (name, data, relpath|None),
/// Swift (name: String, data: Data, relPath: String?).
public sealed record FileEntry(string Name, byte[] Data, string? RelPath = null);

public sealed record FilesClip(IReadOnlyList<FileEntry> Files) : ClipPayload
{
    /// Loose-file convenience: every entry gets RelPath null. Keeps the
    /// (name, bytes) call sites that never deal with folders unchanged.
    public FilesClip(IReadOnlyList<(string Name, byte[] Data)> files)
        : this(files.Select(f => new FileEntry(f.Name, f.Data)).ToList()) { }

    public override string Kind => "files";
    /// Content only — tree and flat delivery of the same bytes must produce
    /// the same echo-suppression key.
    public override string PayloadHash =>
        Hashing.AggregateFilesHash(Files.Select(f => Hashing.Sha256Hex(f.Data)));
}
```

- [ ] **Step 7: `WireMessage.cs` — the `path` field and the new `ClipFiles`.** Replace lines **24–33** — the existing 3-line `///` doc comment (`/// One entry of a kind:"files" clip. Field order name, content, hash, bytes` … `/// (then gets rejected in PeerLink) rather than failing the whole frame parse.`) **together with** the `WireFileEntry` record it documents (lines 27–33). Replacing only 27–33 would leave the stale comment stacked above the new one, and the two would merge into a single contradictory doc comment. The replacement block, comment included:

```csharp
/// One entry of a kind:"files" clip. Field order name, content, hash, bytes,
/// path is golden-vector material — "path" is appended LAST and omitted when
/// null, so every pre-1.3 frame stays byte-identical. Nullable so a malformed
/// inbound entry decodes (then gets rejected in PeerLink, or falls back to
/// flat placement for a bad path) rather than failing the whole frame parse.
public sealed record WireFileEntry
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("hash")] public string? Hash { get; init; }
    [JsonPropertyName("bytes")] public int? Bytes { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
}
```

  and replace `ClipFiles` (lines 94–119) with:

```csharp
    public static WireMessage ClipFiles(IReadOnlyList<FileEntry> files, double ts)
    {
        var entries = new List<WireFileEntry>(files.Count);
        var hashes = new List<string>(files.Count);
        int total = 0;
        foreach (var f in files)
        {
            var h = Hashing.Sha256Hex(f.Data);
            hashes.Add(h);
            // NFC per name AND per path, same rule as ClipFile: the peer renders
            // and WRITES both, so both must leave composed. Keep in lockstep with
            // Swift WireMessage.clipFiles and anyclip.send_clip.
            entries.Add(new WireFileEntry
            {
                Name = TextHelpers.ToNfc(f.Name),
                Content = Convert.ToBase64String(f.Data),
                Hash = h, Bytes = f.Data.Length,
                Path = f.RelPath is null ? null : TextHelpers.ToNfc(f.RelPath),
            });
            total += f.Data.Length;
        }
        return new WireMessage
        {
            Type = "clip", Kind = "files", Files = entries,
            Hash = Hashing.AggregateFilesHash(hashes), Ts = ts, Bytes = total,
        };
    }

    /// Loose-file overload: no entry carries a path.
    public static WireMessage ClipFiles(
        IReadOnlyList<(string Name, byte[] Data)> files, double ts) =>
        ClipFiles(files.Select(f => new FileEntry(f.Name, f.Data)).ToList(), ts);
```

- [ ] **Step 8: `PeerLink.cs` + `LinkManager.cs` — carry `path` through, fix the deconstruction.** In `PeerLink.HandleClipAsync`, replace line 125 and lines 136–137 so the decode builds `FileEntry`:

```csharp
                var decoded = new List<FileEntry>(msg.Files.Count);
```

```csharp
                    var fname = string.IsNullOrEmpty(entry.Name) ? "received.bin" : entry.Name!;
                    // The wire "path" rides through UNVALIDATED: validation belongs
                    // to the placement step (ReceivedTree), which falls back to flat
                    // for a bad path. Rejecting here would drop the whole frame.
                    decoded.Add(new FileEntry(fname, fbytes, entry.Path));
                    // hash NOT trusted from wire; recomputed downstream
```

  In `LinkManager.BroadcastAsync`, replace lines 400–402 (Task 8 rewrites this block again):

```csharp
                dropped = fc.Files.Count - 1;
                var first = fc.Files[0];
                toSend = new FileClip(first.Name, first.Data);
```

- [ ] **Step 9: Run the suite, expected PASS.** All of `AnyClipCore.Tests` green, including the two golden facts and the round-trip/NFC facts.

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet test /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests
```

- [ ] **Step 10: Commit.**

```bash
cd /Users/seojeonghwa/project/AnyClip && git add forwindows/src/AnyClipCore forwindows/tests/AnyClipCore.Tests && git commit -m "$(cat <<'EOF'
feat(wire-c#): protocol minor 3 + optional per-entry folder path

Bump Wire.ProtocolMinor to 3 (cumulative: >=1 files, >=2 64 MiB frames,
>=3 rebuilds folder trees; marker only, gates nothing on send). Add the
pinned FileEntry(Name, Data, RelPath) shape, the optional "path" wire
field serialized last so pre-1.3 frames stay byte-identical, the full
sender/receiver validation rules in Wire.IsValidRelPath, and the
per-segment sanitizer. GoldenVectorTests asserts the new
clip_files_path.bin vector.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: C# sender — expand copied folders into path-carrying `files` clips

**Files:**
- Create `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/FolderExpander.cs`
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/LinkManager.cs` (`BroadcastAsync`, lines 394–431 after Task 7)
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipApp/ClipboardWatcher.cs` (`MaxFilesPerClip` line 107; `CheckFileClipboardAsync` lines 268–337)
- Create test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/FolderExpanderTests.cs`
- Modify test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/LinkManagerTests.cs` (append facts)
- Modify test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/InteropTests.cs` (append one fact — the spec's required interop coverage against the real `fake_peer.py`)
- Modify test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipApp.Tests/ClipboardLogicTests.cs` (**Windows-CI-only to RUN, compile-checked here**: rewrite lines 87–106, 243–258, 260–274; add two facts)

**Interfaces:**

Consumes: `FileEntry(string Name, byte[] Data, string? RelPath = null)`, `FilesClip(IReadOnlyList<FileEntry>)`, `FileClip(string Name, byte[] Data)` (Task 7) · `Wire.IsValidRelPath(string?, string)` (Task 7) · `TextHelpers.ToNfc(string)` · `RotatingLog.Shared.{Info,Warning,Debug,Error}(string)` · `PeerLink.PeerProtocolMinor` (`int`), `PeerLink.PeerName` (`string`), `PeerLink.SendEncodedAsync(EncodedFrame)` · `LinkManager.BroadcastResult(IReadOnlyList<string> Delivered, int OldPeerDrops, IReadOnlyList<string> SizeSkipped)` · `Wire.LinkAcceptsFrame(int, int)` · `ClipboardWatcher.FileBudget` (`static readonly int` = 49_466_572), `ClipboardWatcher.MaxFilesPerClip`, `ClipboardWatcher.SafeSkipAsync(string)`, `ClipboardWatcher.FingerprintList(IReadOnlyList<string>)`, `IWin32Clipboard.GetFilePaths()`

Produces:
- `static class FolderExpander` with `FolderExpander.Plan(IReadOnlyList<FileEntry> Entries, IReadOnlyList<string> TooLargeFolders, IReadOnlyList<string> EmptyFolders, int SkippedFiles)`, `Task<Plan> ExpandAsync(IReadOnlyList<string> selection, long budget, int maxFiles)`, `IReadOnlyList<(string FullPath, string RelPath, long Size)> Walk(string root)`, `string? WirePathFor(string relPath, string name)`, `string FolderDisplayName(string path)`, `int CompareUtf8(string, string)`, `IReadOnlyList<string> JunkNames`
- `static string LinkManager.FlattenNoticeMessage(string peerName)` — the pinned flatten log line
- `ClipboardWatcher.MaxFilesPerClip == 500`

**Cross-task decision — an unrepresentable path DROPS TO LOOSE, it never drops the file (binding on Tasks 2 and 5 too):**

The global constraint says the wire path rules are what the "sender MUST emit", so the sender may not ship a path its own validator would reject. A real filesystem can produce one: a name containing `\` (legal on macOS/Linux, and reachable on Windows through a mounted share), a tree deeper than 32 segments, or a relative path whose sanitized form exceeds 240 characters.

When `Wire.IsValidRelPath(rel, name)` fails for a walked file, the file is still sent — with `RelPath = null`, i.e. as a **loose entry** — and the drop is logged once. Chosen over skipping the file because the on-disk result is identical to what the receiver would do anyway (an invalid path falls back to flat placement for that entry), while skipping would silently lose data the user asked to copy. It does not violate per-folder all-or-nothing: every file still ships, one just lands flat.

`FolderExpander.WirePathFor` is the single choke point for this rule, so the >32-segment and >240-character boundaries are unit-testable without building a deep or long tree on disk (which would risk the Windows `MAX_PATH` limit on the `windows-latest` runner that executes this suite). **Task 2 (`anyclip.py` watcher) and Task 5 (Swift `FolderExpander`) MUST make the identical choice** — otherwise the same tree ships a rebuildable path from one sender and a flattened file from another.

- [ ] **Step 1: Write the failing expander tests.** Create `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/FolderExpanderTests.cs`:

```csharp
using System.Text;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

/// Send-side folder expansion (protocol 1.3). Runs on the platform-neutral
/// suite because FolderExpander only touches System.IO — the WinForms watcher
/// just hands it the clipboard's file-drop list.
public class FolderExpanderTests
{
    // The watcher's budget constant lives in the WinForms assembly, which this
    // platform-neutral suite cannot reference; the formula is pinned there
    // (ClipboardWatcher.FileBudget == (int)((Wire.MaxPayload - 256*1024) * 0.74)).
    private const long ClipboardWatcher_FileBudget = 49_466_572;

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "anyclip-expand-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Write(string dir, string relative, string body)
    {
        var full = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        return full;
    }

    private static string MakeTree(string name)
    {
        var root = Path.Combine(TempDir(), name);
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task FolderExpandsIntoPathCarryingEntriesInByteSortedOrder()
    {
        var root = MakeTree("docs");
        Write(root, "b.txt", "bbb");
        Write(root, "a.txt", "aaa");
        Write(root, "sub/z.txt", "zzz");
        Write(root, "sub/deeper/y.txt", "yyy");

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        Assert.Empty(plan.TooLargeFolders);
        Assert.Empty(plan.EmptyFolders);
        Assert.Equal(0, plan.SkippedFiles);
        // Deterministic byte-wise sort on the relative path, top name included.
        Assert.Equal(
            new[] { "docs/a.txt", "docs/b.txt", "docs/sub/deeper/y.txt", "docs/sub/z.txt" },
            plan.Entries.Select(e => e.RelPath).ToArray());
        Assert.Equal(new[] { "a.txt", "b.txt", "y.txt", "z.txt" },
            plan.Entries.Select(e => e.Name).ToArray());
        Assert.Equal("aaa", Encoding.UTF8.GetString(plan.Entries[0].Data));
    }

    [Fact]
    public async Task JunkFilesAndEmptyDirsAreExcluded()
    {
        var root = MakeTree("mixed");
        Write(root, "keep.txt", "k");
        Write(root, ".DS_Store", "junk");
        Write(root, "Thumbs.db", "junk");
        Write(root, "sub/desktop.ini", "junk");
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        // Empty dirs are not representable and are dropped; junk never ships.
        Assert.Equal(new[] { "mixed/keep.txt" }, plan.Entries.Select(e => e.RelPath).ToArray());
    }

    [Fact]
    public async Task SymlinksAreExcludedAndNeverFollowed()
    {
        var root = MakeTree("linked");
        Write(root, "keep.txt", "k");
        var outside = Path.Combine(TempDir(), "outside.txt");
        File.WriteAllText(outside, "secret");
        // Split out of the junk fact and privilege-gated ON PURPOSE. This suite
        // is the platform-neutral one, and release.yml runs it on
        // windows-latest, where creating a symlink needs
        // SeCreateSymbolicLinkPrivilege / Developer Mode and otherwise throws
        // UnauthorizedAccessException or IOException. Turning the release job
        // red over a missing privilege would be a failure unrelated to folder
        // sync, so the fact no-ops when the link cannot be created. Detection
        // itself is platform-neutral: File.GetAttributes reports ReparsePoint
        // for live AND dangling links without following them.
        try { File.CreateSymbolicLink(Path.Combine(root, "link.txt"), outside); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
            or PlatformNotSupportedException)
        {
            return;   // no symlink privilege on this runner; nothing to assert
        }

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        // The link never ships, and its target is never read — so a symlink can
        // neither leak files from outside the selection nor create a cycle.
        Assert.Equal(new[] { "linked/keep.txt" }, plan.Entries.Select(e => e.RelPath).ToArray());
        Assert.DoesNotContain(plan.Entries, e => e.Name == "link.txt");
    }

    [Fact]
    public void WirePathForRejectsWhatTheReceiverWouldRejectAndDropsToLoose()
    {
        // The sender MUST NOT emit a path its own validator rejects. A real
        // filesystem can produce one, so WirePathFor is the single choke point:
        // null means "ship this file as a LOOSE entry", never "drop the file".
        Assert.Equal("docs/a.txt", FolderExpander.WirePathFor("docs/a.txt", "a.txt"));

        // Deeper than 32 segments (no disk tree needed — and none is BUILT here
        // on purpose, since a 33-deep or 240-char path can blow past MAX_PATH on
        // the windows-latest runner that executes this suite).
        var deep33 = string.Join("/", Enumerable.Repeat("d", Wire.MaxPathSegments)) + "/a.txt";
        Assert.Null(FolderExpander.WirePathFor(deep33, "a.txt"));

        // Sanitized total over 240 characters.
        var long241 = new string('x', 235) + "/a.txt";
        Assert.Null(FolderExpander.WirePathFor(long241, "a.txt"));

        // A backslash in a file NAME is legal on macOS/Linux and reachable on
        // Windows via a mounted share; it is not legal on the wire.
        Assert.Null(FolderExpander.WirePathFor("docs/back\\slash.txt", "back\\slash.txt"));
    }

    [Fact]
    public async Task AnUnrepresentablePathShipsTheFileAsALooseEntry()
    {
        var root = MakeTree("deep");
        // 32 nested single-character directories puts the file at 34 segments
        // (deep/ + 32 dirs + the file) while keeping the ABSOLUTE path short
        // enough for any platform.
        var nested = string.Join("/", Enumerable.Repeat("d", 32)) + "/leaf.txt";
        Write(root, nested, "L");
        Write(root, "shallow.txt", "s");

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        // Both files still ship — the over-deep one just loses its path rather
        // than being dropped, or shipping a path the receiver would reject.
        Assert.Equal(2, plan.Entries.Count);
        var leaf = Assert.Single(plan.Entries, e => e.Name == "leaf.txt");
        Assert.Null(leaf.RelPath);
        Assert.Equal("deep/shallow.txt",
            Assert.Single(plan.Entries, e => e.Name == "shallow.txt").RelPath);
        // Not counted as a skip: nothing was skipped.
        Assert.Equal(0, plan.SkippedFiles);
        Assert.Empty(plan.TooLargeFolders);
    }

    [Fact]
    public async Task OversizeFolderIsAllOrNothingAndNamesTheFolder()
    {
        var root = MakeTree("heavy");
        Write(root, "small.txt", "s");
        var big = Path.Combine(root, "big.bin");
        using (var fs = File.Create(big)) fs.SetLength(ClipboardWatcher_FileBudget + 1);

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        // No partial trees: the whole folder goes, or none of it does.
        Assert.Empty(plan.Entries);
        Assert.Equal(new[] { "heavy" }, plan.TooLargeFolders.ToArray());
        Assert.Equal(0, plan.SkippedFiles);
    }

    [Fact]
    public async Task FolderIsAllOrNothingAgainstTheREMAININGCountToo()
    {
        var root = MakeTree("three");
        Write(root, "a.txt", "a");
        Write(root, "b.txt", "b");
        Write(root, "c.txt", "c");
        var loose = Path.Combine(TempDir(), "loose.txt");
        File.WriteAllText(loose, "l");

        // Cap 3, and the loose file (processed FIRST, in selection order)
        // consumes one slot -> the 3-file folder no longer fits at all.
        var plan = await FolderExpander.ExpandAsync(
            new[] { loose, root }, ClipboardWatcher_FileBudget, 3);
        Assert.Equal(new[] { "loose.txt" }, plan.Entries.Select(e => e.Name).ToArray());
        Assert.Equal(new[] { "three" }, plan.TooLargeFolders.ToArray());

        // Same selection with room for all four: the folder is taken whole.
        var roomy = await FolderExpander.ExpandAsync(
            new[] { loose, root }, ClipboardWatcher_FileBudget, 4);
        Assert.Equal(4, roomy.Entries.Count);
        Assert.Empty(roomy.TooLargeFolders);
    }

    [Fact]
    public async Task EmptyFolderIsReportedAndSendsNothing()
    {
        var root = MakeTree("hollow");
        Write(root, "nested/.DS_Store", "junk");   // nothing left after exclusions

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        Assert.Empty(plan.Entries);
        Assert.Empty(plan.TooLargeFolders);
        Assert.Equal(new[] { "hollow" }, plan.EmptyFolders.ToArray());
    }

    [Fact]
    public async Task LooseFilesCarryNoPathAndKeepTodaysGreedyBehaviour()
    {
        var d = TempDir();
        var s1 = Write(d, "s1.txt", "a");
        var big = Path.Combine(d, "big.bin");
        using (var fs = File.Create(big)) fs.SetLength(ClipboardWatcher_FileBudget + 1);
        var s2 = Write(d, "s2.txt", "b");

        var plan = await FolderExpander.ExpandAsync(
            new[] { s1, big, s2 }, ClipboardWatcher_FileBudget, 500);

        // Greedy, per file, in selection order — unchanged from 1.3.0.
        Assert.Equal(new[] { "s1.txt", "s2.txt" }, plan.Entries.Select(e => e.Name).ToArray());
        Assert.All(plan.Entries, e => Assert.Null(e.RelPath));
        Assert.Equal(1, plan.SkippedFiles);
    }

    [Fact]
    public async Task SelectionOrderIsHonouredAcrossFoldersAndFiles()
    {
        var one = MakeTree("one");
        Write(one, "x.txt", "x");
        var two = MakeTree("two");
        Write(two, "y.txt", "y");
        var loose = Path.Combine(TempDir(), "mid.txt");
        File.WriteAllText(loose, "m");

        var plan = await FolderExpander.ExpandAsync(
            new[] { one, loose, two }, ClipboardWatcher_FileBudget, 500);

        // Each folder keeps its OWN top name; loose files stay path-free.
        Assert.Equal(new string?[] { "one/x.txt", null, "two/y.txt" },
            plan.Entries.Select(e => e.RelPath).ToArray());
    }

    [Fact]
    public void Utf8ByteOrderIsUsedNotUtf16CodeUnitOrder()
    {
        // U+1F600 encodes to F0 9F 98 80 and U+FFFD to EF BF BD, so UTF-8 byte
        // order (== code-point order, what Python's sorted() gives) puts the
        // emoji AFTER. UTF-16 code-unit order puts it BEFORE, because the lead
        // surrogate is 0xD83D < 0xFFFD. A folder with an emoji-named file would
        // otherwise ship in a different order from the Python/Swift senders.
        Assert.True(FolderExpander.CompareUtf8("\U0001F600", "\uFFFD") > 0);
        Assert.True(string.CompareOrdinal("\U0001F600", "\uFFFD") < 0);
        Assert.True(FolderExpander.CompareUtf8("a", "b") < 0);
        Assert.Equal(0, FolderExpander.CompareUtf8("same", "same"));
    }

    [Fact]
    public void FolderDisplayNameHandlesTrailingSeparators()
    {
        Assert.Equal("docs", FolderExpander.FolderDisplayName("/tmp/docs"));
        Assert.Equal("docs", FolderExpander.FolderDisplayName("/tmp/docs/"));
        Assert.Equal("/", FolderExpander.FolderDisplayName("/"));   // root keeps the raw path
    }
}
```

- [ ] **Step 2: Write the failing broadcast tests.** Append to `LinkManagerTests` in `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/LinkManagerTests.cs`:

```csharp
    [Fact]
    public void FlattenNoticeMessageIsThePinnedWording()
    {
        // Pinned across all three implementations; logged once per clip per
        // affected link when the peer takes the frame but cannot rebuild it.
        Assert.Equal("peer old-pc will flatten folders (protocol < 1.3)",
            LinkManager.FlattenNoticeMessage("old-pc"));
    }

    [Fact]
    public async Task FolderOnlyClipSendsNothingToAMinorZeroPeer()
    {
        var m = MakeManager("tok", 28722, "folder", new(), new());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var oldPeer = await RawHandshake(28722, "tok", "old-node", "old", 0, cts.Token);
        using var modern = await RawHandshake(28722, "tok", "new-node", "new", 3, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var res = await m.BroadcastAsync(new FilesClip(new List<FileEntry>
        {
            new("a.txt", "one"u8.ToArray(), "docs/a.txt"),
            new("b.txt", "two"u8.ToArray(), "docs/sub/b.txt"),
        }));

        // A folder entry cannot ride the first-file kind:"file" fallback, so
        // the minor-0 link is skipped ENTIRELY — and kept up.
        Assert.Equal(new[] { "new" }, res.Delivered);
        Assert.Equal(0, res.OldPeerDrops);
        Assert.Empty(res.SizeSkipped);
        Assert.Equal(2, m.ActiveLinkCount);

        var got = await modern.ReceiveMessageAsync(cts.Token);
        Assert.Equal("files", got!.Kind);
        Assert.Equal("docs/a.txt", got.Files![0].Path);
        Assert.Equal("docs/sub/b.txt", got.Files[1].Path);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MinorZeroFallbackPicksTheFirstLooseFileAndMinorTwoGetsTheSameFrame()
    {
        var m = MakeManager("tok", 28723, "mixed", new(), new());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var oldPeer = await RawHandshake(28723, "tok", "o-node", "old-mix", 0, cts.Token);
        using var mid = await RawHandshake(28723, "tok", "m-node", "mid-mix", 2, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var res = await m.BroadcastAsync(new FilesClip(new List<FileEntry>
        {
            new("tree.txt", "one"u8.ToArray(), "docs/tree.txt"),
            new("loose.txt", "two"u8.ToArray()),
        }));
        Assert.Equal(2, res.Sent);
        Assert.Equal(1, res.OldPeerDrops);

        // Minor 0: the folder entry is excluded, so the fallback carries the
        // first LOOSE file, not files[0].
        var toOld = await oldPeer.ReceiveMessageAsync(cts.Token);
        Assert.Equal("file", toOld!.Kind);
        Assert.Equal("loose.txt", toOld.Name);

        // Minor 1-2: the SAME files frame, paths intact — the peer flattens
        // benignly because its strict decoder reads only name + content.
        var toMid = await mid.ReceiveMessageAsync(cts.Token);
        Assert.Equal("files", toMid!.Kind);
        Assert.Equal(2, toMid.Files!.Count);
        Assert.Equal("docs/tree.txt", toMid.Files[0].Path);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }
```

  Then append the required **interop** fact to `InteropTests` in `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/InteropTests.cs`. `LinkManagerTests.FolderOnlyClipSendsNothingToAMinorZeroPeer` above uses a raw in-process handshake and is NOT a substitute: the spec's Testing section requires the minor-0 folder case against the real Python peer, and `fake_peer.py` stays **UNMODIFIED** (still minor 0), so it doubles as the wire-compatibility check that a folder-only clip does not confuse a genuine pre-1.3 decoder.

```csharp
    [Fact]
    public async Task InteropFolderOnlyClipSendsNothingToTheMinorZeroPythonPeer()
    {
        int port = 28637;
        string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
        using var proc = Process.Start(FakePeerPsi(port, outFile))!;
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            var manager = new LinkManager(
                new LinkConfig("interop-token", 28638, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (_, _) => Task.CompletedTask;
            manager.Emit = _ => { };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);
            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 1));

            // Every entry came from a copied folder, and protocol 1.0's single
            // kind:"file" frame has nowhere to put a path -> nothing is sent to
            // this peer at all.
            var res = await manager.BroadcastAsync(new FilesClip(new List<FileEntry>
            {
                new("a.txt", "one"u8.ToArray(), "docs/a.txt"),
                new("b.txt", "two"u8.ToArray(), "docs/sub/b.txt"),
            }));
            Assert.Empty(res.Delivered);
            Assert.Equal(0, res.OldPeerDrops);
            // Skipping is NOT dropping the link: the peer stays connected and a
            // following ordinary clip still reaches it.
            Assert.Equal(1, manager.ActiveLinkCount);

            // Sentinel: waiting on a clip that DOES arrive is what turns
            // "nothing was written" into a real assertion instead of a race
            // against a still-empty file.
            await manager.BroadcastAsync(new TextClip("after-folder"));
            Assert.True(await WaitUntil(() =>
                File.Exists(outFile) && ReadShared(outFile).Contains("after-folder")));
            var seen = ReadShared(outFile);
            Assert.DoesNotContain("\"kind\": \"files\"", seen);
            Assert.DoesNotContain("a.txt", seen);

            manager.Shutdown();
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }
```

- [ ] **Step 3: Run all three, expected FAIL.** `FolderExpander`, `FolderExpander.WirePathFor` and `LinkManager.FlattenNoticeMessage` do not exist (CS0103/CS0117); the broadcast and interop facts would otherwise fail on the minor-0 link receiving a downgraded `file` frame built from `files[0]` (the folder entry).

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet test /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests --filter "FullyQualifiedName~FolderExpanderTests|FullyQualifiedName~LinkManagerTests|FullyQualifiedName~InteropTests"
```

- [ ] **Step 4: Create `FolderExpander.cs`.** Write `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/FolderExpander.cs`:

```csharp
using System.Text;

namespace AnyClip.Core;

/// Send-side folder expansion (protocol 1.3). Where the watcher used to log
/// "folder on clipboard not synced (unsupported)" it now walks the folder and
/// turns it into kind:"files" entries carrying a "path" relative to the
/// selection. Lives in Core (not the WinForms assembly) so the whole rule set
/// is covered by the platform-neutral suite. Keep in lockstep with
/// anyclip.py's watcher expansion and Swift FolderExpander.
public static class FolderExpander
{
    /// Never synced, never counted toward the budget. Case-insensitive because
    /// the filesystems these come from are.
    public static readonly IReadOnlyList<string> JunkNames =
        new[] { ".DS_Store", "Thumbs.db", "desktop.ini" };

    private static readonly HashSet<string> Junk =
        new(JunkNames, StringComparer.OrdinalIgnoreCase);

    /// One expanded selection.
    ///  - Entries: what to send, in selection order; loose files have RelPath null.
    ///  - TooLargeFolders / EmptyFolders: display names for the pinned toasts.
    ///  - SkippedFiles: LOOSE files dropped by the greedy budget/cap, i.e. the
    ///    existing "N file(s) skipped (too large to sync)" toast. A skipped
    ///    folder never lands here — it gets its own toast.
    public sealed record Plan(
        IReadOnlyList<FileEntry> Entries,
        IReadOnlyList<string> TooLargeFolders,
        IReadOnlyList<string> EmptyFolders,
        int SkippedFiles);

    /// Byte-wise comparison of two strings' UTF-8 encodings. The walk order has
    /// to be identical on all three implementations, and UTF-8 byte order is
    /// code-point order (what Python's sorted() gives). StringComparer.Ordinal
    /// compares UTF-16 code units instead, which puts astral characters BEFORE
    /// U+E000..U+FFFF (surrogates are 0xD800..0xDFFF) and would diverge.
    public static int CompareUtf8(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    /// The single choke point for "may this path go on the wire?". Returns
    /// `relPath` when it passes EVERY wire rule, or null when the file must ship
    /// as a LOOSE entry instead.
    ///
    /// The sender MUST NOT emit a path its own validator rejects, and a real
    /// filesystem can produce one: a name containing '\' (legal on macOS/Linux,
    /// reachable on Windows through a mounted share), a tree deeper than
    /// Wire.MaxPathSegments, or a sanitized path over
    /// Wire.MaxSanitizedPathLength characters. Dropping to loose keeps the file
    /// syncing and lands it exactly where the receiver would have put it anyway
    /// (an invalid path falls back to flat placement for that entry), whereas
    /// skipping would silently lose data the user asked to copy.
    /// Keep in lockstep with anyclip.py's watcher expansion and Swift
    /// FolderExpander.wirePathFor.
    public static string? WirePathFor(string relPath, string name)
    {
        if (Wire.IsValidRelPath(relPath, name)) return relPath;
        RotatingLog.Shared.Warning(
            $"path not representable on the wire ({relPath}); "
            + $"sending {name} as a loose file");
        return null;
    }

    /// The top-level name a folder ships under (and the name used in toasts).
    /// A drive root has no basename, so it keeps its raw path.
    public static string FolderDisplayName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// Recursive walk of `root`: files only, symlinks never followed, junk
    /// excluded, empty directories dropped (they are not representable on the
    /// wire). RelPath is "<top folder>/<relative path>", '/'-separated and NFC
    /// per segment so the sort order is the same on every platform.
    ///
    /// RelPath here is the RAW filesystem-derived path and is NOT yet known to
    /// be wire-legal — the walk sorts on it, then ExpandAsync runs every value
    /// through WirePathFor before it becomes a FileEntry.
    public static IReadOnlyList<(string FullPath, string RelPath, long Size)> Walk(string root)
    {
        var found = new List<(string FullPath, string RelPath, long Size)>();
        var stack = new Stack<(string Dir, string Prefix)>();
        stack.Push((root, TextHelpers.ToNfc(FolderDisplayName(root))));
        while (stack.Count > 0)
        {
            var (dir, prefix) = stack.Pop();
            string[] children;
            try { children = Directory.GetFileSystemEntries(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RotatingLog.Shared.Warning($"folder walk failed for {dir}: {e.Message}; skipping");
                continue;
            }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                FileAttributes attrs;
                // File.GetAttributes does NOT follow the link: a symlink reports
                // ReparsePoint (a dangling one included, without throwing). The
                // catch covers the enumerate-then-stat race where the entry is
                // gone by the time we look at it.
                try { attrs = File.GetAttributes(child); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    RotatingLog.Shared.Warning($"stat failed for {child}: {e.Message}; skipping");
                    continue;
                }
                // Checked BEFORE the directory branch: Directory.Exists FOLLOWS
                // a symlink to a folder, and following one would both leak files
                // from outside the selection and reintroduce cycles.
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    RotatingLog.Shared.Info($"symlink not synced (never followed): {child}");
                    continue;
                }
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    stack.Push((child, prefix + "/" + TextHelpers.ToNfc(name)));
                    continue;
                }
                if (Junk.Contains(name))
                {
                    RotatingLog.Shared.Debug($"junk file excluded from folder sync: {child}");
                    continue;
                }
                long size;
                try { size = new FileInfo(child).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    RotatingLog.Shared.Warning($"file stat failed for {child}: {e.Message}; skipping");
                    continue;
                }
                found.Add((child, prefix + "/" + TextHelpers.ToNfc(name), size));
            }
        }
        found.Sort((x, y) => CompareUtf8(x.RelPath, y.RelPath));
        return found;
    }

    /// Expand one clipboard selection. Items are processed in SELECTION order,
    /// each consuming the remaining budget/count:
    ///  - a folder is PER-FOLDER ALL-OR-NOTHING — its entire walked total must
    ///    fit what remains, or the whole folder is skipped (no partial trees);
    ///  - loose files keep today's greedy per-file behaviour.
    public static async Task<Plan> ExpandAsync(
        IReadOnlyList<string> selection, long budget, int maxFiles)
    {
        var entries = new List<FileEntry>();
        var tooLarge = new List<string>();
        var empty = new List<string>();
        int skipped = 0;
        long used = 0;

        foreach (var item in selection)
        {
            if (Directory.Exists(item))
            {
                var display = FolderDisplayName(item);
                var walked = Walk(item);
                if (walked.Count == 0)
                {
                    RotatingLog.Shared.Info(
                        $"folder {display} has no syncable files after exclusions");
                    empty.Add(display);
                    continue;
                }
                long total = 0;
                foreach (var w in walked) total += w.Size;
                // Decided BEFORE any content is read.
                if (entries.Count + walked.Count > maxFiles || used + total > budget)
                {
                    RotatingLog.Shared.Info(
                        $"folder {display} does not fit the remaining clip budget "
                        + $"({walked.Count} files, {total} bytes); skipping the whole folder");
                    tooLarge.Add(display);
                    continue;
                }
                long readBytes = 0;
                foreach (var w in walked)
                {
                    byte[] data;
                    // The all-or-nothing decision is made on the pre-read totals;
                    // a file that vanishes mid-read is a race, not a budget
                    // failure, so it is dropped individually rather than
                    // discarding an otherwise-good tree.
                    try { data = await File.ReadAllBytesAsync(w.FullPath); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        RotatingLog.Shared.Warning(
                            $"file read failed for {w.FullPath}: {e.Message}; dropping from {display}");
                        continue;
                    }
                    readBytes += data.Length;
                    var leafName = Path.GetFileName(w.FullPath);
                    // The ONLY place a path reaches the wire from this sender.
                    // A path the receiver would reject ships as a loose entry
                    // (RelPath null) instead — the file always goes.
                    entries.Add(new FileEntry(
                        leafName, data, WirePathFor(w.RelPath, leafName)));
                }
                used += readBytes;
                continue;
            }

            // Loose file: greedy, unchanged since 1.3.0.
            if (entries.Count >= maxFiles) { skipped++; continue; }
            long fileSize;
            try { fileSize = new FileInfo(item).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RotatingLog.Shared.Warning($"file stat failed for {item}: {e.Message}; skipping");
                skipped++; continue;
            }
            if (used + fileSize > budget) { skipped++; continue; }
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(item); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RotatingLog.Shared.Warning($"file read failed for {item}: {e.Message}; skipping");
                skipped++; continue;
            }
            used += fileSize;
            entries.Add(new FileEntry(Path.GetFileName(item), bytes));
        }
        return new Plan(entries, tooLarge, empty, skipped);
    }
}
```

- [ ] **Step 5: `LinkManager.cs` — minor-0 exclusion + flatten notice.** Add the pinned string builder immediately after the `BroadcastResult` record struct closes — line 44 is `public int Sent => Delivered.Count;`, line 45 is the `}` that closes the record struct, line 46 is blank, so the insertion goes after line 45:

```csharp
    /// Logged once per clip per affected link: the peer takes the kind:"files"
    /// frame but is on protocol &lt; 1.3, so its strict decoder reads only name +
    /// content and the tree lands flat. Pinned wording across all three
    /// implementations. Keep in lockstep with anyclip.flatten_notice and Swift
    /// flattenNotice.
    public static string FlattenNoticeMessage(string peerName) =>
        $"peer {peerName} will flatten folders (protocol < 1.3)";
```

  and replace the per-link variant chooser in `BroadcastAsync` (the `foreach (var link in targets)` body, lines 394–431 after Task 7) with:

```csharp
        foreach (var link in targets)
        {
            var toSend = payload;
            int dropped = 0;
            bool flattens = false;
            if (payload is FilesClip fc)
            {
                if (link.PeerProtocolMinor < 1)
                {
                    // Protocol 1.0 takes one kind:"file" frame, which has no
                    // place to carry a path — a folder entry would land loose
                    // and unlabelled. Folder-derived entries are therefore
                    // EXCLUDED from the fallback: a folder-only clip sends
                    // NOTHING on this link (log only, link kept). Loose files
                    // keep the first-file fallback.
                    var loose = fc.Files.FirstOrDefault(f => f.RelPath is null);
                    if (loose is null)
                    {
                        RotatingLog.Shared.Info(
                            $"folder clip not sent to {link.PeerName} "
                            + "(peer protocol < 1.1 cannot carry folders)");
                        continue;
                    }
                    dropped = fc.Files.Count - 1;
                    toSend = new FileClip(loose.Name, loose.Data);
                }
                else
                    flattens = link.PeerProtocolMinor < 3
                        && fc.Files.Any(f => f.RelPath is not null);
            }
            if (!frames.TryGetValue(toSend.Kind, out var cached))
            {
                try { cached = WireMessage.Clip(toSend, ts).Encode(); }
                catch (PayloadTooLargeException e)
                {
                    RotatingLog.Shared.Warning($"payload too large, dropping: {e.Message}");
                    cached = null;
                }
                frames[toSend.Kind] = cached;
            }
            if (cached is not { } frame) continue;
            if (!Wire.LinkAcceptsFrame(frame.BodyCount, link.PeerProtocolMinor))
            {
                RotatingLog.Shared.Info(
                    $"clip too large for '{link.PeerName}' (peer protocol < 1.2); skipping");
                sizeSkipped.Add(link.PeerName);
                continue;
            }
            if (!await link.SendEncodedAsync(frame)) continue;
            if (dropped > 0)
                RotatingLog.Shared.Info(
                    $"peer {link.PeerName} protocol minor {link.PeerProtocolMinor} < 1: "
                    + $"sent 1 of {dropped + 1} files");
            if (flattens) RotatingLog.Shared.Info(FlattenNoticeMessage(link.PeerName));
            // Only a DELIVERED downgrade counts toward the fallback toast: a
            // gated or failed link received nothing to leave files behind on.
            oldPeerDrops = Math.Max(oldPeerDrops, dropped);
            delivered.Add(link.PeerName);
        }
```

- [ ] **Step 6: Run the Core suite, expected PASS.**

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet test /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests
```

- [ ] **Step 7: Write the watcher tests FIRST — before `ClipboardWatcher.cs`.** The tests state what the new behaviour is; the implementation then satisfies them. Writing `ClipboardWatcher.cs` first, with the tests bolted on afterwards, means nothing ever pinned the intent. `AnyClipApp.Tests` can only be RUN on Windows CI, but it does COMPILE on this Mac (`EnableWindowsTargeting` is set in both `AnyClipApp.csproj` and `AnyClipApp.Tests.csproj`), which is the gate added at the end of this step and again in Step 9. Do NOT write `ClipboardWatcher.cs` before this step.

  In `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipApp.Tests/ClipboardLogicTests.cs`, the three folder facts assert the REPLACED wording and must be rewritten. Replace `FolderSkippedOnceWithToastAndFileSent` (lines 87–106), `FolderMixedWithFilesSkipsFolderSyncsFiles` (lines 243–258) and `MultipleFoldersEmitOneAggregatedSkip` (lines 260–274) with:

```csharp
    [Fact]
    public async Task EmptyFolderToastsTheEmptyWordingAndIsNotReDetected()
    {
        var dir = TempDir();
        var (w, clip, changes, skipped) = Make(dir);
        var folder = TempDir();                 // no files inside
        clip.FilePaths = new List<string> { folder };
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
        Assert.Equal("folder is empty; nothing to sync", Assert.Single(skipped));
        await w.HandleClipboardUpdateAsync();
        Assert.Single(skipped);                 // fingerprint recorded -> no re-detect
    }

    [Fact]
    public async Task FolderIsExpandedIntoAFilesClipWithPaths()
    {
        var (w, clip, changes, skipped) = Make(TempDir());
        var folder = Path.Combine(TempDir(), "docs");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "bbb");
        clip.FilePaths = new List<string> { folder };
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(skipped);
        var fc = Assert.IsType<FilesClip>(Assert.Single(changes));
        Assert.Equal(new[] { "docs/a.txt", "docs/sub/b.txt" },
            fc.Files.Select(f => f.RelPath).ToArray());
    }

    [Fact]
    public async Task FolderMixedWithFilesShipsBothInOneClip()
    {
        var (w, clip, changes, skipped) = Make(TempDir());
        var d = TempDir();
        var folder = Path.Combine(TempDir(), "docs");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "i");
        var f1 = Path.Combine(d, "keep.txt"); File.WriteAllText(f1, "k");
        clip.FilePaths = new List<string> { folder, f1 };
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(skipped);
        var fc = Assert.IsType<FilesClip>(Assert.Single(changes));
        // Selection order; the loose file carries no path.
        Assert.Equal(new string?[] { "docs/inside.txt", null },
            fc.Files.Select(f => f.RelPath).ToArray());
    }

    [Fact]
    public async Task OversizeFolderToastsThePinnedStringAndSendsNothing()
    {
        var (w, clip, changes, skipped) = Make(TempDir());
        var folder = Path.Combine(TempDir(), "heavy");
        Directory.CreateDirectory(folder);
        using (var fs = File.Create(Path.Combine(folder, "big.bin")))
            fs.SetLength((long)ClipboardWatcher.FileBudget + 1);
        clip.FilePaths = new List<string> { folder };
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
        Assert.Equal("folder too large to sync: heavy", Assert.Single(skipped));
    }

    [Fact]
    public void MaxFilesPerClipIsFiveHundred()
    {
        Assert.Equal(500, ClipboardWatcher.MaxFilesPerClip);
    }
```

  **App-test gap (explicit):** `AnyClipApp.Tests` only runs on Windows CI, so these five facts cannot be EXECUTED on this Mac — but they are COMPILED here (Steps 7 and 9), so a typo or a wrong signature is caught locally. Everything they cover that is NOT WinForms-specific (walk order, junk/symlink exclusion, all-or-nothing at both boundaries, empty folders, loose-file greed, selection order) is duplicated in `FolderExpanderTests` in the platform-neutral suite, which IS run in Step 6. What stays Windows-only here: `ClipboardWatcher`'s own wiring — fingerprint recording, toast dispatch order, and the single-loose-file → `FileClip` collapse.

  Now build. **Expected: PASS, 0 errors — and a failure here means the new tests are broken, not that the red is working.** Be clear about what this gate is and is not: after Task 7 every symbol these facts touch already exists (`ClipboardWatcher.MaxFilesPerClip` is present, just still 100; `FileBudget`, `Make`, `TempDir`, `clip.Written` are all unchanged; `FileEntry`/`FilesClip.Files[].RelPath` arrived in Task 7), so the `AnyClipApp` layer cannot give a red BUILD, and it cannot give a red TEST either because it only RUNS on Windows CI. These five facts are red at runtime, on CI, for the right reasons (cap still 100, folders still skipped). What this build buys is the verification that was missing entirely: the facts compile — no typo, no wrong signature, no stale API — *before* an implementation is written on top of them.

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet build /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipApp.Tests
```

- [ ] **Step 8: `ClipboardWatcher.cs` — cap 500 and expansion.** Replace line 107 with:

```csharp
    /// Sender-side cap; receiver stays lenient. Raised 100 -> 500 for protocol
    /// 1.3: a document tree passes 100 files easily and FileBudget is the real
    /// limit (worst-case extra JSON envelope still fits the 256 KiB reservation).
    public const int MaxFilesPerClip = 500;
```

  and replace `CheckFileClipboardAsync` (lines 268–337) with:

```csharp
    private async Task CheckFileClipboardAsync()
    {
        var paths = SafeRead(_clipboard.GetFilePaths);
        if (paths is null || paths.Count == 0) return;
        var fps = FingerprintList(paths);
        if (fps.Count == 0 || fps.SequenceEqual(_lastFileFingerprints)) return;
        // Record FIRST — the fingerprint is always taken (even if everything is
        // skipped) so nothing retry-loops. A folder fingerprints as
        // (path, -1, dir mtime), which the expansion below never mutates, so an
        // unsyncable or just-sent folder is not re-detected on the next update.
        _lastFileFingerprints = fps;

        // Folders are EXPANDED, not skipped (protocol 1.3): each becomes a set
        // of entries carrying a "path" relative to the copied folder. This
        // REPLACES the old "folder on clipboard not synced (unsupported)" path.
        // Per-folder all-or-nothing against the remaining budget/count; loose
        // files keep the greedy per-file rule and its existing toast.
        var plan = await FolderExpander.ExpandAsync(paths, FileBudget, MaxFilesPerClip);
        foreach (var name in plan.TooLargeFolders)
            await SafeSkipAsync($"folder too large to sync: {name}");
        // One toast however many folders came back empty — the wording names none.
        if (plan.EmptyFolders.Count > 0)
            await SafeSkipAsync("folder is empty; nothing to sync");
        if (plan.SkippedFiles > 0)
            await SafeSkipAsync($"{plan.SkippedFiles} file(s) skipped (too large to sync)");

        if (plan.Entries.Count == 0) return;
        // A single LOOSE file keeps the legacy kind:"file" frame; a single
        // folder-derived file must stay kind:"files" or its path is lost.
        ClipPayload payload = plan.Entries.Count == 1 && plan.Entries[0].RelPath is null
            ? new FileClip(plan.Entries[0].Name, plan.Entries[0].Data)
            : new FilesClip(plan.Entries);
        try { await (OnLocalChange?.Invoke(payload) ?? Task.CompletedTask); }
        catch (Exception e)
        { RotatingLog.Shared.Error($"on_change(files) handler failed: {e}"); }
    }
```

- [ ] **Step 9: Build `AnyClipApp.Tests`, expected PASS (0 errors).** `EnableWindowsTargeting` is set in `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipApp/AnyClipApp.csproj` and `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipApp.Tests/AnyClipApp.Tests.csproj` ("Allows building (not running) this project on macOS/Linux"), so the `net8.0-windows` target **does** build on this Mac — it takes a couple of seconds. This compile-verifies both the `ClipboardWatcher.cs` edits and the five new Windows-only facts. Running them still needs Windows CI; do NOT push to get that signal.

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet build /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipApp.Tests
```

- [ ] **Step 10: Commit.**

```bash
cd /Users/seojeonghwa/project/AnyClip && git add forwindows/src forwindows/tests && git commit -m "$(cat <<'EOF'
feat(sender-c#): expand copied folders into path-carrying files clips

Add FolderExpander (recursive walk, files only, UTF-8 byte-sorted, junk
and symlinks excluded, empty dirs dropped) in Core so the rules are
covered by the platform-neutral suite. Per-folder all-or-nothing against
the remaining budget/count with the pinned "folder too large to sync"
and "folder is empty" toasts; loose files keep today's greedy rule.
WirePathFor validates every path before it goes out, so a tree the
receiver would reject ships its file as a loose entry instead of a bad
path. Raise MaxFilesPerClip to 500. In the broadcast fan-out, folder entries
are excluded from the protocol-1.0 first-file fallback (a folder-only
clip sends nothing on that link) and a minor 1-2 link logs the pinned
flatten notice.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: C# receiver — rebuild folder trees under `received/` and place top-level items

**Files:**
- Create `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/ReceivedTree.cs`
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/Daemon.cs` (`ClearDirectoryFiles` lines 70–77 and its two call sites, lines 112 and 266; the pre-write summary line inserted before line 128; `FilesClip` received toast, lines 147–154)
- Modify `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipApp/ClipboardWatcher.cs` (`ApplyRemoteAsync` `FilesClip` case, lines 375–398)
- Create test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/ReceivedTreeTests.cs`
- Modify test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/DaemonTests.cs` (`ClearDirectoryFilesKeepsSubdirs`, lines 356–365)
- Modify test `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipApp.Tests/ClipboardLogicTests.cs` (**Windows-CI-only to RUN, compile-checked here**: add one fact)

**Interfaces:**

Consumes: `FileEntry(string Name, byte[] Data, string? RelPath = null)`, `FilesClip(IReadOnlyList<FileEntry>)` (Task 7) · `Wire.IsValidRelPath(string?, string)`, `TextHelpers.SanitizePathSegments(string)` (Task 7) · `TextHelpers.SanitizeFilename(string)`, `TextHelpers.UniquifyNames(IReadOnlyList<string>)`, `TextHelpers.ToNfc(string)` · `Daemon.ClearDirectoryFiles(string)` (renamed here) · `ClipboardWatcher._receivedDir`, `ClipboardWatcher.FingerprintList(IReadOnlyList<string>)`, `IWin32Clipboard.SetFilePaths(IReadOnlyList<string>)`

Produces:
- `static class ReceivedTree` with `readonly record struct Placement(string RelativePath, string TopItem)`, `IReadOnlyList<Placement> Plan(IReadOnlyList<FileEntry> files, Func<string, bool> topExists)`, `Func<string, bool> TopExistsIn(string receivedDir)`, `IReadOnlyList<string> TopLevelItems(IReadOnlyList<Placement> placements)`, `bool ResolvesUnder(string root, string relativePath)`, `IReadOnlyList<string> Write(string receivedDir, IReadOnlyList<FileEntry> files)`, `string ReceivedSummary(IReadOnlyList<Placement> placements)` + `string ReceivedSummary(IReadOnlyList<FileEntry> files, string receivedDir)`
- `static void Daemon.ClearReceivedDir(string dir)` (replaces `ClearDirectoryFiles`)

**The received toast must name the folder that actually exists on disk.** The summary is derived from `Placement.TopItem` — the sanitized, uniquified top segment `Plan` produced — never from the raw wire `path`. Two reasons: after a top-segment collision the clip lands in `received/docs-2` while the raw segment still says `docs`, which would point the user at the wrong folder; and the raw segment is attacker-controlled and unsanitized, so it would reach a notification verbatim.

Because `Plan` is deterministic given what is already under `received/`, the Daemon computes the summary **before** `ApplyRemoteAsync` writes anything — at that moment `topExists` answers exactly what the write is about to see. Summarizing afterwards would re-plan against the folder the write just created and bump the name a second time (`docs-2` → `docs-3`). Both calls go through `TopExistsIn` so there is one definition of "already taken". No `IClipboardSync` change is needed; received applies are already serialized on the manager's single apply queue, so nothing can write to `received/` between the summary and the apply.

- [ ] **Step 1: Write the failing placement tests.** Create `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests/ReceivedTreeTests.cs`:

```csharp
using System.Text;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

/// Receive-side tree rebuild (protocol 1.3). Entries WITHOUT a path behave
/// exactly as they did in 1.3.0; entries with one are validated and, on ANY
/// violation, fall back to flat placement for that entry alone.
public class ReceivedTreeTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "anyclip-recv-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    private static FileEntry E(string name, string? relPath = null, string body = "x") =>
        new(name, Encoding.UTF8.GetBytes(body), relPath);

    private static readonly Func<string, bool> NothingExists = _ => false;

    [Fact]
    public void ValidPathsRebuildOneTreeAndKeepBatchOrder()
    {
        var plan = ReceivedTree.Plan(new[]
        {
            E("a.txt", "docs/a.txt"),
            E("b.txt", "docs/sub/b.txt"),
            E("loose.txt"),
        }, NothingExists);
        Assert.Equal(new[] { "docs/a.txt", "docs/sub/b.txt", "loose.txt" },
            plan.Select(p => p.RelativePath).ToArray());
        // One clipboard item per top-level thing, de-duped in batch order.
        Assert.Equal(new[] { "docs", "loose.txt" },
            ReceivedTree.TopLevelItems(plan).ToArray());
    }

    [Fact]
    public void AnyRuleViolationFallsBackToFlatForThatEntryOnly()
    {
        var plan = ReceivedTree.Plan(new[]
        {
            E("ok.txt", "docs/ok.txt"),
            E("evil.txt", "../../etc/evil.txt"),   // traversal
            E("abs.txt", "/etc/abs.txt"),          // absolute
            E("drv.txt", "C:/Windows/drv.txt"),    // drive letter
            E("back.txt", "docs\\back.txt"),       // backslash
            E("mismatch.txt", "docs/other.txt"),   // last segment != name
        }, NothingExists);
        Assert.Equal(
            new[] { "docs/ok.txt", "evil.txt", "abs.txt", "drv.txt", "back.txt", "mismatch.txt" },
            plan.Select(p => p.RelativePath).ToArray());
        // The frame is never dropped and nothing escapes received/.
        Assert.All(plan, p => Assert.True(ReceivedTree.ResolvesUnder("/tmp/received", p.RelativePath)));
    }

    [Fact]
    public void TopSegmentUniquifyIsAppliedToEveryEntryOfThatFolder()
    {
        // received/docs already exists -> the WHOLE clip moves to docs-2, so one
        // copied folder always lands in exactly one new folder.
        var plan = ReceivedTree.Plan(new[]
        {
            E("a.txt", "docs/a.txt"),
            E("b.txt", "docs/sub/b.txt"),
            E("c.txt", "notes/c.txt"),
        }, top => top == "docs");
        Assert.Equal(new[] { "docs-2/a.txt", "docs-2/sub/b.txt", "notes/c.txt" },
            plan.Select(p => p.RelativePath).ToArray());
        Assert.Equal(new[] { "docs-2", "notes" }, ReceivedTree.TopLevelItems(plan).ToArray());
    }

    [Fact]
    public void TopUniquifyKeepsBumpingAndAvoidsLooseFileNames()
    {
        var plan = ReceivedTree.Plan(new[]
        {
            E("docs"),                              // a loose file literally named "docs"
            E("a.txt", "docs/a.txt"),
        }, top => top == "docs-2");                 // docs-2 already on disk
        Assert.Equal(new[] { "docs", "docs-3/a.txt" },
            plan.Select(p => p.RelativePath).ToArray());
    }

    [Fact]
    public void FlatEntriesKeepTodaysWithinBatchUniquify()
    {
        var plan = ReceivedTree.Plan(new[]
        {
            E("note.txt"), E("note.txt"), E("(E&S) plan.txt"),
        }, NothingExists);
        Assert.Equal(new[] { "note.txt", "note (2).txt", "(E&S) plan.txt" },
            plan.Select(p => p.RelativePath).ToArray());
    }

    [Fact]
    public void EverySegmentIsSanitizedAndNfcNormalized()
    {
        var nfd = "보고서".Normalize(NormalizationForm.FormD);
        var plan = ReceivedTree.Plan(new[]
        {
            E("메모.txt".Normalize(NormalizationForm.FormD),
                nfd + "/CON/메모.txt".Normalize(NormalizationForm.FormD)),
        }, NothingExists);
        Assert.Equal(
            "보고서".Normalize(NormalizationForm.FormC) + "/_CON/"
            + "메모.txt".Normalize(NormalizationForm.FormC),
            plan[0].RelativePath);
    }

    [Fact]
    public void ResolvesUnderRejectsEscapes()
    {
        var root = TempDir();
        Assert.True(ReceivedTree.ResolvesUnder(root, "docs/a.txt"));
        Assert.True(ReceivedTree.ResolvesUnder(root, "a.txt"));
        Assert.False(ReceivedTree.ResolvesUnder(root, "../a.txt"));
        Assert.False(ReceivedTree.ResolvesUnder(root, "docs/../../a.txt"));
        Assert.False(ReceivedTree.ResolvesUnder(root, ""));   // resolves to root itself
    }

    [Fact]
    public void WriteRebuildsARealTreeAndReturnsTopLevelAbsolutePaths()
    {
        var root = TempDir();
        var placed = ReceivedTree.Write(root, new[]
        {
            E("a.txt", "docs/a.txt", "aaa"),
            E("b.txt", "docs/sub/deeper/b.txt", "bbb"),
            E("loose.txt", null, "lll"),
        });
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(root, "docs", "a.txt")));
        Assert.Equal("bbb",
            File.ReadAllText(Path.Combine(root, "docs", "sub", "deeper", "b.txt")));
        Assert.Equal("lll", File.ReadAllText(Path.Combine(root, "loose.txt")));
        // Intermediate dirs created; clipboard gets the FOLDER once + the file.
        Assert.Equal(
            new[] { Path.Combine(root, "docs"), Path.Combine(root, "loose.txt") },
            placed.ToArray());
    }

    [Fact]
    public void WriteUniquifiesAgainstAnExistingTopFolderOnDisk()
    {
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        var placed = ReceivedTree.Write(root, new[] { E("a.txt", "docs/a.txt", "second") });
        Assert.Equal(new[] { Path.Combine(root, "docs-2") }, placed.ToArray());
        Assert.Equal("second", File.ReadAllText(Path.Combine(root, "docs-2", "a.txt")));
    }

    [Fact]
    public void ReceivedSummaryNamesAFolderOnlyClipAndCountsAnythingElse()
    {
        var root = TempDir();
        Assert.Equal("docs (2 files)", ReceivedTree.ReceivedSummary(new[]
        {
            E("a.txt", "docs/a.txt"), E("b.txt", "docs/sub/b.txt"),
        }, root));
        // Two folders, or a folder plus a loose file, keep the plain count.
        Assert.Equal("2 files", ReceivedTree.ReceivedSummary(new[]
        {
            E("a.txt", "docs/a.txt"), E("b.txt", "notes/b.txt"),
        }, root));
        Assert.Equal("2 files", ReceivedTree.ReceivedSummary(new[]
        {
            E("a.txt", "docs/a.txt"), E("loose.txt"),
        }, root));
        Assert.Equal("3 files", ReceivedTree.ReceivedSummary(new[]
        {
            E("a.txt"), E("b.txt"), E("c.txt"),
        }, root));
    }

    [Fact]
    public void ReceivedSummaryNamesTheFolderTHISCLIPWILLLANDIN()
    {
        // received/docs is taken, so the clip goes to docs-2 — and the toast has
        // to say docs-2, or it points the user at somebody else's folder.
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        Assert.Equal("docs-2 (2 files)", ReceivedTree.ReceivedSummary(new[]
        {
            E("a.txt", "docs/a.txt"), E("b.txt", "docs/sub/b.txt"),
        }, root));
    }

    [Fact]
    public void ReceivedSummaryNeverEchoesARawWirePathIntoTheToast()
    {
        // The top segment is attacker-controlled. It reaches the summary only
        // after Plan has sanitized it. ('a|b' passes IsValidRelPath — the
        // validator constrains separators and segments, not the denylist — and
        // is then sanitized to 'a_b'. Do NOT use 'a:b' here: that trips the
        // drive-letter rule and goes flat before sanitization is reached.)
        var root = TempDir();
        Assert.Equal("a_b (1 files)",
            ReceivedTree.ReceivedSummary(new[] { E("x.txt", "a|b/x.txt") }, root));
        // A path that fails validation is flat, so the toast names nothing.
        Assert.Equal("1 files",
            ReceivedTree.ReceivedSummary(new[] { E("x.txt", "../../etc/x.txt") }, root));
    }

    [Fact]
    public void ReceivedSummaryFromPlacementsIsPure()
    {
        // The overload the Daemon does NOT call, pinned so the disk-probing one
        // stays a thin wrapper: a folder is a placement whose TopItem is not the
        // whole RelativePath (a flat entry's TopItem IS its own name).
        Assert.Equal("docs (2 files)", ReceivedTree.ReceivedSummary(new[]
        {
            new ReceivedTree.Placement("docs/a.txt", "docs"),
            new ReceivedTree.Placement("docs/sub/b.txt", "docs"),
        }));
        Assert.Equal("1 files", ReceivedTree.ReceivedSummary(new[]
        {
            new ReceivedTree.Placement("loose.txt", "loose.txt"),
        }));
        Assert.Equal("0 files", ReceivedTree.ReceivedSummary(Array.Empty<ReceivedTree.Placement>()));
    }
}
```

  Replace `ClearDirectoryFilesKeepsSubdirs` in `DaemonTests.cs` (lines 356–365) with:

```csharp
    [Fact]
    public void ClearReceivedDirRemovesFilesAndWholeTrees()
    {
        // Since protocol 1.3 received/ holds folder TREES, not just loose
        // files. Leaving subdirectories behind would grow disk without bound
        // AND make every restart bump the top-folder uniquify (docs-2, docs-3,
        // ...), so the sweep is now recursive.
        var dir = Path.Combine(Path.GetTempPath(), "anyclip-clear-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(dir, "docs", "sub"));
        File.WriteAllText(Path.Combine(dir, "docs", "sub", "deep.txt"), "d");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        Daemon.ClearReceivedDir(dir);
        Assert.Empty(Directory.GetFileSystemEntries(dir));
        Assert.True(Directory.Exists(dir));   // the directory itself survives
    }
```

- [ ] **Step 2: Run the new tests, expected FAIL.** `ReceivedTree` and `Daemon.ClearReceivedDir` do not exist yet (CS0103/CS0117).

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet test /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests --filter "FullyQualifiedName~ReceivedTreeTests|FullyQualifiedName~ClearReceivedDir"
```

- [ ] **Step 3: Create `ReceivedTree.cs` — the planner.** Write `/Users/seojeonghwa/project/AnyClip/forwindows/src/AnyClipCore/ReceivedTree.cs`:

```csharp
namespace AnyClip.Core;

/// Receive-side placement for a kind:"files" clip (protocol 1.3). Entries
/// without a "path" behave exactly as they did in 1.3.0; entries with one are
/// validated against every wire rule and, on ANY violation, fall back to flat
/// placement for THAT entry — the frame is never dropped and nothing is ever
/// written outside received/. Lives in Core (not the WinForms assembly) so the
/// whole rebuild is covered by the platform-neutral suite; the App layer only
/// puts the returned paths on the clipboard. Keep in lockstep with
/// anyclip.update_local_files and Swift ReceivedTree.
public static class ReceivedTree
{
    /// Where one entry lands, relative to received/.
    ///  - RelativePath: '/'-joined SANITIZED segments; the writer swaps in the
    ///    platform separator.
    ///  - TopItem: the clipboard item this entry belongs to — the top folder for
    ///    a tree entry, the file itself for a flat one. Repeats across entries
    ///    of one folder; TopLevelItems de-dupes it in batch order.
    public readonly record struct Placement(string RelativePath, string TopItem);

    /// Plan the whole clip. `topExists` answers "is there already something
    /// called this directly under received/?" — injected so the planner stays
    /// pure and testable.
    public static IReadOnlyList<Placement> Plan(
        IReadOnlyList<FileEntry> files, Func<string, bool> topExists)
    {
        // Pass 1: classify. A tree entry is one whose wire path passes EVERY
        // rule; anything else is flat.
        var segments = new List<IReadOnlyList<string>?>(files.Count);
        foreach (var f in files)
            segments.Add(f.RelPath is not null && Wire.IsValidRelPath(f.RelPath, f.Name)
                ? TextHelpers.SanitizePathSegments(f.RelPath)
                : null);

        // Pass 2: flat names keep today's within-batch uniquify, in order.
        var flatNames = new List<string>();
        for (int i = 0; i < files.Count; i++)
            if (segments[i] is null) flatNames.Add(TextHelpers.SanitizeFilename(files[i].Name));
        var uniqueFlat = TextHelpers.UniquifyNames(flatNames);

        // Pass 3: ONE replacement per DISTINCT top segment, in first-appearance
        // order, so every entry of one copied folder lands in the SAME new
        // folder even when the name had to be bumped. The claimed set starts
        // with the flat names (Ordinal — the same comparison rule as the
        // existing file uniquify), so a folder never collides with a loose file
        // of the same name either.
        var claimed = new HashSet<string>(uniqueFlat, StringComparer.Ordinal);
        var topMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in segments)
        {
            if (s is null) continue;
            var top = s[0];
            if (topMap.ContainsKey(top)) continue;
            var candidate = top;
            int n = 2;
            while (claimed.Contains(candidate) || topExists(candidate))
                candidate = $"{top}-{n++}";
            claimed.Add(candidate);
            topMap[top] = candidate;
        }

        // Pass 4: emit in the clip's own order.
        var result = new List<Placement>(files.Count);
        int flatIndex = 0;
        for (int i = 0; i < files.Count; i++)
        {
            if (segments[i] is not { } s)
            {
                var name = uniqueFlat[flatIndex++];
                result.Add(new Placement(name, name));
                continue;
            }
            var parts = s.ToArray();
            parts[0] = topMap[s[0]];
            result.Add(new Placement(string.Join("/", parts), parts[0]));
        }
        return result;
    }

    /// The clip's top-level items in batch order: each copied folder once, plus
    /// every loose file. This is what goes on the clipboard.
    public static IReadOnlyList<string> TopLevelItems(IReadOnlyList<Placement> placements)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<string>();
        foreach (var p in placements) if (seen.Add(p.TopItem)) items.Add(p.TopItem);
        return items;
    }

    /// Traversal backstop: true only when `relativePath` resolves strictly
    /// INSIDE `root`. Sanitization already strips '/', '\' and '..' from every
    /// segment, so this cannot fail on a planned path — it is the second lock
    /// on the one attack that matters, checked again before every write.
    public static bool ResolvesUnder(string root, string relativePath)
    {
        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(rootFull,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException) { return false; }
        return full.Length > rootFull.Length
            && full.StartsWith(rootFull, StringComparison.Ordinal);
    }

    /// "Is there already something called this directly under received/?" — the
    /// ONE definition of a taken top-level name, shared by Write and the toast
    /// summary so they can never disagree about whether a name had to be bumped.
    public static Func<string, bool> TopExistsIn(string receivedDir) =>
        top => Directory.Exists(Path.Combine(receivedDir, top))
            || File.Exists(Path.Combine(receivedDir, top));

    /// Body of the "AnyClip <- peer" toast for a files clip: a clip that is
    /// entirely ONE copied folder names it, anything else keeps today's count.
    ///
    /// Derived from Placement.TopItem — the SANITIZED, UNIQUIFIED name the clip
    /// actually lands under — never from the raw wire path. Naming the raw top
    /// segment would point the user at `docs` when a collision put the clip in
    /// `docs-2`, and would put attacker-controlled text straight into a
    /// notification. A flat placement's TopItem IS its own RelativePath, which
    /// is how "one folder" is told apart from "one loose file".
    /// Keep in lockstep with Swift/Python.
    public static string ReceivedSummary(IReadOnlyList<Placement> placements)
    {
        if (placements.Count == 0) return "0 files";
        var top = placements[0].TopItem;
        foreach (var p in placements)
            if (!string.Equals(p.TopItem, top, StringComparison.Ordinal)
                || string.Equals(p.RelativePath, p.TopItem, StringComparison.Ordinal))
                return $"{placements.Count} files";
        return $"{top} ({placements.Count} files)";
    }

    /// Convenience for the Daemon: plan against what is on disk RIGHT NOW, then
    /// summarize. MUST be called BEFORE the write — Plan is deterministic given
    /// received/, so pre-write it names the folder exactly as Write is about to
    /// create it; post-write it would see that folder and bump again.
    public static string ReceivedSummary(IReadOnlyList<FileEntry> files, string receivedDir) =>
        ReceivedSummary(Plan(files, TopExistsIn(receivedDir)));
}
```

- [ ] **Step 4: Add the writer to `ReceivedTree.cs`.** Append inside the class:

```csharp
    /// Write one received files clip under `receivedDir`, creating intermediate
    /// directories, and return the clip's TOP-LEVEL items as ABSOLUTE paths in
    /// batch order — exactly what the platform puts on the clipboard (CF_HDROP
    /// on Windows, NSPasteboard file URLs on macOS). IO exceptions propagate to
    /// the caller's existing narrow catch.
    public static IReadOnlyList<string> Write(
        string receivedDir, IReadOnlyList<FileEntry> files)
    {
        Directory.CreateDirectory(receivedDir);
        // Same predicate the Daemon's pre-write ReceivedSummary used, so the
        // toast names the folder this call is about to create.
        var plan = Plan(files, TopExistsIn(receivedDir));
        for (int i = 0; i < files.Count; i++)
        {
            var rel = plan[i].RelativePath;
            if (!ResolvesUnder(receivedDir, rel))
            {
                // Unreachable with sanitized segments; kept as the hard stop so
                // no path can ever escape received/.
                RotatingLog.Shared.Warning(
                    $"refusing out-of-tree destination '{rel}'; placing flat");
                rel = TextHelpers.SanitizeFilename(files[i].Name);
            }
            var target = Path.Combine(receivedDir,
                rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, files[i].Data);
        }
        return TopLevelItems(plan)
            .Select(t => Path.Combine(receivedDir, t)).ToList();
    }
```

- [ ] **Step 5: `Daemon.cs` — recursive sweep + folder-aware received toast.** Replace lines 70–77 with:

```csharp
    /// Empty received/ without removing it. Recursive since protocol 1.3: the
    /// directory now holds folder TREES, and leaving them behind would grow
    /// disk without bound AND make every restart bump the top-folder uniquify
    /// (docs-2, docs-3, ...). Keep in lockstep with anyclip.clear_received_dir.
    public static void ClearReceivedDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.GetFileSystemEntries(dir))
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { RotatingLog.Shared.Debug($"could not remove {entry}: {e.Message}"); }
    }
```

  Change both call sites — line 112 and line 266 — from `ClearDirectoryFiles(receivedDir);` to `ClearReceivedDir(receivedDir);`.

  Then the received toast. The summary must be computed **before** `ApplyRemoteAsync` writes anything (see the Interfaces note), so add one line at the top of the `manager.OnClip` handler — immediately after `coordinator.MarkReceived(...)` and **before** `bool ok = await clipboard.ApplyRemoteAsync(payload);` (line 128). `receivedDir` is the local declared at line 111 and is already in scope for this closure:

```csharp
            // Summarized BEFORE the write: ReceivedTree.Plan is deterministic
            // given what is under received/, so this names the top folder
            // EXACTLY as the write is about to create it — uniquify bump
            // included. After the write that folder exists and re-planning
            // would bump again (docs-2 -> docs-3).
            string? filesSummary = payload is FilesClip pre
                ? ReceivedTree.ReceivedSummary(pre.Files, receivedDir)
                : null;
```

  and replace the `FilesClip` receive arm (lines 147–154) with:

```csharp
                case FilesClip fsc:
                    if (fsc.Files.Count == 1)
                        coordinator.MarkReceived("file", Hashing.Sha256Hex(fsc.Files[0].Data));
                    RotatingLog.Shared.Info(
                        $"<- received {fsc.Files.Count} files from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    // Names the folder when the clip is one copied folder,
                    // otherwise today's plain count. Never the raw wire path.
                    toast($"AnyClip ← {peer}", filesSummary!);
                    break;
```

- [ ] **Step 6: Run the Core suite, expected PASS.**

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet test /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests
```

- [ ] **Step 7: Write the placement test FIRST — before `ClipboardWatcher.cs`.** Same discipline as Task 8 Step 7: the fact pins the intended behaviour, and the compile gate at the end of this step (and again in Step 9) is the only local verification this layer gets. Append to `ClipboardLogicTests` in `/Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipApp.Tests/ClipboardLogicTests.cs`:

```csharp
    [Fact]
    public async Task ApplyRemoteFilesClipRebuildsTreeAndPlacesTopLevelItems()
    {
        var dir = TempDir();
        var (w, clip, changes, _) = Make(dir);
        var payload = new FilesClip(new List<FileEntry>
        {
            new("a.txt", "aaa"u8.ToArray(), "docs/a.txt"),
            new("b.txt", "bbb"u8.ToArray(), "docs/sub/b.txt"),
            new("loose.txt", "lll"u8.ToArray()),
        });
        Assert.True(await w.ApplyRemoteAsync(payload));
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(dir, "docs", "a.txt")));
        Assert.Equal("bbb", File.ReadAllText(Path.Combine(dir, "docs", "sub", "b.txt")));
        Assert.Equal("lll", File.ReadAllText(Path.Combine(dir, "loose.txt")));
        // CF_HDROP: the FOLDER once, plus the loose file — not every leaf.
        Assert.Contains(clip.Written, x => x ==
            $"files:{Path.Combine(dir, "docs")};{Path.Combine(dir, "loose.txt")}");
        // Baseline is the placed items -> re-detect does not echo.
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
    }
```

  **App-test gap (explicit):** this fact runs on Windows CI only, but it is COMPILED here (Steps 7 and 9), so a typo in the `case FilesClip fs:` arm or in this fact's signature is caught locally rather than shipping uncaught. Everything it asserts about *what* gets written and *which* paths are returned is already covered on every platform by `ReceivedTreeTests.WriteRebuildsARealTreeAndReturnsTopLevelAbsolutePaths`; what stays Windows-only is the `IWin32Clipboard.SetFilePaths` (CF_HDROP) call and the fingerprint baselining around it.

  Now build. **Expected: PASS, 0 errors** — same caveat as Task 8 Step 7. `FileEntry` came in with Task 7 and `ReceivedTree` with Step 3 of this task, so nothing this fact references is missing and there is no compile-level red to observe; the fact is red at RUNTIME on Windows CI, where the old arm still writes every entry flat into `received/` and never returns a folder path to `SetFilePaths`. The gate exists to catch a typo in the fact before the implementation lands on top of it. A build error here is a bug in the test.

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet build /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipApp.Tests
```

- [ ] **Step 8: `ClipboardWatcher.cs` — CF_HDROP placement of the rebuilt tree.** Replace the `FilesClip` arm of `ApplyRemoteAsync` (lines 375–398) with:

```csharp
            case FilesClip fs:
                try
                {
                    // Tree rebuild + flat fallback + top-folder uniquify all live
                    // in ReceivedTree (Core, platform-neutral tests); this layer
                    // only puts the result on the clipboard.
                    var placed = ReceivedTree.Write(_receivedDir, fs.Files);
                    // CF_HDROP takes the clip's TOP-LEVEL items in batch order:
                    // each copied folder once (as a folder path) plus every
                    // loose file. Baseline to exactly those paths so the write
                    // does not echo back out.
                    _lastFileFingerprints = FingerprintList(placed);
                    bool filesOk = _clipboard.SetFilePaths(placed);
                    if (!filesOk) RotatingLog.Shared.Warning("clipboard write (files) failed");
                    return Task.FromResult(filesOk);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    RotatingLog.Shared.Warning($"files write to {_receivedDir} failed: {e.Message}");
                    return Task.FromResult(false);
                }
```

- [ ] **Step 9: Build `AnyClipApp.Tests` (expected PASS, 0 errors), then run the Core suite once more (expected PASS).** The build is the only verification the `AnyClipApp` edits get on this machine — without it the `case FilesClip fs:` arm and the new fact would be committed with zero checks of any kind. Do NOT push to get the Windows-CI signal instead.

```bash
export PATH="$HOME/.dotnet:$PATH" && dotnet build /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipApp.Tests \
  && dotnet test /Users/seojeonghwa/project/AnyClip/forwindows/tests/AnyClipCore.Tests
```

- [ ] **Step 10: Commit.**

```bash
cd /Users/seojeonghwa/project/AnyClip && git add forwindows/src forwindows/tests && git commit -m "$(cat <<'EOF'
feat(receiver-c#): rebuild folder trees under received/ and place top-level items

Add ReceivedTree in Core: validate every wire path rule, fall back to
flat placement per offending entry (never drop the frame), sanitize and
NFC-normalize each segment, create intermediate dirs, and uniquify a
colliding top segment ONCE per clip so one copied folder lands in one
new folder. Write() returns the clip's top-level items, which the
WinForms watcher hands to CF_HDROP. The received toast names the folder
for a folder-only clip, derived from the sanitized top segment the clip
actually lands under (computed before the write, so a uniquify bump is
reflected) rather than from the raw wire path. The startup/shutdown
sweep of received/ is now recursive so trees do not accumulate across
restarts.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

### Task 10: README — protocol 1.3 wire essentials and folder-sync semantics

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/README.md` (the "How it works" bullet list, lines 148–156;
  the "알려진 제한" list, lines 298–304)

**Interfaces:**

Consumes (documents only, changes no code): `PROTOCOL_MINOR = 3`, `MAX_FILES_PER_CLIP = 500`,
`FILE_BUDGET = 49_466_572` (Task 1); the pinned toast strings `folder too large to sync: <name>` /
`folder is empty; nothing to sync` and the log line `peer <name> will flatten folders (protocol < 1.3)`
(Task 2); `received/<top>` rebuild + `<top>-2` uniquify (Task 3).

Produces: no symbols — final doc step of the feature.

Steps:

- [ ] **Step 1: Update the version-negotiation bullet.** In `README.md`, replace line 153:
  ```markdown
  - **버전 협상**: handshake에서 양쪽이 protocol_major/minor를 교환해 메이저 불일치 시 link 거부 + 메뉴바 경고. 현재 프로토콜은 1.3이며 minor는 누적 기능 레벨이다(≥1이면 `kind:"files"`, ≥2면 64MiB 프레임 수신, ≥3이면 폴더 트리 복원). minor 3은 능력 표시(capability marker)일 뿐이라 송신 경로에서 아무것도 막지 않는다.
  ```

- [ ] **Step 2: Replace the multi-file bullet and add the folder bullets.** In `README.md`, replace
  line 156 with these three bullets:
  ```markdown
  - **여러 파일 동기화**: 파일을 2개 이상 복사하면 하나의 `kind:"files"` 프레임(프로토콜 1.1)으로 묶어 전송. 합계 예산(~49MB)을 넘거나 500개를 초과하는 파일은 건너뛰고 알림으로 표시. 상대가 프로토콜 1.0(구버전)이면 첫 파일만 전송.
  - **폴더 동기화**: 폴더를 복사하면 하위 파일을 재귀적으로 펼쳐 같은 `kind:"files"` 프레임에 담아 보낸다(프로토콜 1.3). 각 항목에는 폴더 이름부터 시작하는 상대 경로 `path`가 함께 실리고(POSIX `/`·NFC·상대 경로·`..` 불가·최대 32단계), 받는 쪽은 `~/.anyclip/received/<폴더 이름>/…` 아래에 트리를 그대로 복원한 뒤 최상위 항목을 클립보드에 올린다. 같은 이름의 폴더가 이미 있으면 `<이름>-2`로 만들어 한 클립이 한 폴더에 떨어진다. 폴더 하나는 **전부 아니면 전부**라 남은 예산·개수에 통째로 들어가지 않으면 통째로 건너뛰고 `folder too large to sync: <이름>` 알림을 띄운다(부분 트리 없음). `.DS_Store`·`Thumbs.db`·`desktop.ini`와 심볼릭 링크는 제외하고(따라가지 않음), 남는 파일이 없는 폴더는 `folder is empty; nothing to sync`로 끝난다. 같은 선택에 섞인 낱개 파일은 지금처럼 `path` 없이 전송된다.
  - **버전이 섞인 폴더 전송**: `path`는 선택 필드라 구버전 피어도 프레임을 그대로 받는다. 프로토콜 1.1~1.2 피어는 `path`를 무시하고 파일을 평평하게 저장하며, 이때 로그에 클립당 한 번 `peer <name> will flatten folders (protocol < 1.3)`가 남는다. 프로토콜 1.0 피어에게는 폴더에서 나온 항목을 첫 파일 폴백에서 제외하므로, 폴더만 복사한 클립은 그 링크로 아무것도 보내지 않는다(로그만). 트리를 그대로 주고받으려면 양쪽 모두 1.4.0 이상이어야 한다.
  ```

- [ ] **Step 3: Update the known-limitations list.** In `README.md`, insert after line 300
  (`- 다중 피어는 모든 기기가 1.3.0 이상일 때 완전 동작 …`):
  ```markdown
  - 폴더 동기화는 파일만 복원 — 빈 폴더와 심볼릭 링크는 제외, 트리 그대로 받으려면 모든 기기가 1.4.0 이상
  - Python 빌드의 macOS 쪽은 클립보드에 여러 항목을 올릴 수 없어 받은 클립의 첫 최상위 항목만 올라감 (네이티브 Swift/C# 빌드는 전부 올림)
  ```

- [ ] **Step 4: Verify the doc, expect the old claim gone and the new wording present.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && ! grep -q "폴더는 동기화하지 않음" README.md && ! grep -q "100개를 초과" README.md && grep -c "folder too large to sync\|peer <name> will flatten folders\|현재 프로토콜은 1.3" README.md
  ```
  Expected output: `3` (and exit status 0 — neither stale phrase remains).

- [ ] **Step 5: Run the suite one last time, expect PASS (docs-only change, nothing may regress).**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && .venv/bin/python -m pytest tests/ -q
  ```

- [ ] **Step 6: Commit.**
  ```bash
  cd /Users/seojeonghwa/project/AnyClip && git add README.md && git commit -m "$(cat <<'EOF'
docs: document folder sync and protocol 1.3 in the wire essentials

Bumps the stated protocol to 1.3 (cumulative minors: >=1 files, >=2 64 MiB
frames, >=3 folder trees; minor 3 is a capability marker only), raises the
documented per-clip file cap 100 -> 500, and describes folder expansion, the
per-folder all-or-nothing rule with its two toasts, the received/<top> rebuild
with <top>-2 uniquify, and what mixed-version peers do (1.1-1.2 flatten,
1.0 gets nothing for a folder-only clip).

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
  ```
