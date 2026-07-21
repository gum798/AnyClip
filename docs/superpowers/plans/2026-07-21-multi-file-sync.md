# Multi-file Clipboard Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sync multi-file clipboard selections across all three AnyClip implementations via a new wire kind `"files"` (protocol 1.0 → 1.1), and fix the receive-side filename sanitizer so names like `(E&S)_SCM 마스터플랜_20250915_공유6.pptx` survive unchanged.

**Architecture:** A selection of ≥ 2 files ships as ONE `kind:"files"` JSON frame (array of name/content/hash/bytes entries plus an order-independent aggregate hash used for echo suppression). Sending is gated on the peer's hello `protocol_minor` (≥ 1); old peers get today's first-file behavior. The receiver writes all files to `~/.anyclip/received/`, uniquifies name collisions, and places the whole set on the clipboard in one operation. Spec: `docs/superpowers/specs/2026-07-21-multi-file-sync-design.md`.

**Tech Stack:** Python 3.12 asyncio (repo root `anyclip.py`), Swift 6 SPM (`formacOS/`), C# .NET 8 (`forwindows/`), shared golden vectors + `fake_peer.py` interop.

## Global Constraints

Every task's requirements implicitly include these (values verbatim from the spec):

- **Wire kind `"files"`** — top-level key order `type, kind, files, hash, ts, bytes`; entry key order `name, content, hash, bytes`; entry `name` NFC-normalized; `content` base64 ASCII; entry `hash` = sha256 lowercase hex of that file's raw bytes; top-level `hash` = aggregate; top-level `bytes` = sum of raw sizes.
- **Aggregate hash** = sha256 lowercase hex of the ASCII concatenation of the per-file sha256 hex strings sorted ordinally, no separator. Known-answer: `aggregate([sha256("alpha"), sha256("beta")]) == "0cb0309affcf4f994813ec26b8afc7e0b758605a04641de9871e04363de5e6b8"`.
- **Limits:** `MAX_PAYLOAD` = 16 MiB per frame (unchanged); send budget = existing fileBudget = `int((16*1024*1024 - 256*1024) * 0.74)` = 12,221,153 raw bytes applied to the **sum**; `MAX_FILES_PER_CLIP` = 100 (sender-side only; receiver stays lenient).
- **Greedy selection is skip-and-continue**, in selection order: a file that would overflow the budget (or the count cap) is skipped, and later smaller files may still be accepted. Identical loop semantics in all three implementations.
- **Single-file rule:** exactly 1 sendable file → legacy `kind:"file"` (unchanged); ≥ 2 → `"files"` if peer minor ≥ 1, else first sendable file as `"file"` plus a skip notification with the dropped count.
- **protocol_minor 0 → 1** in all three. The golden `hello.bin` fixture intentionally stays `protocol_minor: 0` (historical sample material) — do not regenerate it to 1, and do not change the existing hello golden asserts.
- **Sanitizer** (receive-side, identical semantics in all three): NFC → basename (split on `/` and `\`) → replace `\ / < > : " | ? *`, U+0000–U+001F, U+007F with `_` → trim trailing dots and spaces → empty/`.`/`..` → `received.bin` → Windows reserved device names (`CON PRN AUX NUL COM1-9 LPT1-9`, case-insensitive, stem before the first dot) prefixed with `_`. `(E&S)_SCM 마스터플랜_20250915_공유6.pptx` must survive unchanged.
- **Uniquify** (identical in all three): used-set guard; duplicates get ` (2)`, ` (3)` … before the LAST dot; a leading dot is NOT an extension (`.env` → `.env (2)`); a candidate colliding with an already-emitted name bumps further.
- **Receive is all-or-nothing:** any invalid/non-strict-base64 entry or an empty `files` array drops the whole frame (log only, link stays up). All hashes recomputed from decoded bytes — never trusted from the wire.
- **Golden `clip_files.bin` reference values** (entries `("노트.txt", b"golden multi one \x00\x01")`, `("réport (v2).bin", b"golden multi two \x02\x03")`): per-file hashes `19ec298dbf31b7d37f08d0536d1657c4ed056115a83317d711be4f0a80fced65` and `ce020e6c9b4aac66ff8b8901b7569372cb1672068f7921f822d65c2975facc7a`; aggregate `7d04c7a5d04332ff7e657a1046d2e3c22e808f5fccba7dbf321d9738dcb2979c`; total bytes 38. If any implementation disagrees with these, that implementation is wrong.
- **Ordering:** execute tasks in numeric order. Hard dependencies: Task 5 (golden vectors) must land before Task 6's and Task 9's golden-vector test steps; Task 8 (`fake_peer.py --send-files`) must land before Task 10.
- **Commits** end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. Work on branch `feat/multi-file-sync`.
- ⚠️ **Environment warning:** running the Swift daemon/interop test suites on this Mac can flip the live AnyClip menu-bar app into a sticky false auth-error state. This is a known local artifact, not a test failure — restart AnyClip.app to clear it after test runs.

---

### Task 1: Python wire layer — aggregate hash, protocol minor → 1, kind "files" send + receive

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/anyclip.py`
  - line 71 (`PROTOCOL_MINOR = 0`)
  - insert two module helpers after `sha256_bytes` (ends line 355, before `grab_clipboard_image` at line 358)
  - `send_clip` "file" branch + trailing `else` at lines 1543-1563
  - inbound dispatch "file" branch + trailing `else` at lines 1403-1414
- Test: `/Users/seojeonghwa/project/AnyClip/tests/test_wire_files.py` (create)

**Interfaces:**
- Consumes: existing module funcs `sha256_bytes(data: bytes) -> str` (line 354), `PeerLink.send_clip(self, kind, content)` (line 1515), `PeerLink._send(self, writer, obj)` (line 1436).
- Produces:
  - `aggregate_files_hash(hashes: list) -> str`
  - `decode_files_payload(msg: dict) -> Optional[list]` (returns `[(name, raw_bytes), ...]` or `None` = whole-frame drop)
  - `send_clip(kind="files", content=[(name:str, raw:bytes), ...])` builds the CONTRACT wire object
  - inbound dispatch delivers `on_clip("files", [(name, raw), ...])`
  - `PROTOCOL_MINOR == 1`

- [ ] **Step 1: Write the failing test** — create `tests/test_wire_files.py`:
```python
"""Wire-layer tests for the multi-file clip kind ("files", protocol 1.1):
the aggregate echo-suppression hash, strict inbound decode, and the exact
send_clip wire shape/field order. Pure logic; no sockets."""
from __future__ import annotations

import asyncio
import hashlib

import anyclip


class _OpenWriter:
    """Minimal StreamWriter stand-in: send_clip only calls is_closing()."""
    def is_closing(self) -> bool:
        return False


def _capture_link():
    link = anyclip.PeerLink.__new__(anyclip.PeerLink)
    link._writer = _OpenWriter()
    link._send_timeout = 1.0
    sent = []
    async def fake_send(writer, obj):
        sent.append(obj)
    link._send = fake_send
    return link, sent


def test_protocol_minor_bumped_to_one():
    assert anyclip.PROTOCOL_MINOR == 1


def test_aggregate_is_order_independent_and_known():
    ha = hashlib.sha256(b"alpha").hexdigest()
    hb = hashlib.sha256(b"beta").hexdigest()
    expected = "0cb0309affcf4f994813ec26b8afc7e0b758605a04641de9871e04363de5e6b8"
    assert anyclip.aggregate_files_hash([ha, hb]) == expected
    assert anyclip.aggregate_files_hash([hb, ha]) == expected  # order-independent


def test_decode_files_payload_valid():
    msg = {"type": "clip", "kind": "files", "files": [
        {"name": "a", "content": "YWxwaGE=", "hash": "x", "bytes": 5},
        {"name": "b", "content": "YmV0YQ==", "hash": "y", "bytes": 4},
    ]}
    assert anyclip.decode_files_payload(msg) == [("a", b"alpha"), ("b", b"beta")]


def test_decode_files_payload_bad_base64_drops_whole_frame():
    msg = {"type": "clip", "kind": "files", "files": [
        {"name": "a", "content": "YWxwaGE=", "bytes": 5},
        {"name": "b", "content": "not base64!!", "bytes": 0},
    ]}
    assert anyclip.decode_files_payload(msg) is None


def test_decode_files_payload_empty_or_missing_array_ignored():
    assert anyclip.decode_files_payload({"kind": "files", "files": []}) is None
    assert anyclip.decode_files_payload({"kind": "files"}) is None


def test_send_clip_files_wire_shape_and_field_order():
    async def go():
        link, sent = _capture_link()
        data = [("alpha.bin", b"alpha"), ("beta.bin", b"beta")]
        await link.send_clip("files", data)
        assert len(sent) == 1
        payload = sent[0]
        assert list(payload.keys()) == ["type", "kind", "files", "hash", "ts", "bytes"]
        assert payload["type"] == "clip" and payload["kind"] == "files"
        assert payload["bytes"] == 9  # 5 + 4
        e0 = payload["files"][0]
        assert list(e0.keys()) == ["name", "content", "hash", "bytes"]
        assert e0["name"] == "alpha.bin"
        assert e0["content"] == "YWxwaGE="
        assert e0["hash"] == hashlib.sha256(b"alpha").hexdigest()
        assert e0["bytes"] == 5
        expected_agg = anyclip.aggregate_files_hash([
            hashlib.sha256(b"alpha").hexdigest(),
            hashlib.sha256(b"beta").hexdigest(),
        ])
        assert payload["hash"] == expected_agg
    asyncio.run(go())
```

- [ ] **Step 2: Run test to verify it fails** — `source .venv/bin/activate && pytest tests/test_wire_files.py -v`
  Expected: `test_protocol_minor_bumped_to_one` fails `assert 0 == 1`; the aggregate/decode tests fail with `AttributeError: module 'anyclip' has no attribute 'aggregate_files_hash'` / `decode_files_payload`; `test_send_clip_files_wire_shape_and_field_order` fails `assert 0 == 1` (len(sent), because unknown kind is dropped).

- [ ] **Step 3a: Bump the protocol minor** — Edit `anyclip.py`, line 71:
```python
PROTOCOL_MINOR = 1
```
(replacing `PROTOCOL_MINOR = 0`).

- [ ] **Step 3b: Add the two module helpers** — insert between `sha256_bytes` (line 355) and `grab_clipboard_image` (line 358). Anchor on:
```python
def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def grab_clipboard_image() -> Optional[bytes]:
```
Replace with:
```python
def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def aggregate_files_hash(hashes: list) -> str:
    """Echo-suppression key for a multi-file clip. Order-independent: sort
    the per-file sha256 lowercase-hex strings lexicographically, concatenate
    with NO separator, and sha256 the ASCII bytes of that concatenation.
    Identical formula in Swift (WireProtocol) and C# (WireMessage)."""
    joined = "".join(sorted(hashes))
    return hashlib.sha256(joined.encode("ascii")).hexdigest()


def decode_files_payload(msg: dict) -> Optional[list]:
    """Decode a kind:"files" clip into [(name, raw_bytes), ...].

    Strict: any entry with a missing/non-str/non-strict-base64 ``content``,
    a non-object entry, or an empty/missing ``files`` array returns None so
    the caller drops the WHOLE frame (no partial apply). Names come straight
    off the wire (already NFC per the sender); sanitization happens on write.
    Wire hashes are never trusted -- recomputed downstream from decoded bytes."""
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
        decoded.append((name, raw))
    return decoded


def grab_clipboard_image() -> Optional[bytes]:
```
(`base64`, `hashlib`, `Optional`, and `log` are already imported/defined — lines 10, 12, 29, 96.)

- [ ] **Step 3c: Add the send_clip "files" branch** — Edit `anyclip.py`. Anchor on the end of the "file" branch + the `else` (lines 1558-1563):
```python
                "ts": time.time(),
                "bytes": len(raw_b),
            }
        else:
            log.debug(f"send_clip: unknown kind {kind!r}, dropping")
            return
```
Replace with:
```python
                "ts": time.time(),
                "bytes": len(raw_b),
            }
        elif kind == "files":
            # content is expected to be a list of (name, raw_bytes) tuples.
            if not isinstance(content, list) or not content:
                return
            files_arr = []
            hashes = []
            total = 0
            for ent in content:
                if not isinstance(ent, tuple) or len(ent) != 2:
                    return
                name, raw = ent
                if not isinstance(name, str) or not isinstance(raw, (bytes, bytearray)):
                    return
                raw_b = bytes(raw)
                h = sha256_bytes(raw_b)
                files_arr.append({
                    "name": name,
                    "content": base64.b64encode(raw_b).decode("ascii"),
                    "hash": h,
                    "bytes": len(raw_b),
                })
                hashes.append(h)
                total += len(raw_b)
            payload = {
                "type": "clip",
                "kind": "files",
                "files": files_arr,
                "hash": aggregate_files_hash(hashes),
                "ts": time.time(),
                "bytes": total,
            }
        else:
            log.debug(f"send_clip: unknown kind {kind!r}, dropping")
            return
```
(The existing `await self._send(writer, payload)` at line 1564 sends it; `_send` already enforces the 16 MiB `MAX_PAYLOAD` cap at line 1438.)

- [ ] **Step 3d: Add the inbound dispatch "files" branch** — Edit `anyclip.py`. Anchor on the "file" branch tail + `else` (lines 1412-1414):
```python
                        await self.on_clip("file", (name, raw))
                    else:
                        log.debug(f"ignoring clip with kind={kind!r}")
```
Replace with:
```python
                        await self.on_clip("file", (name, raw))
                    elif kind == "files":
                        decoded = decode_files_payload(msg)
                        if decoded is None:
                            continue  # whole-frame drop already logged
                        await self.on_clip("files", decoded)
                    else:
                        log.debug(f"ignoring clip with kind={kind!r}")
```

- [ ] **Step 4: Run test to verify it passes** — `source .venv/bin/activate && pytest tests/test_wire_files.py -v`
  Expected: 6 passed. Also run `pytest tests/test_version_negotiator.py -v` — still all pass (negotiator tests build `VersionInfo` explicitly, unaffected by the constant bump).

- [ ] **Step 5: Commit**
```
git add anyclip.py tests/test_wire_files.py
git commit -m "$(cat <<'EOF'
feat(wire): kind "files" send/receive, aggregate hash, protocol minor 1

Add module-level aggregate_files_hash + decode_files_payload, extend
send_clip and the inbound dispatch with the multi-file "files" kind, and
bump PROTOCOL_MINOR 0 -> 1. Strict per-entry base64 => whole-frame drop.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Python filename sanitizer (denylist) + uniquify helper

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/anyclip.py`
  - insert two helpers + two constants between `EchoSuppressor.should_send` (ends line 811) and `class ClipboardWatcher:` (line 814)
  - rewire the inline sanitize inside `update_local_file` at lines 1027-1036
- Test: `/Users/seojeonghwa/project/AnyClip/tests/test_sanitize.py` (create)

**Interfaces:**
- Consumes: `unicodedata` (imported line 24).
- Produces:
  - `sanitize_filename(name: str) -> str` (denylist; CONTRACT rules 1-6)
  - `uniquify_names(names: list) -> list` (` (2)`, ` (3)` before the last extension)

- [ ] **Step 1: Write the failing test** — create `tests/test_sanitize.py`:
```python
"""Filename sanitizer (denylist, not whitelist) + within-batch uniquify.
Keep in lockstep with Swift sanitizeFilename and C# SanitizeFilename."""
from __future__ import annotations

import unicodedata

import pytest

from anyclip import sanitize_filename, uniquify_names


def test_korean_business_name_survives_unchanged():
    name = "(E&S)_SCM 마스터플랜_20250915_공유6.pptx"
    assert sanitize_filename(name) == name


@pytest.mark.parametrize("raw,expected", [
    ("../x", "x"),
    ("..", "received.bin"),
    (".", "received.bin"),
    ("", "received.bin"),
    ("a/b\\c.txt", "c.txt"),
    ('a<b>c:"d|e?f*g.txt', "a_b_c__d_e_f_g.txt"),
    ("trail...  ", "trail"),
    ("con", "_con"),
    ("COM1.txt", "_COM1.txt"),
    ("LPT9", "_LPT9"),
    ("nul.txt", "_nul.txt"),
    ("note\x01\x7f.txt", "note__.txt"),
    ("bad:name?.txt", "bad_name_.txt"),
])
def test_sanitize_cases(raw, expected):
    assert sanitize_filename(raw) == expected


def test_nfd_korean_normalized_to_nfc():
    nfc = "받은파일.txt"
    nfd = unicodedata.normalize("NFD", nfc)
    assert nfd != nfc
    assert sanitize_filename(nfd) == nfc


def test_uniquify_with_extension():
    assert uniquify_names(["a.txt", "a.txt", "a.txt"]) == \
        ["a.txt", "a (2).txt", "a (3).txt"]


def test_uniquify_without_extension():
    assert uniquify_names(["b", "b"]) == ["b", "b (2)"]


def test_uniquify_multi_dot_before_last_extension():
    assert uniquify_names(["archive.tar.gz", "archive.tar.gz"]) == \
        ["archive.tar.gz", "archive.tar (2).gz"]


def test_uniquify_no_collision_untouched():
    assert uniquify_names(["a.txt", "b.txt"]) == ["a.txt", "b.txt"]


def test_uniquify_dotfile_treated_as_no_extension():
    assert uniquify_names([".env", ".env"]) == [".env", ".env (2)"]


def test_uniquify_guards_against_existing_names():
    assert uniquify_names(["a (2).txt", "a.txt", "a.txt"]) == \
        ["a (2).txt", "a.txt", "a (3).txt"]
```

- [ ] **Step 2: Run test to verify it fails** — `source .venv/bin/activate && pytest tests/test_sanitize.py -v`
  Expected: `ImportError: cannot import name 'sanitize_filename' from 'anyclip'`.

- [ ] **Step 3a: Add the helpers** — Edit `anyclip.py`. Anchor on:
```python
    def should_send(self, kind: str, payload_hash: str) -> bool:
        return self._last.get(kind) != payload_hash


class ClipboardWatcher:
```
Replace with:
```python
    def should_send(self, kind: str, payload_hash: str) -> bool:
        return self._last.get(kind) != payload_hash


_WIN_RESERVED = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
}
_DENYLIST_CHARS = set('\\/<>:"|?*')


def sanitize_filename(name: str) -> str:
    """Cross-platform-safe basename for a received file. Denylist (not the
    old alnum whitelist), so (), &, spaces, and non-ASCII letters survive.
    Keep in lockstep with Swift sanitizeFilename and C# SanitizeFilename.

    1. NFC normalize.
    2. Basename: split on BOTH '/' and '\\', keep the last component.
    3. Replace denylisted chars, control chars (< U+0020), and U+007F with '_'.
    4. Trim TRAILING dots and spaces.
    5. Empty / '.' / '..'  -> 'received.bin'.
    6. Windows reserved device name (stem before the first dot) -> prefix '_'.
    """
    name = unicodedata.normalize("NFC", name)
    name = name.replace("\\", "/").rsplit("/", 1)[-1]
    name = "".join(
        "_" if (ch in _DENYLIST_CHARS or ord(ch) < 0x20 or ord(ch) == 0x7F) else ch
        for ch in name
    )
    name = name.rstrip(". ")
    if name in ("", ".", ".."):
        return "received.bin"
    if name.split(".", 1)[0].upper() in _WIN_RESERVED:
        name = "_" + name
    return name


def uniquify_names(names: list) -> list:
    """Disambiguate duplicate names within one received batch (after
    sanitization). First occurrence keeps its name; later duplicates get
    ' (2)', ' (3)', ... inserted before the LAST extension (a leading dot
    is not an extension: '.env' -> '.env (2)'). A candidate that collides
    with an already-emitted name is bumped further. Keep in lockstep with
    Swift uniquifyNames and C# TextHelpers.UniquifyNames."""
    used = set()
    result = []
    for name in names:
        if name not in used:
            used.add(name)
            result.append(name)
            continue
        dot = name.rfind(".")
        stem, ext = (name, "") if dot <= 0 else (name[:dot], name[dot:])
        n = 2
        candidate = f"{stem} ({n}){ext}"
        while candidate in used:
            n += 1
            candidate = f"{stem} ({n}){ext}"
        used.add(candidate)
        result.append(candidate)
    return result


class ClipboardWatcher:
```

- [ ] **Step 3b: Rewire `update_local_file`** — Edit `anyclip.py`. Anchor on lines 1027-1036:
```python
        # Sanitize the name -- accept basename only and strip anything
        # that would land outside the target directory. Normalize to NFC
        # first: a macOS peer sends NFD (decomposed Hangul = conjoining jamo
        # U+11xx Windows can't render). Keep in lockstep with Swift
        # sanitizeFilename and C# TextHelpers.SanitizeFilename.
        safe = os.path.basename(unicodedata.normalize("NFC", name)).strip() or "received.bin"
        # Drop characters that are awkward on either OS.
        safe = "".join(
            c if c.isalnum() or c in "._- " else "_" for c in safe
        )
```
Replace with:
```python
        # Cross-platform-safe basename via the shared denylist sanitizer
        # (NFC + traversal strip + reserved-name guard). Keep in lockstep
        # with Swift sanitizeFilename and C# TextHelpers.SanitizeFilename.
        safe = sanitize_filename(name)
```

- [ ] **Step 4: Run tests to verify they pass** — `source .venv/bin/activate && pytest tests/test_sanitize.py tests/test_clipboard_watcher.py -v`
  Expected: all pass. In particular `test_received_filename_normalized_to_nfc` (existing, `tests/test_clipboard_watcher.py:130`) still passes — it round-trips an NFD Korean name through `update_local_file`, which now routes through `sanitize_filename`.

- [ ] **Step 5: Commit**
```
git add anyclip.py tests/test_sanitize.py
git commit -m "$(cat <<'EOF'
fix(receive): denylist filename sanitizer + within-batch uniquify

Replace the alnum whitelist (which mangled (E&S)_...pptx) with a
cross-platform denylist that keeps punctuation and non-ASCII letters, plus
a uniquify_names helper for duplicate names in a received batch.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Python watcher multi-file detection (send side)

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/anyclip.py`
  - add module constants after `MAX_PAYLOAD` (line 75)
  - rewrite the file-baseline seed in `ClipboardWatcher.__init__` (lines 846-858)
  - rewrite `_check_file_clipboard` entirely (lines 930-987)
  - wrap the `update_local_file` fingerprint write in a list (line 1049)
- Test: `/Users/seojeonghwa/project/AnyClip/tests/test_clipboard_watcher.py` (extend)

**Interfaces:**
- Consumes: `grab_clipboard_files() -> list` (line 388), `ClipboardWatcher.on_change`, `ClipboardWatcher._notify_file_skipped` (line 989), `sha256_bytes` (line 354), `unicodedata`, `stat_mod` (imported line 19).
- Produces:
  - module constants `FILE_BUDGET: int` (≈12,221,153), `MAX_FILES_PER_CLIP: int` (100)
  - `ClipboardWatcher._last_file_fp` is now `Optional[list]` of ordered `(path, size, mtime_ns)` tuples
  - watcher emits `on_change("files", [(name, raw), ...])` for ≥ 2 accepted files, else `on_change("file", (name, raw))`

- [ ] **Step 1: Write the failing tests** — append to `tests/test_clipboard_watcher.py`:
```python
@pytest.mark.asyncio
async def test_multiple_files_emitted_as_files_kind(monkeypatch, tmp_path):
    changes = []

    async def on_change(kind, data):
        changes.append((kind, data))

    watcher = _make_watcher(on_change)
    f1 = tmp_path / "a.txt"; f1.write_bytes(b"one")
    f2 = tmp_path / "b.txt"; f2.write_bytes(b"two")
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(f1), str(f2)])

    await watcher._check_file_clipboard()
    assert changes == [("files", [("a.txt", b"one"), ("b.txt", b"two")])]

    # Same selection again -> fingerprint list matches -> no second emission.
    await watcher._check_file_clipboard()
    assert len(changes) == 1


@pytest.mark.asyncio
async def test_folder_mixed_with_files(monkeypatch, tmp_path):
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    folder = tmp_path / "docs"; folder.mkdir()
    f1 = tmp_path / "a.txt"; f1.write_bytes(b"one")
    f2 = tmp_path / "b.txt"; f2.write_bytes(b"two")
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(folder), str(f1), str(f2)])

    await watcher._check_file_clipboard()
    assert changes == [("files", [("a.txt", b"one"), ("b.txt", b"two")])]
    assert any("docs" in m for m in skipped)


@pytest.mark.asyncio
async def test_budget_greedy_partial_send(monkeypatch, tmp_path):
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    monkeypatch.setattr(anyclip, "FILE_BUDGET", 10)
    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    f1 = tmp_path / "a.txt"; f1.write_bytes(b"123456")   # 6, accepted (total 6)
    f2 = tmp_path / "b.txt"; f2.write_bytes(b"789012")   # 6, 6+6=12>10 skip
    f3 = tmp_path / "c.txt"; f3.write_bytes(b"XY")       # 2, 6+2=8<=10 accepted
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(f1), str(f2), str(f3)])

    await watcher._check_file_clipboard()
    assert changes == [("files", [("a.txt", b"123456"), ("c.txt", b"XY")])]
    assert any("skipped" in m for m in skipped)


@pytest.mark.asyncio
async def test_single_survivor_falls_back_to_file_kind(monkeypatch, tmp_path):
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    monkeypatch.setattr(anyclip, "FILE_BUDGET", 10)
    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    f1 = tmp_path / "a.txt"; f1.write_bytes(b"12345678")  # 8, accepted
    f2 = tmp_path / "b.txt"; f2.write_bytes(b"ABCDEFGH")  # 8, 8+8=16>10 skip
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(f1), str(f2)])

    await watcher._check_file_clipboard()
    assert changes == [("file", ("a.txt", b"12345678"))]
    assert any("skipped" in m for m in skipped)
```

- [ ] **Step 2: Run tests to verify they fail** — `source .venv/bin/activate && pytest tests/test_clipboard_watcher.py -v -k "files or folder_mixed or budget or single_survivor"`
  Expected: `test_multiple_files_emitted_as_files_kind` fails (old code emits `("file", ("a.txt", b"one"))` — only the first file); the budget tests fail with `AttributeError: <module 'anyclip'> does not have the attribute 'FILE_BUDGET'`.

- [ ] **Step 3a: Add module constants** — Edit `anyclip.py`. Anchor on line 75:
```python
MAX_PAYLOAD = 16 * 1024 * 1024  # 16 MiB hard cap per frame (enough for typical PNGs)
```
Replace with:
```python
MAX_PAYLOAD = 16 * 1024 * 1024  # 16 MiB hard cap per frame (enough for typical PNGs)
# Greedy multi-file send budget, applied to the SUM of raw file sizes in one
# "files" clip (reserves ~256 KB for the JSON envelope + base64 1.34x). Same
# value the single-file path used inline. Keep in lockstep with Swift/C#.
FILE_BUDGET = int((MAX_PAYLOAD - 256 * 1024) * 0.74)  # ~12,221,153
# Sender-side cap on files per clip; the receiver stays lenient.
MAX_FILES_PER_CLIP = 100
```

- [ ] **Step 3b: Rewrite the file baseline seed** — Edit `anyclip.py`. Anchor on lines 846-858:
```python
        # File path on the clipboard: cache (path, size, mtime) so we do
        # not re-read megabytes of bytes off disk every poll cycle.
        self._last_file_fp: Optional[tuple] = None
        self._last_file_hash: Optional[str] = None
        self._oversize_file_warned = False
        # Seed file baseline as well.
        for _path in grab_clipboard_files() or []:
            try:
                stat = os.stat(_path)
            except OSError:
                continue
            self._last_file_fp = (_path, stat.st_size, stat.st_mtime_ns)
            break
```
Replace with:
```python
        # Files on the clipboard: cache an ORDERED list of (path, size,
        # mtime_ns) fingerprints for the whole selection so we neither
        # re-read bytes every poll nor re-detect an unchanged selection.
        self._last_file_fp: Optional[list] = None
        self._last_file_hash: Optional[str] = None
        # Seed the baseline from whatever is already on the clipboard so we
        # do not fire a spurious initial send at startup.
        seed = []
        for _path in grab_clipboard_files() or []:
            try:
                stat = os.stat(_path)
            except OSError:
                continue
            seed.append((_path, stat.st_size, stat.st_mtime_ns))
        self._last_file_fp = seed or None
```

- [ ] **Step 3c: Rewrite `_check_file_clipboard`** — Edit `anyclip.py`. Anchor on the full current method (lines 930-987):
```python
    async def _check_file_clipboard(self) -> None:
        files = await asyncio.to_thread(grab_clipboard_files)
        if not files:
            return
        # Only the first file is synced -- multi-file is intentional scope-out.
        path = files[0]
        try:
            stat = await asyncio.to_thread(os.stat, path)
        except OSError:
            return
        fp = (path, stat.st_size, stat.st_mtime_ns)
        if fp == self._last_file_fp:
            return  # nothing new on the clipboard
        # Folders are an explicit scope-out (same as multi-file). Record
        # the fingerprint FIRST so the same copy is never re-detected --
        # the old EISDIR path skipped that update and retried (and
        # logged) every poll cycle, forever.
        if stat_mod.S_ISDIR(stat.st_mode):
            self._last_file_fp = fp
            display = os.path.basename(path.rstrip("/\\")) or path
            log.warning(f"folder on clipboard not synced (unsupported): {path!r}")
            await self._notify_file_skipped(
                f"folder not synced — folders are not supported: {display}"
            )
            return
        # Refuse files that would not fit in a single frame after base64.
        # Reserve ~256 KB for JSON envelope and the b64 1.34x inflation.
        budget = int((MAX_PAYLOAD - 256 * 1024) * 0.74)
        if stat.st_size > budget:
            if not self._oversize_file_warned:
                log.warning(
                    f"file {path!r} too large to sync "
                    f"({stat.st_size} bytes > limit {budget}); skipping"
                )
                self._oversize_file_warned = True
            # Still update the fingerprint so we do not keep re-warning.
            self._last_file_fp = fp
            return
        self._oversize_file_warned = False
        try:
            data = await asyncio.to_thread(Path(path).read_bytes)
        except OSError as exc:
            # Record the fingerprint anyway: a path that cannot be read
            # now will not become readable by polling it forever.
            self._last_file_fp = fp
            log.warning(f"file read failed for {path!r}: {exc}; skipping")
            return
        self._last_file_fp = fp
        self._last_file_hash = sha256_bytes(data)
        # NFC on the wire: macOS reads filenames in NFD (decomposed Hangul =
        # conjoining jamo U+11xx a Windows peer can't render). Normalize so
        # every receiver gets a composed, renderable name. Keep in lockstep
        # with Swift WireMessage.clipFile and C# WireMessage.ClipFile.
        name = unicodedata.normalize("NFC", os.path.basename(path))
        try:
            await self.on_change("file", (name, data))
        except Exception as exc:
            log.exception(f"on_change(file) handler failed: {exc}")
```
Replace with:
```python
    async def _check_file_clipboard(self) -> None:
        paths = await asyncio.to_thread(grab_clipboard_files)
        if not paths:
            return
        # Build the ORDERED fingerprint for the WHOLE selection. A path that
        # vanished between grab and stat drops out of both fingerprint and
        # candidate list.
        fp = []
        statted = []  # (path, os.stat_result) in selection order
        for path in paths:
            try:
                stat = await asyncio.to_thread(os.stat, path)
            except OSError:
                continue
            fp.append((path, stat.st_size, stat.st_mtime_ns))
            statted.append((path, stat))
        if not fp:
            return
        if fp == self._last_file_fp:
            return  # unchanged selection
        # Record the fingerprint FIRST so a selection we cannot fully sync is
        # never re-detected and retried every poll cycle (folder-skip design).
        self._last_file_fp = fp

        # Filter folders (each named once), then greedily accept files in
        # selection order while sum(raw) <= FILE_BUDGET and count <= cap.
        skipped_folders = []
        accepted = []  # (name, raw_bytes)
        skipped_count = 0
        total = 0
        for path, stat in statted:
            if stat_mod.S_ISDIR(stat.st_mode):
                skipped_folders.append(os.path.basename(path.rstrip("/\\")) or path)
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
            accepted.append((name, data))

        if skipped_folders:
            if len(skipped_folders) == 1:
                await self._notify_file_skipped(
                    "folder not synced — folders are not supported: "
                    f"{skipped_folders[0]}"
                )
            else:
                await self._notify_file_skipped(
                    f"{len(skipped_folders)} folders not synced — "
                    "folders are not supported"
                )
        if skipped_count:
            await self._notify_file_skipped(
                f"{skipped_count} file(s) skipped (too large to sync)"
            )

        if not accepted:
            return
        if len(accepted) == 1:
            self._last_file_hash = sha256_bytes(accepted[0][1])
            try:
                await self.on_change("file", accepted[0])
            except Exception as exc:
                log.exception(f"on_change(file) handler failed: {exc}")
        else:
            try:
                await self.on_change("files", accepted)
            except Exception as exc:
                log.exception(f"on_change(files) handler failed: {exc}")
```

- [ ] **Step 3d: Keep `update_local_file` fingerprint list-shaped** — Edit `anyclip.py`. Anchor on lines 1047-1051:
```python
        try:
            stat = target.stat()
            self._last_file_fp = (str(target), stat.st_size, stat.st_mtime_ns)
        except OSError:
            self._last_file_fp = None
```
Replace with:
```python
        try:
            stat = target.stat()
            self._last_file_fp = [(str(target), stat.st_size, stat.st_mtime_ns)]
        except OSError:
            self._last_file_fp = None
```

- [ ] **Step 4: Run tests to verify they pass** — `source .venv/bin/activate && pytest tests/test_clipboard_watcher.py -v`
  Expected: all pass, including the four pre-existing tests (`test_directory_skipped_with_single_notice` still asserts `"TODO" in skipped[0]`; `test_regular_file_still_synced`, `test_decomposed_filename_sent_as_nfc`, `test_unreadable_file_does_not_loop` unchanged behavior for single-path selections).

- [ ] **Step 5: Commit**
```
git add anyclip.py tests/test_clipboard_watcher.py
git commit -m "$(cat <<'EOF'
feat(watcher): detect multi-file selections and emit kind "files"

Fingerprint the whole ordered selection, filter folders (named once),
greedily fill FILE_BUDGET / MAX_FILES_PER_CLIP in selection order, and emit
on_change("files", [...]) for >=2 accepted files (single survivor still
"file"). Skips notify + always record the fingerprint so nothing loops.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Python receive/apply + peer-minor gating (old-peer fallback)

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/anyclip.py`
  - add `set_clipboard_files` after `set_clipboard_file` (ends line 459)
  - add `ClipboardWatcher.update_local_files` after `update_local_file` (ends line 1055)
  - retain peer minor in `PeerLink`: `__init__` (after line 1113), `_session` locked block (after line 1365), teardown reset (lines 1428-1431), property
  - add module-level `emit_files_clip` before `run()` (line 1958)
  - add "files" branch to `on_remote_clip` (after line 2015) and `on_local_change` (after line 2063)
- Test: `/Users/seojeonghwa/project/AnyClip/tests/test_receive_files.py` (create)

**Interfaces:**
- Consumes: `sanitize_filename`, `uniquify_names` (Task 2); `aggregate_files_hash`, `sha256_bytes`, `decode_files_payload` (Task 1); `set_clipboard_file` (line 426); `LOG_DIR` (line 98); `EchoSuppressor` (line 795).
- Produces:
  - `set_clipboard_files(paths: list) -> bool` (Windows multi-path; else False)
  - `ClipboardWatcher.update_local_files(files: list) -> int` (returns number of files actually placed on the clipboard; baselines fingerprint to placed paths)
  - `PeerLink.peer_protocol_minor -> Optional[int]`
  - `emit_files_clip(link, suppressor, data) -> tuple` returning `("suppressed", 0)` | `("files", n)` | `("file", dropped)`

- [ ] **Step 1: Write the failing test** — create `tests/test_receive_files.py`:
```python
"""Receive/apply + peer-minor send gating for the "files" kind."""
from __future__ import annotations

import asyncio
import os
import types

import anyclip
from anyclip import (
    ClipboardWatcher,
    EchoSuppressor,
    aggregate_files_hash,
    sha256_bytes,
)


class _FakeLink:
    def __init__(self, minor):
        self.peer_protocol_minor = minor
        self.peer_name = "peer"
        self.sent = []

    async def send_clip(self, kind, content):
        self.sent.append((kind, content))


def test_peer_protocol_minor_initialized_by_init():
    cfg = types.SimpleNamespace(token="tok")
    link = anyclip.PeerLink(cfg, "node-1", None)
    assert link.peer_protocol_minor is None


def test_emit_files_new_peer_sends_files():
    async def go():
        link = _FakeLink(minor=1)
        data = [("a.txt", b"one"), ("b.txt", b"two")]
        assert await anyclip.emit_files_clip(link, EchoSuppressor(), data) == ("files", 2)
        assert link.sent == [("files", data)]
    asyncio.run(go())


def test_emit_files_old_peer_falls_back_to_first_file():
    async def go():
        link = _FakeLink(minor=0)
        data = [("a.txt", b"one"), ("b.txt", b"two"), ("c.txt", b"three")]
        assert await anyclip.emit_files_clip(link, EchoSuppressor(), data) == ("file", 2)
        assert link.sent == [("file", ("a.txt", b"one"))]
    asyncio.run(go())


def test_emit_files_suppresses_echo_but_not_a_different_set():
    async def go():
        link = _FakeLink(minor=1)
        sup = EchoSuppressor()
        data = [("a.txt", b"one"), ("b.txt", b"two")]
        agg = aggregate_files_hash([sha256_bytes(b"one"), sha256_bytes(b"two")])
        sup.mark_received("files", agg)
        assert await anyclip.emit_files_clip(link, sup, data) == ("suppressed", 0)
        assert link.sent == []
        other = [("a.txt", b"one"), ("b.txt", b"CHANGED")]
        decision, _ = await anyclip.emit_files_clip(link, sup, other)
        assert decision == "files"
    asyncio.run(go())


def test_update_local_files_writes_uniquifies_and_baselines(monkeypatch, tmp_path):
    monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
    monkeypatch.setattr(anyclip.sys, "platform", "darwin")  # places FIRST only
    placed = {}

    def fake_set(path):
        placed["path"] = path
        return True

    monkeypatch.setattr(anyclip, "set_clipboard_file", fake_set)
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [])
    monkeypatch.setattr(anyclip, "grab_clipboard_image", lambda: None)
    monkeypatch.setattr(anyclip.pyperclip, "paste", lambda: "")

    async def _noop(kind, data):
        return None

    watcher = ClipboardWatcher(0.01, _noop)
    n = watcher.update_local_files([
        ("dup.txt", b"one"), ("dup.txt", b"two"), ("a<b.txt", b"three"),
    ])
    received = tmp_path / "received"
    assert (received / "dup.txt").read_bytes() == b"one"
    assert (received / "dup (2).txt").read_bytes() == b"two"
    assert (received / "a_b.txt").read_bytes() == b"three"  # denylist sanitized
    assert n == 1  # macOS places the first file only
    assert os.path.basename(placed["path"]) == "dup.txt"
    assert isinstance(watcher._last_file_fp, list) and len(watcher._last_file_fp) == 1
    assert watcher._last_file_fp[0][0].endswith("dup.txt")
```

- [ ] **Step 2: Run test to verify it fails** — `source .venv/bin/activate && pytest tests/test_receive_files.py -v`
  Expected: `test_peer_protocol_minor_initialized_by_init` fails `AttributeError: 'PeerLink' object has no attribute '_peer_protocol_minor'`; `emit_files_clip` tests fail `AttributeError: module 'anyclip' has no attribute 'emit_files_clip'`; `update_local_files` test fails `AttributeError: 'ClipboardWatcher' object has no attribute 'update_local_files'`.

- [ ] **Step 3a: Add `set_clipboard_files`** — Edit `anyclip.py`. Anchor on the tail of `set_clipboard_file` (lines 456-462):
```python
        except Exception as exc:
            log.warning(f"set_clipboard_file (Windows) failed: {exc}")
            return False
    return False


def set_clipboard_image(png_bytes: bytes) -> bool:
```
Replace with:
```python
        except Exception as exc:
            log.warning(f"set_clipboard_file (Windows) failed: {exc}")
            return False
    return False


def set_clipboard_files(paths: list) -> bool:
    """Place multiple file references on the clipboard in one operation.

    Windows-> PowerShell ``Set-Clipboard -Path`` with N paths.
    macOS  -> not reliably supported for multiple furls (the shipped Mac app
              is the Swift port); the caller places only the first file via
              set_clipboard_file, so this returns False here.
    Other  -> no-op (False).
    """
    if not paths:
        return False
    if sys.platform == "win32":
        try:
            abs_paths = [str(Path(p).resolve()) for p in paths]
            quoted = ",".join("'" + p.replace("'", "''") + "'" for p in abs_paths)
            result = subprocess.run(
                ["powershell", "-NoProfile", "-Command",
                 f"Set-Clipboard -Path {quoted}"],
                capture_output=True, timeout=5,
                creationflags=0x08000000,
            )
            return result.returncode == 0
        except Exception as exc:
            log.warning(f"set_clipboard_files (Windows) failed: {exc}")
            return False
    return False


def set_clipboard_image(png_bytes: bytes) -> bool:
```

- [ ] **Step 3b: Add `ClipboardWatcher.update_local_files`** — Edit `anyclip.py`. Anchor on the end of `update_local_file` (lines 1052-1056):
```python
        ok = set_clipboard_file(str(target))
        if not ok:
            log.warning("clipboard write (file) failed or unsupported on this OS")
        return ok


class AuthGate:
```
Replace with:
```python
        ok = set_clipboard_file(str(target))
        if not ok:
            log.warning("clipboard write (file) failed or unsupported on this OS")
        return ok

    def update_local_files(self, files: list) -> int:
        """Write received files under ~/.anyclip/received/ and place them on
        the clipboard in one operation. ``files`` is [(name, raw_bytes), ...].

        Names are sanitized then de-duplicated within the batch before
        writing. macOS places only the FIRST file (AppleScript furl limit);
        Windows places all. Baselines the fingerprint to the paths actually
        PLACED on the clipboard so the just-written files are not re-detected.
        Returns the number of files placed on the clipboard (0 on failure)."""
        names = uniquify_names([sanitize_filename(n) for n, _ in files])
        target_dir = LOG_DIR / "received"
        written = []  # absolute path strings, in batch order
        try:
            target_dir.mkdir(parents=True, exist_ok=True)
            for safe, (_orig, data) in zip(names, files):
                target = target_dir / safe
                target.write_bytes(bytes(data))
                written.append(str(target))
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
        fp = []
        for p in placed:
            try:
                st = os.stat(p)
            except OSError:
                continue
            fp.append((p, st.st_size, st.st_mtime_ns))
        self._last_file_fp = fp or None
        return len(placed)


class AuthGate:
```

- [ ] **Step 3c: Retain peer minor in `PeerLink`** — three edits.

  Edit i — `__init__`, anchor on line 1113:
```python
        self._peer_name: Optional[str] = None  # peer's display name (from hello)
```
Replace with:
```python
        self._peer_name: Optional[str] = None  # peer's display name (from hello)
        self._peer_protocol_minor: Optional[int] = None  # from hello; gates kind:"files"
```

  Edit ii — `_session` locked block, anchor on lines 1364-1366:
```python
            self._writer = writer
            self._peer_node_id = peer_id
            self._peer_name = hello.get("name") or peer_id[:8]
```
Replace with:
```python
            self._writer = writer
            self._peer_node_id = peer_id
            self._peer_protocol_minor = peer_proto_minor_raw
            self._peer_name = hello.get("name") or peer_id[:8]
```
(`peer_proto_minor_raw` is the sanitized int computed at lines 1285-1287.)

  Edit iii — teardown reset, anchor on lines 1428-1431:
```python
                if was_active:
                    self._writer = None
                    self._peer_node_id = None
                    self._peer_name = None
```
Replace with:
```python
                if was_active:
                    self._writer = None
                    self._peer_node_id = None
                    self._peer_protocol_minor = None
                    self._peer_name = None
```

- [ ] **Step 3d: Add the `peer_protocol_minor` property** — Edit `anyclip.py`. Anchor on the existing `peer_name` property (lines 1133-1135):
```python
    @property
    def peer_name(self) -> Optional[str]:
        return self._peer_name
```
Replace with:
```python
    @property
    def peer_name(self) -> Optional[str]:
        return self._peer_name

    @property
    def peer_protocol_minor(self) -> Optional[int]:
        return self._peer_protocol_minor
```

- [ ] **Step 3e: Add module-level `emit_files_clip`** — Edit `anyclip.py`. Anchor on line 1958:
```python
async def run(config: Config) -> None:
    setup_logging(config.verbose)
```
Replace with:
```python
async def emit_files_clip(link, suppressor, data) -> tuple:
    """Decide how to send a local multi-file selection to the peer and do it.
    ``data`` is [(name, raw_bytes), ...] with len >= 2. Returns:
      ("suppressed", 0) -- echo of a just-received set; nothing sent.
      ("files", n)      -- sent all n files as one kind:"files" clip.
      ("file", dropped) -- peer protocol_minor 0; sent the first file as a
                           legacy kind:"file" clip; ``dropped`` others not sent.
    """
    hashes = [sha256_bytes(bytes(raw)) for _name, raw in data]
    aggregate = aggregate_files_hash(hashes)
    if not suppressor.should_send("files", aggregate):
        return ("suppressed", 0)
    minor = link.peer_protocol_minor or 0
    if minor >= 1:
        await link.send_clip("files", data)
        return ("files", len(data))
    first_name, first_raw = data[0]
    await link.send_clip("file", (first_name, bytes(first_raw)))
    return ("file", len(data) - 1)


async def run(config: Config) -> None:
    setup_logging(config.verbose)
```

- [ ] **Step 3f: Add the `on_remote_clip` "files" branch** — Edit `anyclip.py`. Anchor on the file-branch tail + `link = PeerLink` (lines 2011-2017):
```python
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip ← {peer}",
                    message=f"file: {name} ({len(raw_b)//1024} KB)",
                )

    link = PeerLink(config, node_id, on_remote_clip)
```
Replace with:
```python
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip ← {peer}",
                    message=f"file: {name} ({len(raw_b)//1024} KB)",
                )
        elif kind == "files":
            assert isinstance(data, list)
            # data: [(name, raw_bytes), ...] already decoded from the wire.
            hashes = [sha256_bytes(bytes(raw)) for _name, raw in data]
            aggregate = aggregate_files_hash(hashes)
            suppressor.mark_received("files", aggregate)
            placed = await asyncio.to_thread(watcher.update_local_files, data)
            # Python-macOS places only the FIRST file; a re-detection of a
            # lone placed file surfaces as kind:"file", so also seed the
            # single-file suppressor slot with that file's hash.
            if placed == 1:
                suppressor.mark_received("file", hashes[0])
            log.info(
                f"<- received {len(data)} files from {peer!r} "
                f"({placed} placed on clipboard)"
            )
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip ← {peer}",
                    message=f"{len(data)} files",
                )

    link = PeerLink(config, node_id, on_remote_clip)
```

- [ ] **Step 3g: Add the `on_local_change` "files" branch** — Edit `anyclip.py`. Anchor on the file-branch tail + `on_file_skipped` def (lines 2059-2065):
```python
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip → {peer}",
                    message=f"file: {name} ({len(raw_b)//1024} KB)",
                )

    async def on_file_skipped(message: str) -> None:
```
Replace with:
```python
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip → {peer}",
                    message=f"file: {name} ({len(raw_b)//1024} KB)",
                )
        elif kind == "files":
            assert isinstance(data, list)
            decision, count = await emit_files_clip(link, suppressor, data)
            peer = link.peer_name or "peer"
            if decision == "suppressed":
                log.debug("skip echo of just-received files")
                return
            if decision == "files":
                log.info(f"-> sent {count} files to {peer!r}")
                if notify_enabled:
                    await notify_async(
                        title=f"AnyClip → {peer}", message=f"{count} files",
                    )
            else:  # "file" old-peer fallback
                log.info(
                    f"-> sent 1 file to {peer!r} "
                    f"(peer proto minor 0, {count} dropped)"
                )
                if notify_enabled:
                    await notify_async(
                        title=f"AnyClip → {peer}", message="file (1 of many)",
                    )
                await on_file_skipped(
                    f"{count} file(s) not sent — peer needs an update for "
                    "multi-file sync"
                )

    async def on_file_skipped(message: str) -> None:
```

- [ ] **Step 4: Run tests to verify they pass** — `source .venv/bin/activate && pytest tests/test_receive_files.py -v`
  Expected: 5 passed. Then run the whole Python suite: `pytest tests/ -v` — all green.

- [ ] **Step 5: Commit**
```
git add anyclip.py tests/test_receive_files.py
git commit -m "$(cat <<'EOF'
feat(receive+send): apply kind "files" and gate sends on peer minor

update_local_files writes+uniquifies received files and places them
(Windows: all via Set-Clipboard -Path; macOS: first only), baselining the
fingerprint to the placed paths. PeerLink retains the peer's protocol_minor;
emit_files_clip sends kind:"files" to minor>=1 peers and falls back to the
first file + skip notice for minor 0. Suppressor marks recomputed hashes.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Golden vectors — `clip_files.bin` + manifest fields (+ fake_peer `--send-files`)

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/formacOS/Scripts/gen-golden-vectors.py` (add import + `FILES`/`_agg`, a `clip_files.bin` vector, and 4 manifest fields)
- Modify: `/Users/seojeonghwa/project/AnyClip/formacOS/Scripts/fake_peer.py` (add `--send-files`)
- Regenerated (committed): `/Users/seojeonghwa/project/AnyClip/formacOS/Tests/AnyClipCoreTests/Fixtures/clip_files.bin`, `.../manifest.json`
- Test: `/Users/seojeonghwa/project/AnyClip/tests/test_golden_files.py` (create)

**Interfaces:**
- Consumes: `anyclip.aggregate_files_hash` (Task 1); the generator's existing `frame()` + `TS` (`gen-golden-vectors.py:24`, `:21`).
- Produces: `clip_files.bin` fixture; manifest keys `files_names` (list), `files_hashes` (list, wire order), `files_aggregate` (str), `files_total_bytes` (int); `fake_peer.py --send-files`.

- [ ] **Step 1: Write the failing test** — create `tests/test_golden_files.py`:
```python
"""The clip_files.bin golden vector decodes to the manifest's recorded names,
per-file hashes, aggregate, and total bytes -- proving Python's encoding is
the canonical source the Swift/C# suites assert against."""
from __future__ import annotations

import base64
import hashlib
import json
import pathlib

import anyclip

FIX = (pathlib.Path(__file__).resolve().parent.parent
       / "formacOS" / "Tests" / "AnyClipCoreTests" / "Fixtures")


def _decode_frame(path: pathlib.Path) -> dict:
    raw = path.read_bytes()
    n = int.from_bytes(raw[:4], "big")
    assert len(raw) == 4 + n, "length prefix must match body length"
    return json.loads(raw[4:].decode("utf-8"))


def test_clip_files_golden_matches_manifest():
    manifest = json.loads((FIX / "manifest.json").read_text(encoding="utf-8"))
    obj = _decode_frame(FIX / "clip_files.bin")
    assert list(obj.keys()) == ["type", "kind", "files", "hash", "ts", "bytes"]
    assert obj["type"] == "clip" and obj["kind"] == "files"

    names, hashes, total = [], [], 0
    for ent in obj["files"]:
        assert list(ent.keys()) == ["name", "content", "hash", "bytes"]
        data = base64.b64decode(ent["content"], validate=True)
        recomputed = hashlib.sha256(data).hexdigest()
        assert recomputed == ent["hash"]  # wire hash matches recomputed
        assert ent["bytes"] == len(data)
        names.append(ent["name"])
        hashes.append(recomputed)
        total += len(data)

    assert names == manifest["files_names"]
    assert hashes == manifest["files_hashes"]
    assert obj["bytes"] == total == manifest["files_total_bytes"]
    agg = anyclip.aggregate_files_hash(hashes)
    assert agg == obj["hash"] == manifest["files_aggregate"]
```

- [ ] **Step 2: Run test to verify it fails** — `source .venv/bin/activate && pytest tests/test_golden_files.py -v`
  Expected: fails at `_decode_frame(FIX / "clip_files.bin")` with `FileNotFoundError: ... Fixtures/clip_files.bin` (the fixture does not exist yet).

- [ ] **Step 3a: Extend the generator** — Edit `gen-golden-vectors.py`.

  Edit i — add `import unicodedata` after `import pathlib` (line 10):
```python
import json
import pathlib
```
Replace with:
```python
import json
import pathlib
import unicodedata
```

  Edit ii — add the `FILES` constant + `_agg` helper after `TS` (line 21):
```python
TS = 1718000000.5
```
Replace with:
```python
TS = 1718000000.5
# One Korean and one accented-Latin name, binary bodies. Names NFC on wire.
FILES = [
    ("노트.txt", b"golden multi one \x00\x01"),
    ("réport (v2).bin", b"golden multi two \x02\x03"),
]


def _agg(hexes: list) -> str:
    return hashlib.sha256("".join(sorted(hexes)).encode("ascii")).hexdigest()
```

  Edit iii — add the `clip_files.bin` vector. Anchor on the `clip_file.bin` entry + `ping.bin` (lines 47-53):
```python
        "clip_file.bin": {
            "type": "clip", "kind": "file", "name": FILE_NAME,
            "content": base64.b64encode(FILE_BYTES).decode("ascii"),
            "hash": hashlib.sha256(FILE_BYTES).hexdigest(), "ts": TS,
            "bytes": len(FILE_BYTES),
        },
        "ping.bin": {"type": "ping", "ts": TS},
```
Replace with:
```python
        "clip_file.bin": {
            "type": "clip", "kind": "file", "name": FILE_NAME,
            "content": base64.b64encode(FILE_BYTES).decode("ascii"),
            "hash": hashlib.sha256(FILE_BYTES).hexdigest(), "ts": TS,
            "bytes": len(FILE_BYTES),
        },
        "clip_files.bin": {
            "type": "clip", "kind": "files",
            "files": [
                {
                    "name": unicodedata.normalize("NFC", n),
                    "content": base64.b64encode(b).decode("ascii"),
                    "hash": hashlib.sha256(b).hexdigest(),
                    "bytes": len(b),
                }
                for n, b in FILES
            ],
            "hash": _agg([hashlib.sha256(b).hexdigest() for _n, b in FILES]),
            "ts": TS,
            "bytes": sum(len(b) for _n, b in FILES),
        },
        "ping.bin": {"type": "ping", "ts": TS},
```

  Edit iv — add the manifest fields. Anchor on the tail of the `manifest` dict (lines 63-67):
```python
        "file_name": FILE_NAME,
        "file_b64": base64.b64encode(FILE_BYTES).decode("ascii"),
        "file_hash": hashlib.sha256(FILE_BYTES).hexdigest(),
        "ts": TS,
    }
```
Replace with:
```python
        "file_name": FILE_NAME,
        "file_b64": base64.b64encode(FILE_BYTES).decode("ascii"),
        "file_hash": hashlib.sha256(FILE_BYTES).hexdigest(),
        "files_names": [unicodedata.normalize("NFC", n) for n, _ in FILES],
        "files_hashes": [hashlib.sha256(b).hexdigest() for _n, b in FILES],
        "files_aggregate": _agg(
            [hashlib.sha256(b).hexdigest() for _n, b in FILES]),
        "files_total_bytes": sum(len(b) for _n, b in FILES),
        "ts": TS,
    }
```

- [ ] **Step 3b: Regenerate fixtures** — run the generator (this step DOES execute at implementation time):
```
python3 formacOS/Scripts/gen-golden-vectors.py
```
Expected stdout: `wrote 6 fixtures to .../formacOS/Tests/AnyClipCoreTests/Fixtures` (5 existing + the new `clip_files.bin`).

- [ ] **Step 3c: Prove the run is non-breaking (old vectors byte-identical)** — the existing vectors must be untouched:
```
git status --short formacOS/Tests/AnyClipCoreTests/Fixtures/
git diff --stat -- \
  formacOS/Tests/AnyClipCoreTests/Fixtures/clip_text.bin \
  formacOS/Tests/AnyClipCoreTests/Fixtures/clip_image.bin \
  formacOS/Tests/AnyClipCoreTests/Fixtures/clip_file.bin \
  formacOS/Tests/AnyClipCoreTests/Fixtures/hello.bin \
  formacOS/Tests/AnyClipCoreTests/Fixtures/ping.bin
```
Expected: `git status` shows only `clip_files.bin` (untracked, `??`) and `manifest.json` (modified, ` M`); the `git diff --stat` over the five old vectors prints NOTHING (zero changed bytes). If any of the five appears, STOP — the generator edit unintentionally altered canonical output.

- [ ] **Step 3d: Add `--send-files` to the fake peer** — Edit `fake_peer.py` (used by Swift/C# `InteropTests`).

  Edit i — register the flag. Anchor on lines 58-59:
```python
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
```
Replace with:
```python
    ap.add_argument("--out", required=True)
    ap.add_argument("--send-files", action="store_true",
                    help="after handshake, send one kind:'files' clip (2 entries)")
    args = ap.parse_args()
```

  Edit ii — send the two-entry "files" clip after the initial text clip, before the record loop. Anchor on lines 88-95:
```python
    text = "hello-from-python"
    send_frame(conn, {
        "type": "clip", "kind": "text", "content": text,
        "hash": hashlib.sha256(text.encode("utf-8")).hexdigest(),
        "ts": time.time(),
    })

    while True:
```
Replace with:
```python
    text = "hello-from-python"
    send_frame(conn, {
        "type": "clip", "kind": "text", "content": text,
        "hash": hashlib.sha256(text.encode("utf-8")).hexdigest(),
        "ts": time.time(),
    })

    if args.send_files:
        import base64 as _b64
        import unicodedata as _ud
        entries = [
            (_ud.normalize("NFC", "노트.txt"), b"multi body one"),
            (_ud.normalize("NFC", "(E&S) plan.txt"), b"multi body two"),
        ]
        files_field = [
            {
                "name": n,
                "content": _b64.b64encode(b).decode("ascii"),
                "hash": hashlib.sha256(b).hexdigest(),
                "bytes": len(b),
            }
            for n, b in entries
        ]
        agg = hashlib.sha256(
            "".join(sorted(f["hash"] for f in files_field)).encode("ascii")
        ).hexdigest()
        send_frame(conn, {
            "type": "clip", "kind": "files", "files": files_field,
            "hash": agg, "ts": time.time(),
            "bytes": sum(f["bytes"] for f in files_field),
        })

    while True:
```

- [ ] **Step 4: Run the test to verify it passes** — `source .venv/bin/activate && pytest tests/test_golden_files.py -v`
  Expected: 1 passed. Sanity-check the fake peer still parses: `python3 formacOS/Scripts/fake_peer.py --help` exits 0 and lists `--send-files`.

- [ ] **Step 5: Commit**
```
git add formacOS/Scripts/gen-golden-vectors.py formacOS/Scripts/fake_peer.py formacOS/Tests/AnyClipCoreTests/Fixtures/clip_files.bin formacOS/Tests/AnyClipCoreTests/Fixtures/manifest.json tests/test_golden_files.py
git commit -m "$(cat <<'EOF'
test(golden): clip_files.bin vector + manifest fields + fake_peer --send-files

Two-entry files vector (Korean + accented-Latin names, binary bodies) at the
shared fixed ts, with files_names/files_hashes/files_aggregate/
files_total_bytes in the manifest. Old vectors byte-identical after regen.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

### Task 6: Swift core — wire kind "files", aggregate hash, sanitizer denylist, uniquify, minor bump

**Files:**
- Modify: `formacOS/Sources/AnyClipCore/Hashing.swift` (append after line 12)
- Modify: `formacOS/Sources/AnyClipCore/TextHelpers.swift` (replace `sanitizeFilename` at lines 14-38; append `uniquifyNames` + private `insertSuffix`)
- Modify: `formacOS/Sources/AnyClipCore/WireProtocol.swift` (bump `protocolMinor` at line 7; extend `ClipPayload` at lines 31-51; add `WireFileEntry` struct before `WireMessage`; add `files` field at line 67; add `clipFiles` after line 122; add `.files` case to `clip(_:ts:)` at lines 124-130; add `decodeFileEntries` after line 193)
- Test: `formacOS/Tests/AnyClipCoreTests/HashingTests.swift`, `.../TextHelpersTests.swift`, `.../WireProtocolTests.swift`, `.../GoldenVectorTests.swift`

**Interfaces:**
- Consumes (from Task 5, must already be committed): fixture `formacOS/Tests/AnyClipCoreTests/Fixtures/clip_files.bin` (a `kind:"files"` frame with two entries `("노트.txt", b"golden multi one \x00\x01")` and `("réport (v2).bin", b"golden multi two \x02\x03")`), and `manifest.json` extended with keys `files_names` (`[String]`), `files_hashes` (`[String]`, per-entry, wire order), `files_aggregate` (`String`), `files_total_bytes` (`Int`).
- Produces:
  - `public func aggregateFilesHash(_ hashes: [String]) -> String`
  - `public func sanitizeFilename(_ name: String) -> String` (new denylist semantics)
  - `public func uniquifyNames(_ names: [String]) -> [String]`
  - `public struct WireFileEntry: Codable, Sendable, Equatable { public var name: String; public var content: String; public var hash: String; public var bytes: Int }`
  - `WireMessage.files: [WireFileEntry]?`
  - `public static func clipFiles(files: [(name: String, data: Data)], ts: Double) -> WireMessage`
  - `ClipPayload.files([(name: String, data: Data)])` (with `kind == "files"`, `payloadHash == aggregate`)
  - `public func decodeFileEntries(_ files: [WireFileEntry]?) -> [(name: String, data: Data)]?`
  - `Wire.protocolMinor == 1`

- [ ] **Step 1: Write the failing aggregate-hash test** — append to `formacOS/Tests/AnyClipCoreTests/HashingTests.swift`:
```swift
@Test func aggregateFilesHashMatchesFormulaAndIsOrderIndependent() {
    // Aggregate = sha256 of the per-file hex hashes sorted lexicographically
    // and concatenated with no separator. Hex is ASCII, so "a…" sorts before
    // "b…" — assert against the formula itself (no magic constant).
    let h1 = String(repeating: "a", count: 64)
    let h2 = String(repeating: "b", count: 64)
    let expected = sha256Hex(h1 + h2)
    #expect(aggregateFilesHash([h1, h2]) == expected)
    #expect(aggregateFilesHash([h2, h1]) == expected) // input order must not matter
    #expect(aggregateFilesHash(["ff", "00"]) == sha256Hex("00" + "ff"))
}
```

- [ ] **Step 2: Run test to verify it fails** — `swift test --package-path formacOS --filter aggregateFilesHashMatchesFormulaAndIsOrderIndependent`. Expected: compile error `cannot find 'aggregateFilesHash' in scope`.

- [ ] **Step 3: Implement `aggregateFilesHash`** — append to `formacOS/Sources/AnyClipCore/Hashing.swift` after line 12:
```swift

/// Echo-suppression key for a multi-file clip. Sort the per-file sha256
/// lowercase-hex strings lexicographically (hex is ASCII, so Swift's default
/// String `<` gives the required plain ordinal order), concatenate with no
/// separator, and sha256 the ASCII bytes. Order-independent so pasteboard
/// re-detection order can never break suppression. Keep in lockstep with
/// anyclip.aggregate_files_hash and C# Hashing.AggregateFilesHash.
public func aggregateFilesHash(_ hashes: [String]) -> String {
    sha256Hex(hashes.sorted().joined())
}
```

- [ ] **Step 4: Run test to verify it passes** — `swift test --package-path formacOS --filter aggregateFilesHashMatchesFormulaAndIsOrderIndependent`. Expected: `Test aggregateFilesHashMatchesFormulaAndIsOrderIndependent ... passed`.

- [ ] **Step 5: Commit** —
```
git add formacOS/Sources/AnyClipCore/Hashing.swift formacOS/Tests/AnyClipCoreTests/HashingTests.swift
git commit -m "$(cat <<'EOF'
feat(core-swift): aggregate-files hash for multi-file echo suppression

Sorted-hex-concat sha256, order-independent, in lockstep with the Python
canonical formula.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 6: Write the failing sanitizer/uniquify tests** — in `formacOS/Tests/AnyClipCoreTests/TextHelpersTests.swift`, REPLACE the existing `sanitizeKeepsSafeChars` body (its old whitelist expectation `we!rd:na?me → we_rd_na_me` is obsolete under the denylist) and append the new cases. Replace lines 11-19:
```swift
@Test func sanitizeKeepsSafeChars() {
    #expect(sanitizeFilename("report v2.txt") == "report v2.txt")
    #expect(sanitizeFilename("a/b/c.txt") == "c.txt")          // basename only
    // Denylist (not whitelist): "!" and "&" are kept; ":" and "?" become "_".
    #expect(sanitizeFilename("we!rd:na?me") == "we!rd_na_me")
    #expect(sanitizeFilename("") == "received.bin")
    #expect(sanitizeFilename("   ") == "received.bin")
    #expect(sanitizeFilename("한글파일.txt") == "한글파일.txt")
    #expect(sanitizeFilename("???") == "___")
}

@Test func sanitizeKeepsParensAmpersandSpacesForRealFilename() {
    // The reported regression: the alnum whitelist mangled "(", "&", ")" to "_".
    let name = "(E&S)_SCM 마스터플랜_20250915_공유6.pptx"
    #expect(sanitizeFilename(name) == name)                    // survives UNCHANGED
}

@Test func sanitizeSplitsOnBothSlashKinds() {
    #expect(sanitizeFilename("a\\b\\c.txt") == "c.txt")        // Windows backslash path
    #expect(sanitizeFilename("../x") == "x")                   // traversal -> last component
    #expect(sanitizeFilename("..") == "received.bin")          // dotdot -> received.bin
    #expect(sanitizeFilename(".") == "received.bin")
}

@Test func sanitizeTrimsTrailingDotsAndSpaces() {
    #expect(sanitizeFilename("name.  ") == "name")
    #expect(sanitizeFilename("name...") == "name")
    #expect(sanitizeFilename("keep.mid.dots.txt") == "keep.mid.dots.txt")
}

@Test func sanitizePrefixesWindowsReservedDeviceNames() {
    #expect(sanitizeFilename("CON") == "_CON")
    #expect(sanitizeFilename("con.txt") == "_con.txt")         // case-insensitive, stem-before-first-dot
    #expect(sanitizeFilename("COM1") == "_COM1")
    #expect(sanitizeFilename("LPT9.log") == "_LPT9.log")
    #expect(sanitizeFilename("COM10") == "COM10")              // not a reserved device
    #expect(sanitizeFilename("console.txt") == "console.txt")  // only exact stem matches
}

@Test func uniquifyInsertsSuffixBeforeLastExtension() {
    #expect(uniquifyNames(["a.txt", "a.txt", "a.txt"]) == ["a.txt", "a (2).txt", "a (3).txt"])
    #expect(uniquifyNames(["x", "x"]) == ["x", "x (2)"])       // no extension
    #expect(uniquifyNames(["a.tar.gz", "a.tar.gz"]) == ["a.tar.gz", "a.tar (2).gz"]) // last ext only
    #expect(uniquifyNames(["a.txt", "b.txt"]) == ["a.txt", "b.txt"]) // no collision -> untouched
    #expect(uniquifyNames([".env", ".env"]) == [".env", ".env (2)"]) // leading dot != extension
    #expect(uniquifyNames(["a (2).txt", "a.txt", "a.txt"]) == ["a (2).txt", "a.txt", "a (3).txt"]) // guard vs existing
}
```
(Leave the existing `sanitizeNormalizesDecomposedUnicodeToNFC` test as-is; the new sanitizer still NFC-normalizes.)

- [ ] **Step 7: Run tests to verify they fail** — `swift test --package-path formacOS --filter sanitize`. Expected: assertion failures such as `sanitizeFilename("(E&S)…") == name` failing (old whitelist returns `_E_S_…`), and `cannot find 'uniquifyNames' in scope`.

- [ ] **Step 8: Rewrite the sanitizer and add uniquify** — in `formacOS/Sources/AnyClipCore/TextHelpers.swift`, REPLACE the whole `sanitizeFilename` function (lines 14-38) with:
```swift
/// Sanitize an inbound file name into a cross-platform-safe basename.
/// Denylist (not whitelist) so legitimate punctuation like "(", "&", ")" and
/// spaces survive. Identical semantics in Python (anyclip.sanitize_filename)
/// and C# (TextHelpers.SanitizeFilename):
///   1. NFC normalize.
///   2. Basename: split on both "/" and "\", keep the last component.
///   3. Replace \ / < > : " | ? *, controls < U+0020, and U+007F with "_".
///   4. Trim trailing dots and spaces.
///   5. Empty / "." / ".." -> "received.bin".
///   6. Windows reserved device names (CON PRN AUX NUL COM1-9 LPT1-9,
///      case-insensitive, matched on the stem before the FIRST dot) -> "_"-prefixed.
public func sanitizeFilename(_ name: String) -> String {
    let nfc = name.precomposedStringWithCanonicalMapping
    let base = nfc.split(whereSeparator: { $0 == "/" || $0 == "\\" })
        .last.map(String.init) ?? ""
    let deny: Set<Character> = ["\\", "/", "<", ">", ":", "\"", "|", "?", "*"]
    var out = ""
    for scalar in base.unicodeScalars {
        if scalar.value < 0x20 || scalar.value == 0x7F || deny.contains(Character(scalar)) {
            out.append("_")
        } else {
            out.append(Character(scalar))
        }
    }
    while let last = out.last, last == "." || last == " " { out.removeLast() }
    if out.isEmpty || out == "." || out == ".." { return "received.bin" }
    let stem = out.split(separator: ".", maxSplits: 1,
                         omittingEmptySubsequences: false).first.map(String.init) ?? out
    let upper = stem.uppercased()
    let isCom = upper.count == 4 && upper.hasPrefix("COM") && ("1"..."9").contains(upper.last!)
    let isLpt = upper.count == 4 && upper.hasPrefix("LPT") && ("1"..."9").contains(upper.last!)
    if ["CON", "PRN", "AUX", "NUL"].contains(upper) || isCom || isLpt {
        out = "_" + out
    }
    return out
}

/// De-duplicate names WITHIN one received batch, after sanitization: the first
/// occurrence keeps its name, later duplicates get " (2)", " (3)" … inserted
/// before the LAST extension (a leading dot is not an extension:
/// ".env" -> ".env (2)"). A candidate that collides with an already-emitted
/// name is bumped further. Keep in lockstep with the Python/C# receivers.
public func uniquifyNames(_ names: [String]) -> [String] {
    var used = Set<String>()
    var out: [String] = []
    for name in names {
        if !used.contains(name) {
            used.insert(name)
            out.append(name)
            continue
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
        out.append(candidate)
    }
    return out
}
```
- [ ] **Step 9: Run tests to verify they pass** — `swift test --package-path formacOS --filter sanitize` then `swift test --package-path formacOS --filter uniquify`. Expected: all sanitize/uniquify tests `passed`, including `sanitizeKeepsParensAmpersandSpacesForRealFilename`.

- [ ] **Step 10: Commit** —
```
git add formacOS/Sources/AnyClipCore/TextHelpers.swift formacOS/Tests/AnyClipCoreTests/TextHelpersTests.swift
git commit -m "$(cat <<'EOF'
feat(core-swift): denylist filename sanitizer + batch uniquify

Whitelist -> cross-platform denylist so "(E&S)…pptx" survives unchanged;
adds reserved-device-name prefixing and " (n)" collision suffixing.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 11: Write the failing wire-files tests** — append to `formacOS/Tests/AnyClipCoreTests/WireProtocolTests.swift`, and (same commit) FIX the minor assertion in the existing `helloCarriesAllProtocolFields` — change line 41 `#expect(body?["protocol_minor"] as? Int == 0)` to `== 1` (our live hello now advertises minor 1). Append:
```swift
@Test func clipFilesRoundTripAndTopLevelFieldOrder() throws {
    let files: [(name: String, data: Data)] = [
        (name: "노트.txt", data: Data("one".utf8)),
        (name: "b.bin", data: Data([0, 1, 2])),
    ]
    let msg = WireMessage.clipFiles(files: files, ts: 5.0)
    #expect(msg.kind == "files")
    #expect(msg.ts == 5.0)
    #expect(msg.bytes == 6)                                     // sum of raw byte counts
    let entries = try #require(msg.files)
    #expect(entries.count == 2)
    #expect(entries[0].name == "노트.txt")
    #expect(entries[0].bytes == 3)
    #expect(strictBase64Decode(entries[0].content) == Data("one".utf8))
    #expect(entries[0].hash == sha256Hex(Data("one".utf8)))
    #expect(msg.hash == aggregateFilesHash([
        sha256Hex(Data("one".utf8)), sha256Hex(Data([0, 1, 2]))]))
    // Round-trip through the frame codec.
    let frame = try msg.encodeFrame()
    let decoded = try #require(WireMessage.decodeBody(frame.dropFirst(4)))
    #expect(decoded.files?.count == 2)
    #expect(decoded.files?[0].name == "노트.txt")
    #expect(decoded.hash == msg.hash)
    // Top-level key order: type before kind before files (CONTRACT).
    let json = String(data: frame.dropFirst(4), encoding: .utf8)!
    #expect(json.range(of: "\"type\"")!.lowerBound < json.range(of: "\"kind\"")!.lowerBound)
    #expect(json.range(of: "\"kind\"")!.lowerBound < json.range(of: "\"files\"")!.lowerBound)
}

@Test func clipFilesNormalizesEntryNamesToNFC() {
    let base = "결과보고서"
    let nfd = base.decomposedStringWithCanonicalMapping + ".pdf"
    let nfc = base.precomposedStringWithCanonicalMapping + ".pdf"
    let msg = WireMessage.clipFiles(files: [(name: nfd, data: Data([1]))], ts: 0)
    #expect(Array((msg.files?[0].name ?? "").utf8) == Array(nfc.utf8))
}

@Test func decodeFileEntriesDropsInvalidOrEmpty() {
    let good = WireFileEntry(
        name: "a.txt", content: Data("x".utf8).base64EncodedString(),
        hash: sha256Hex(Data("x".utf8)), bytes: 1)
    let bad = WireFileEntry(name: "b.txt", content: "!!!not-base64!!!", hash: "0", bytes: 0)
    #expect(decodeFileEntries([good, bad]) == nil)              // any invalid -> whole frame dropped
    #expect(decodeFileEntries([]) == nil)                       // empty array -> dropped
    #expect(decodeFileEntries(nil) == nil)
    let ok = decodeFileEntries([good])
    #expect(ok?.count == 1)
    #expect(ok?[0].name == "a.txt")
    #expect(ok?[0].data == Data("x".utf8))
}

@Test func clipPayloadFilesKindAndAggregateHash() {
    let payload = ClipPayload.files([
        (name: "a", data: Data("one".utf8)),
        (name: "b", data: Data("two".utf8)),
    ])
    #expect(payload.kind == "files")
    #expect(payload.payloadHash == aggregateFilesHash([
        sha256Hex(Data("one".utf8)), sha256Hex(Data("two".utf8))]))
}

@Test func protocolMinorIsOne() {
    #expect(Wire.protocolMinor == 1)
}
```

- [ ] **Step 12: Run tests to verify they fail** — `swift test --package-path formacOS --filter clipFiles`. Expected: compile errors `cannot find 'WireFileEntry'` / `type 'WireMessage' has no member 'clipFiles'` / `type 'ClipPayload' has no member 'files'` / `cannot find 'decodeFileEntries'`.

- [ ] **Step 13: Bump the minor constant** — in `formacOS/Sources/AnyClipCore/WireProtocol.swift`, change line 7 from `public static let protocolMinor = 0` to `public static let protocolMinor = 1`.

- [ ] **Step 14: Add the `files` payload representation** — in `formacOS/Sources/AnyClipCore/WireProtocol.swift`:

(a) Extend `ClipPayload` (lines 31-51). Add the case and both switch arms:
```swift
public enum ClipPayload: Sendable {
    case text(String)
    case image(Data)
    case file(name: String, data: Data)
    case files([(name: String, data: Data)])

    public var kind: String {
        switch self {
        case .text: return "text"
        case .image: return "image"
        case .file: return "file"
        case .files: return "files"
        }
    }

    public var payloadHash: String {
        switch self {
        case .text(let s): return sha256Hex(s)
        case .image(let d): return sha256Hex(d)
        case .file(_, let d): return sha256Hex(d)
        case .files(let fs): return aggregateFilesHash(fs.map { sha256Hex($0.data) })
        }
    }
}
```

(b) Insert a `WireFileEntry` struct immediately before `public struct WireMessage` (before the doc comment at line 53). snake-free field names are the wire keys, in CONTRACT order:
```swift
/// One entry inside a kind:"files" clip. Property order name,content,hash,bytes
/// IS the wire field order (JSONEncoder emits keys in declaration order).
public struct WireFileEntry: Codable, Sendable, Equatable {
    public var name: String
    public var content: String
    public var hash: String
    public var bytes: Int
    public init(name: String, content: String, hash: String, bytes: Int) {
        self.name = name
        self.content = content
        self.hash = hash
        self.bytes = bytes
    }
}

```

(c) Add the `files` field to `WireMessage` between `content` (line 67) and `hash` (line 68) — position controls the top-level wire order type,kind,files,hash,ts,bytes:
```swift
    public var kind: String?
    public var content: String?
    public var files: [WireFileEntry]?
    public var hash: String?
```

(d) Add `clipFiles` immediately after `clipFile` (after line 122):
```swift

    public static func clipFiles(files: [(name: String, data: Data)], ts: Double) -> WireMessage {
        var m = WireMessage(type: "clip")
        m.kind = "files"
        var entries: [WireFileEntry] = []
        var hashes: [String] = []
        var total = 0
        for f in files {
            let h = sha256Hex(f.data)
            entries.append(WireFileEntry(
                name: f.name.precomposedStringWithCanonicalMapping,  // NFC on the wire
                content: f.data.base64EncodedString(), hash: h, bytes: f.data.count))
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

(e) Add the `.files` arm to `clip(_:ts:)` (lines 124-130):
```swift
    public static func clip(_ payload: ClipPayload, ts: Double) -> WireMessage {
        switch payload {
        case .text(let s): return clipText(s, ts: ts)
        case .image(let d): return clipImage(d, ts: ts)
        case .file(let n, let d): return clipFile(name: n, data: d, ts: ts)
        case .files(let fs): return clipFiles(files: fs, ts: ts)
        }
    }
```

(f) Add `decodeFileEntries` after `strictBase64Decode` (after line 193):
```swift

/// Decode a kind:"files" message's entries into (name, rawBytes). Returns nil
/// if the array is empty/nil OR ANY entry has non-strict base64 content — the
/// caller drops the WHOLE frame (no partial apply). Names pass through raw;
/// sanitize/uniquify happen write-side. Hashes are never trusted from the wire.
public func decodeFileEntries(_ files: [WireFileEntry]?) -> [(name: String, data: Data)]? {
    guard let files, !files.isEmpty else { return nil }
    var out: [(name: String, data: Data)] = []
    for e in files {
        guard let data = strictBase64Decode(e.content) else { return nil }
        out.append((name: e.name, data: data))
    }
    return out
}
```

- [ ] **Step 15: Run tests to verify they pass** — `swift test --package-path formacOS --filter WireProtocol`. Expected: `clipFilesRoundTripAndTopLevelFieldOrder`, `clipFilesNormalizesEntryNamesToNFC`, `decodeFileEntriesDropsInvalidOrEmpty`, `clipPayloadFilesKindAndAggregateHash`, `protocolMinorIsOne`, and the edited `helloCarriesAllProtocolFields` all `passed`.

- [ ] **Step 16: Commit** —
```
git add formacOS/Sources/AnyClipCore/WireProtocol.swift formacOS/Tests/AnyClipCoreTests/WireProtocolTests.swift
git commit -m "$(cat <<'EOF'
feat(core-swift): kind "files" wire payload + protocol minor 1

Adds WireFileEntry/clipFiles/ClipPayload.files/decodeFileEntries; top-level
hash is the aggregate; bumps protocolMinor 0->1 (advisory, links stay up).

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 17: Write the failing golden-vector test** — append to `formacOS/Tests/AnyClipCoreTests/GoldenVectorTests.swift`:
```swift
@Test func goldenClipFilesDecodes() throws {
    let m = try decodeGoldenFrame("clip_files.bin")
    let man = try manifest()
    #expect(m.kind == "files")
    let entries = try #require(m.files)
    let names = man["files_names"] as! [String]
    let hashes = man["files_hashes"] as! [String]
    #expect(entries.count == names.count)
    for (i, e) in entries.enumerated() {
        #expect(e.name == names[i])
        let data = try #require(strictBase64Decode(e.content))
        #expect(sha256Hex(data) == hashes[i])          // per-file hash recomputed from bytes
        #expect(e.hash == hashes[i])                    // wire hash matches manifest
        #expect(e.bytes == data.count)
    }
    // Aggregate + total match the Python-canonical manifest values.
    #expect(m.hash == man["files_aggregate"] as? String)
    #expect(aggregateFilesHash(hashes) == man["files_aggregate"] as? String)
    #expect(m.bytes == man["files_total_bytes"] as? Int)
}
```

- [ ] **Step 18: Run test to verify it fails** — `swift test --package-path formacOS --filter goldenClipFilesDecodes`. Expected (if Task 5 fixtures are present): initially fails only if run before Step 13-14 land; after those it must pass. If it fails with `nil` fixture load, confirm Task 5 committed `clip_files.bin` + the four manifest keys — this test CONSUMES them and cannot be made green without them.

- [ ] **Step 19: Run test to verify it passes** — `swift test --package-path formacOS --filter goldenClipFilesDecodes`. Expected: `passed`. Then run the whole core suite: `swift test --package-path formacOS --filter AnyClipCoreTests`. Expected: all pass (including the pre-existing `goldenHelloDecodes`, which asserts the FIXTURE's minor — see Concerns).

- [ ] **Step 20: Commit** —
```
git add formacOS/Tests/AnyClipCoreTests/GoldenVectorTests.swift
git commit -m "$(cat <<'EOF'
test(core-swift): golden-vector decode for kind "files"

Asserts clip_files.bin per-entry names/hashes/bytes and the aggregate against
the Python-canonical manifest.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Swift daemon — multi-grab, greedy filter, PeerLink files handling, receive apply, old-peer fallback

> **PLAN WARNING (this Mac):** running the `AnyClipDaemonTests` suites here can flip the live AnyClip menu-bar app into a false auth-error state (the test daemons bind/handshake on loopback and the running app misreads it). This is a known, harmless issue — **restart the AnyClip app to clear the sticky red icon.** It does not indicate a real failure.

**Files:**
- Modify: `formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift` (add `maxFilesPerClip`; rename `lastFileFingerprint`→`lastFileFingerprints`; drop `oversizeFileWarned`; reseed init at lines 81-83; rewrite `checkFileClipboard` at lines 141-180; rewrite `updateLocalFile` at lines 201-219 to delegate to new `updateLocalFiles`; add `grabFileURLs`, keep-compat `grabImage` at 234-243)
- Modify: `formacOS/Sources/AnyClipDaemon/PeerLink.swift` (add `peerProtocolMinor` field near line 36; set it in the registration block near line 306; reset in teardown near line 349 and `shutdown` near line 431; add `"files"` branch to `handleClip` at lines 358-383)
- Modify: `formacOS/Sources/AnyClipDaemon/Daemon.swift` (add top-level `downgradeForPeer`; add `.files` arm to inbound `onClip` switch at lines 130-159; rewrite `sendOutbound` at lines 169-191 for downgrade + `.files` arm)
- Test: `formacOS/Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift`, `.../PeerLinkTests.swift`, `.../DaemonTests.swift`

**Interfaces:**
- Consumes (Task 6): `ClipPayload.files([(name:String,data:Data)])`, `WireMessage.clipFiles`, `decodeFileEntries`, `aggregateFilesHash`, `sanitizeFilename`, `uniquifyNames`, `WireFileEntry`.
- Produces:
  - `ClipboardWatcher.updateLocalFiles(_ files: [(name: String, data: Data)]) -> [(name: String, data: Data)]` (returns the files actually PLACED on the clipboard, with sanitized+uniquified names)
  - `ClipboardWatcher.maxFilesPerClip: Int` (= 100)
  - `PeerLink.peerProtocolMinor: Int` (public private(set), the peer's advertised minor from the hello; 0 when unlinked)
  - `public func downgradeForPeer(_ payload: ClipPayload, peerMinor: Int) -> (payload: ClipPayload?, dropped: Int)`

- [ ] **Step 1: Write the failing watcher multi-file tests** — append to `formacOS/Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift` (reuse the file's existing `privatePasteboard()`, `tempDir()`, `makeWatcher` helpers):
```swift
@Test @MainActor func twoFilesOnClipboardEmitsFilesPayload() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let f1 = dir.appendingPathComponent("a.txt"); try Data("one".utf8).write(to: f1)
    let f2 = dir.appendingPathComponent("b.txt"); try Data("two".utf8).write(to: f2)
    pb.clearContents()
    pb.writeObjects([f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] {
        #expect(fs.count == 2)
        #expect(fs.contains { $0.name == "a.txt" && $0.data == Data("one".utf8) })
        #expect(fs.contains { $0.name == "b.txt" && $0.data == Data("two".utf8) })
    } else { Issue.record("expected .files payload") }
}

@Test @MainActor func sameFileSelectionDetectedOnlyOnce() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let f1 = dir.appendingPathComponent("a.txt"); try Data("one".utf8).write(to: f1)
    let f2 = dir.appendingPathComponent("b.txt"); try Data("two".utf8).write(to: f2)
    pb.clearContents(); pb.writeObjects([f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().count == 1)
    // Re-copy the identical selection: changeCount ticks, but the fingerprint
    // list is unchanged, so nothing re-emits.
    pb.clearContents(); pb.writeObjects([f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().count == 1)
}

@Test @MainActor func folderMixedWithFilesSkipsFolderSyncsFiles() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let folder = dir.appendingPathComponent("sub", isDirectory: true)
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    let f1 = dir.appendingPathComponent("a.txt"); try Data("one".utf8).write(to: f1)
    let f2 = dir.appendingPathComponent("b.txt"); try Data("two".utf8).write(to: f2)
    pb.clearContents()
    pb.writeObjects([folder as NSURL, f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] { #expect(fs.count == 2) } else { Issue.record("expected .files") }
    #expect(skipped.get().contains { $0.contains("folders are not supported") })
}

@Test @MainActor func budgetGreedySkipOverflowFallsBackToSingleFile() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    // Two 7 MiB sparse files: the first fits the ~11.65 MB budget, the second
    // overflows the cumulative sum and is skipped -> one survivor -> kind "file".
    func sparse(_ name: String) throws -> URL {
        let u = dir.appendingPathComponent(name)
        FileManager.default.createFile(atPath: u.path, contents: nil)
        let h = try FileHandle(forWritingTo: u)
        try h.truncate(atOffset: UInt64(7 * 1024 * 1024)); try h.close()
        return u
    }
    let f1 = try sparse("big1.bin"); let f2 = try sparse("big2.bin")
    pb.clearContents(); pb.writeObjects([f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .file(let name, _) = got[0] { #expect(name == "big1.bin") }
    else { Issue.record("expected single-file fallback, got \(got)") }
    #expect(skipped.get().contains { $0.contains("skipped") })
}

@Test @MainActor func maxFilesCapEmitsAtMostOneHundred() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    var urls: [NSURL] = []
    for i in 0..<101 {
        let u = dir.appendingPathComponent("f\(i).txt")
        try Data("x".utf8).write(to: u)
        urls.append(u as NSURL)
    }
    pb.clearContents(); pb.writeObjects(urls)
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] { #expect(fs.count == 100) } else { Issue.record("expected .files") }
    #expect(skipped.get().contains { $0.contains("skipped") })
}

@Test @MainActor func updateLocalFilesWritesUniquifiedAndDoesNotEcho() async throws {
    let pb = privatePasteboard()
    let received = tempDir()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: received, changes: changes, skipped: skipped)
    let placed = watcher.updateLocalFiles([
        (name: "dup.txt", data: Data("1".utf8)),
        (name: "dup.txt", data: Data("2".utf8)),
    ])
    #expect(placed.count == 2)
    #expect(placed.map(\.name) == ["dup.txt", "dup (2).txt"])
    #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("dup.txt").path))
    #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("dup (2).txt").path))
    // Placement baselines the fingerprint list, so the next poll does not echo.
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}
```

- [ ] **Step 2: Run tests to verify they fail** — `swift test --package-path formacOS --filter twoFilesOnClipboardEmitsFilesPayload`. Expected: compile error `value of type 'ClipboardWatcher' has no member 'updateLocalFiles'` and (once compiling) `.files` never emitted because the watcher still grabs only `.first`.

- [ ] **Step 3: Rework the watcher fields** — in `formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift`:

(a) After line 48 (`static let fileBudget = …`), add:
```swift
    /// Sender-side cap; the receiver stays lenient. Matches MAX_FILES_PER_CLIP.
    static let maxFilesPerClip = 100
```

(b) Replace the fingerprint field (line 61) and delete the warned flag (line 62):
```swift
    private var lastFileFingerprints: [FileFingerprint] = []
```
(remove the `private var oversizeFileWarned = false` line entirely).

(c) Reseed in `init` — replace lines 81-83:
```swift
        lastFileFingerprints = Self.grabFileURLs(pasteboard).compactMap { FileFingerprint(url: $0) }
```

- [ ] **Step 4: Rewrite `checkFileClipboard`** — replace the whole method (lines 141-180) with:
```swift
    private func checkFileClipboard() async {
        let urls = Self.grabFileURLs(pasteboard)
        guard !urls.isEmpty else { return }
        let fingerprints = urls.compactMap { FileFingerprint(url: $0) }
        guard fingerprints != lastFileFingerprints else { return }
        // Record FIRST so the same selection is never re-detected (no retry loop),
        // regardless of what we end up sending.
        lastFileFingerprints = fingerprints

        var sendable: [(name: String, data: Data)] = []
        var running = 0
        var skippedForSize = 0
        for url in urls {
            guard let fp = FileFingerprint(url: url) else { continue }
            if fp.isDirectory {
                AnyLog.shared.warning("folder on clipboard not synced (unsupported): \(url.path)")
                if let onSkipped = callbacks.onFileSkipped {
                    await onSkipped(
                        "folder not synced — folders are not supported: \(url.lastPathComponent)")
                }
                continue
            }
            if sendable.count >= Self.maxFilesPerClip || running + fp.size > Self.fileBudget {
                skippedForSize += 1
                continue
            }
            guard let data = try? Data(contentsOf: url) else {
                AnyLog.shared.warning("file read failed for \(url.path); skipping")
                continue
            }
            running += fp.size
            sendable.append((name: url.lastPathComponent, data: data))
        }
        if skippedForSize > 0, let onSkipped = callbacks.onFileSkipped {
            await onSkipped("\(skippedForSize) file(s) skipped (too large to sync)")
        }
        // 0 sendable -> nothing. 1 -> legacy .file. >=2 -> .files.
        if sendable.count == 1 {
            await callbacks.onChange(.file(name: sendable[0].name, data: sendable[0].data))
        } else if sendable.count >= 2 {
            await callbacks.onChange(.files(sendable))
        }
    }
```

- [ ] **Step 5: Add `updateLocalFiles`, make `updateLocalFile` delegate** — replace `updateLocalFile` (lines 201-219) with:
```swift
    @discardableResult
    public func updateLocalFile(name: String, data: Data) -> Bool {
        !updateLocalFiles([(name: name, data: data)]).isEmpty
    }

    /// Sanitize + uniquify, write every file into the flat receivedDir, then
    /// place ALL written URLs on the clipboard in ONE writeObjects. Returns the
    /// files actually PLACED (sanitized names) so the caller can baseline echo
    /// suppression to the placed set.
    @discardableResult
    public func updateLocalFiles(_ files: [(name: String, data: Data)]) -> [(name: String, data: Data)] {
        do {
            try FileManager.default.createDirectory(
                at: receivedDir, withIntermediateDirectories: true)
        } catch {
            AnyLog.shared.warning("received dir create failed: \(error)")
            return []
        }
        let names = uniquifyNames(files.map { sanitizeFilename($0.name) })
        var placedURLs: [NSURL] = []
        var placed: [(name: String, data: Data)] = []
        for (i, f) in files.enumerated() {
            let target = receivedDir.appendingPathComponent(names[i])
            do {
                try f.data.write(to: target)
                placedURLs.append(target as NSURL)
                placed.append((name: names[i], data: f.data))
            } catch {
                AnyLog.shared.warning("file write to \(target.path) failed: \(error)")
            }
        }
        guard !placedURLs.isEmpty else { return [] }
        // Baseline the fingerprint list to the placed paths BEFORE the clipboard
        // write so a racing poll cannot echo.
        lastFileFingerprints = placedURLs.compactMap { FileFingerprint(url: $0 as URL) }
        pasteboard.clearContents()
        let ok = pasteboard.writeObjects(placedURLs)
        lastChangeCount = pasteboard.changeCount
        if !ok { AnyLog.shared.warning("clipboard write (files) failed") }
        return placed
    }
```

- [ ] **Step 6: Add `grabFileURLs`, update `grabImage`** — replace `grabFileURL` (lines 223-229) with:
```swift
    static func grabFileURLs(_ pb: NSPasteboard) -> [URL] {
        let options: [NSPasteboard.ReadingOptionKey: Any] =
            [.urlReadingFileURLsOnly: true]
        let raw = pb.readObjects(forClasses: [NSURL.self], options: options)
        return (raw as? [URL]) ?? []
    }
```
And in `grabImage` change line 235 `if grabFileURL(pb) != nil { return nil }` to:
```swift
        if !grabFileURLs(pb).isEmpty { return nil }
```

- [ ] **Step 7: Run tests to verify they pass** — `swift test --package-path formacOS --filter ClipboardWatcherTests`. Expected: the new multi-file tests and ALL pre-existing watcher tests (`smallFileOnClipboardIsSent`, `oversizedFileIsSkipped`, `folderOnClipboardIsSkippedWithToastOnce`, `updateLocalFileWritesToReceivedDirAndDoesNotEcho`) `passed`.

- [ ] **Step 8: Commit** —
```
git add formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift formacOS/Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift
git commit -m "$(cat <<'EOF'
feat(daemon-swift): multi-file clipboard grab, greedy budget, batch apply

grabFileURLs returns the full list; ordered fingerprint gate; folder/size
skips with count notifications; updateLocalFiles sanitizes+uniquifies and
places all URLs in one writeObjects.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 9: Write the failing PeerLink tests** — append to `formacOS/Tests/AnyClipDaemonTests/PeerLinkTests.swift` (reuse its `makeLink`/`waitUntil` helpers):
```swift
@Test func twoLinksExchangeMultipleFiles() async throws {
    let aClips = Locked<[ClipPayload]>([]); let aEvents = Locked<[DaemonEvent]>([])
    let bClips = Locked<[ClipPayload]>([]); let bEvents = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28485, name: "node-a", clips: aClips, events: aEvents)
    let b = await makeLink(token: "tok", port: 28486, name: "node-b", clips: bClips, events: bEvents)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })
    let connectB = Task {
        await b.tryConnect(
            to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28485)!),
            label: "127.0.0.1:28485")
    }
    defer { connectB.cancel() }
    #expect(await waitUntil { let x = await a.isActive; let y = await b.isActive; return x && y })

    let files: [(name: String, data: Data)] = [
        (name: "노트.txt", data: Data("body one".utf8)),
        (name: "réport (v2).bin", data: Data([0, 1, 2, 3])),
    ]
    await b.sendClip(.files(files))
    #expect(await waitUntil {
        aClips.get().contains {
            if case .files(let fs) = $0 {
                return fs.count == 2
                    && fs.contains { $0.name == "노트.txt" && $0.data == Data("body one".utf8) }
                    && fs.contains { $0.name == "réport (v2).bin" && $0.data == Data([0, 1, 2, 3]) }
            }
            return false
        }
    })
    await a.shutdown(); await b.shutdown()
}

@Test func peerProtocolMinorRetainedFromHello() async throws {
    let clips = Locked<[ClipPayload]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28487, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28487)!))
    try await raw.start()
    defer { raw.cancel() }
    var hello = WireMessage.hello(
        tokenHash: sha256Hex("tok"), nodeID: "ffffffff-oldpeer", name: "old", appVersion: "1.0.0")
    hello.protocol_minor = 0                                  // pre-1.1 peer
    try await raw.sendFrame(hello)
    _ = try await raw.receiveMessage()                        // a's hello
    #expect(await waitUntil { await a.isActive })
    #expect(await a.peerProtocolMinor == 0)
    await a.shutdown()
}
```

- [ ] **Step 10: Run tests to verify they fail** — `swift test --package-path formacOS --filter peerProtocolMinorRetainedFromHello`. Expected: compile error `value of type 'PeerLink' has no member 'peerProtocolMinor'`, and `twoLinksExchangeMultipleFiles` never surfaces `.files` (PeerLink drops the unknown kind).

- [ ] **Step 11: Retain the peer minor and handle `"files"` inbound** — in `formacOS/Sources/AnyClipDaemon/PeerLink.swift`:

(a) After line 36 (`public private(set) var peerName: String?`) add:
```swift
    /// Peer's advertised protocol minor from the hello; gates the outbound
    /// files/kind:"file" downgrade. 0 when unlinked.
    public private(set) var peerProtocolMinor: Int = 0
```

(b) In the registration block, right after `peerNodeID = peerID` (line 306) add:
```swift
        peerProtocolMinor = peerVersion.protocolMinor
```

(c) In the teardown block (lines 346-351), inside `if wasActive {`, add after `peerName = nil`:
```swift
            peerProtocolMinor = 0
```

(d) In `shutdown()` (lines 428-436), after `peerName = nil` add:
```swift
        peerProtocolMinor = 0
```

(e) In `handleClip` (lines 358-383), add a `"files"` case before `default:`:
```swift
        case "files":
            guard let entries = decodeFileEntries(m.files) else {
                AnyLog.shared.warning(
                    "bad files payload from peer (empty or invalid base64); dropping frame")
                return
            }
            await onClip?(.files(entries))
```
(No change to `sendClip` — it already routes through `WireMessage.clip(payload,ts:)`, which Task 6 extended with the `.files` arm.)

- [ ] **Step 12: Run tests to verify they pass** — `swift test --package-path formacOS --filter PeerLinkTests`. Expected: `twoLinksExchangeMultipleFiles`, `peerProtocolMinorRetainedFromHello`, and all pre-existing PeerLink tests `passed`.

- [ ] **Step 13: Commit** —
```
git add formacOS/Sources/AnyClipDaemon/PeerLink.swift formacOS/Tests/AnyClipDaemonTests/PeerLinkTests.swift
git commit -m "$(cat <<'EOF'
feat(daemon-swift): PeerLink kind "files" receive + retained peer minor

handleClip decodes kind:"files" (whole-frame drop on bad base64/empty);
peerProtocolMinor is captured from the hello for the outbound send gate.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 14: Write the failing downgrade test** — append to `formacOS/Tests/AnyClipDaemonTests/DaemonTests.swift`:
```swift
@Test func downgradeForPeerKeepsFilesForModernPeer() {
    let payload = ClipPayload.files([(name: "a", data: Data([1])), (name: "b", data: Data([2]))])
    let (out, dropped) = downgradeForPeer(payload, peerMinor: 1)
    #expect(dropped == 0)
    if case .files(let fs)? = out { #expect(fs.count == 2) } else { Issue.record("expected .files") }
}

@Test func downgradeForPeerDropsToFirstFileForOldPeer() {
    let payload = ClipPayload.files([(name: "a", data: Data([1])), (name: "b", data: Data([2]))])
    let (out, dropped) = downgradeForPeer(payload, peerMinor: 0)
    #expect(dropped == 1)                                     // one file left behind
    if case .file(let name, let data)? = out {
        #expect(name == "a"); #expect(data == Data([1]))     // first file, legacy kind:"file"
    } else { Issue.record("expected .file") }
}

@Test func downgradePassesNonFilesPayloadsThrough() {
    let (out, dropped) = downgradeForPeer(.text("hi"), peerMinor: 0)
    #expect(dropped == 0)
    if case .text(let s)? = out { #expect(s == "hi") } else { Issue.record("expected .text") }
}
```

- [ ] **Step 15: Run test to verify it fails** — `swift test --package-path formacOS --filter downgradeForPeer`. Expected: compile error `cannot find 'downgradeForPeer' in scope`.

- [ ] **Step 16: Add `downgradeForPeer` and wire it into the daemon** — in `formacOS/Sources/AnyClipDaemon/Daemon.swift`:

(a) Add a top-level function after `clearDirectoryFiles` (after line 49):
```swift

/// Decide what to actually send given the peer's protocol minor. Minor >= 1
/// understands kind:"files" (pass through, dropped == 0). Minor 0 predates
/// multi-file sync: degrade a .files batch to its first file as legacy
/// kind:"file"; `dropped` counts the files left behind for the notification.
/// Returns a nil payload only for an empty .files batch (nothing to send).
public func downgradeForPeer(
    _ payload: ClipPayload, peerMinor: Int
) -> (payload: ClipPayload?, dropped: Int) {
    guard case .files(let fs) = payload, peerMinor < 1 else { return (payload, 0) }
    guard let first = fs.first else { return (nil, 0) }
    return (.file(name: first.name, data: first.data), fs.count - 1)
}
```

(b) Add the `.files` arm to the inbound `onClip` switch, immediately after the `.file` case (after line 157, before the switch's closing `}` at line 158):
```swift
                case .files(let fs):
                    let placed = await MainActor.run {
                        watcherBox.get()?.updateLocalFiles(fs) ?? []
                    }
                    // markReceived("files", aggregate) already ran at the top of
                    // this handler. If exactly one file landed, the watcher will
                    // re-detect it as a single-file copy (kind "file"), so also
                    // suppress that hash.
                    if placed.count == 1 {
                        await coordinator.markReceived(
                            kind: "file", hash: sha256Hex(placed[0].data))
                    }
                    AnyLog.shared.info(
                        "<- received \(fs.count) files from \(peer) "
                        + "(\(placed.count) written to clipboard)")
                    notify("AnyClip ← \(peer)", "\(placed.count) files")
```

(c) Replace the whole `sendOutbound` closure (lines 169-191) with the downgrade-aware version:
```swift
        let sendOutbound: @Sendable (ClipPayload) async -> Void = { [coordinator, weak link] rawPayload in
            guard let link else { return }
            guard await link.isActive else { return }

            // Old-peer fallback: a peer that predates protocol 1.1 cannot decode
            // kind:"files". Degrade a batch to its first file and notify.
            let (maybePayload, dropped) = downgradeForPeer(
                rawPayload, peerMinor: await link.peerProtocolMinor)
            guard let payload = maybePayload else { return }
            if dropped > 0 {
                notify("AnyClip",
                    "\(dropped) file(s) not synced — update the peer to receive multiple files")
            }

            guard await coordinator.shouldSend(
                kind: payload.kind, hash: payload.payloadHash)
            else {
                AnyLog.shared.debug("skip echo of just-received \(payload.kind)")
                return
            }
            await link.sendClip(payload)
            let peer = await link.peerName ?? "peer"
            switch payload {
            case .text(let text):
                AnyLog.shared.info("-> sent text \(text.count) chars to \(peer)")
                notify("AnyClip → \(peer)", preview(text))
            case .image(let png):
                AnyLog.shared.info("-> sent image \(png.count) bytes to \(peer)")
                notify("AnyClip → \(peer)", "image (\(png.count / 1024) KB)")
            case .file(let name, let data):
                AnyLog.shared.info("-> sent file \(name) \(data.count) bytes to \(peer)")
                notify("AnyClip → \(peer)", "file: \(name) (\(data.count / 1024) KB)")
            case .files(let fs):
                let total = fs.reduce(0) { $0 + $1.data.count }
                AnyLog.shared.info("-> sent \(fs.count) files \(total) bytes to \(peer)")
                notify("AnyClip → \(peer)", "\(fs.count) files")
            }
        }
```

- [ ] **Step 17: Run tests to verify they pass** — `swift test --package-path formacOS --filter DaemonTests`. Expected: the three `downgradeForPeer*` tests and the pre-existing daemon tests `passed`.

- [ ] **Step 18: Run the full daemon suite** — `swift test --package-path formacOS --filter AnyClipDaemonTests`. Expected: all pass. (See the PLAN WARNING at the top of this task — restart the live AnyClip app afterward if its menu-bar icon went red.)

- [ ] **Step 19: Commit** —
```
git add formacOS/Sources/AnyClipDaemon/Daemon.swift formacOS/Tests/AnyClipDaemonTests/DaemonTests.swift
git commit -m "$(cat <<'EOF'
feat(daemon-swift): kind "files" send/receive wiring + old-peer downgrade

Inbound places the batch and suppresses re-detection; outbound gates on the
peer's minor, degrading to first-file-as-kind:"file" for pre-1.1 peers.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: fake_peer `--send-files` + Swift InteropTests (both directions)

> **PLAN WARNING (this Mac):** the interop suite runs a real handshake on loopback; like the other daemon suites it can flip the live AnyClip menu-bar icon into a false auth-error state. Restart the AnyClip app to clear it — it is not a real failure.

**Files:**
- Modify: `formacOS/Scripts/fake_peer.py` (add `import base64`, `import unicodedata`; add `--send-files` argparse flag near line 59; add an aggregate helper; send one `kind:"files"` clip after the existing text clip when the flag is set, before the recv loop at line 95)
- Test: `formacOS/Tests/AnyClipDaemonTests/InteropTests.swift` (extend the send-direction assertions in `interopWithPythonFakePeer`; add a new receive-direction test)

**Interfaces:**
- Consumes (Task 6/7): `ClipPayload.files`, `aggregateFilesHash`, `sha256Hex`, `PeerLink.sendClip(.files(...))`, `PeerLink` capturing `onClip` (the existing bare-link interop harness surfaces `ClipPayload` — it does NOT run the watcher write path).
- Produces: no new Swift symbols; the fixed two-entry batch the fake peer sends is `("노트.txt", b"multi body one")`, `("(E&S) plan.txt", b"multi body two")` (names NFC).

- [ ] **Step 1: Write the failing send-direction assertion** — in `formacOS/Tests/AnyClipDaemonTests/InteropTests.swift`, inside `interopWithPythonFakePeer`, after the existing Swift→Python send block (after line 92 `await link.sendPing()` and its assert block ending line 103), insert:
```swift
    // Swift -> Python: a two-file kind:"files" clip. The fake peer records it
    // verbatim; assert both names and the aggregate hash land in the outfile.
    let mf1 = (name: "노트.txt", data: Data("files body one".utf8))
    let mf2 = (name: "(E&S) plan.txt", data: Data("files body two".utf8))
    await link.sendClip(.files([mf1, mf2]))
    let expectedAgg = aggregateFilesHash([sha256Hex(mf1.data), sha256Hex(mf2.data)])
    #expect(await waitUntil(5) {
        guard let lines = try? String(contentsOf: outFile, encoding: .utf8) else { return false }
        return lines.contains("\"kind\": \"files\"")
            && lines.contains("노트.txt")
            && lines.contains("(E&S) plan.txt")
            && lines.contains(expectedAgg)
    })
```

- [ ] **Step 2: Run test to verify it fails** — `swift test --package-path formacOS --filter interopWithPythonFakePeer`. Expected: the new `#expect` times out / fails because the fake peer never receives a well-formed `kind:"files"` (it does, but this step only proves the assertion is wired; it should already pass once Task 6/7 are in, since `sendClip(.files)` works). If it FAILS here it means the frame isn't reaching Python — re-check Task 6 `clipFiles` is committed. (This test needs no fake_peer change; the send path is pure Swift.)

- [ ] **Step 3: Confirm the send-direction assertion passes, then commit** — `swift test --package-path formacOS --filter interopWithPythonFakePeer`. Expected: `passed`.
```
git add formacOS/Tests/AnyClipDaemonTests/InteropTests.swift
git commit -m "$(cat <<'EOF'
test(interop-swift): Swift->Python kind "files" send is recorded intact

Asserts both filenames and the aggregate hash reach the fake peer's log.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 4: Write the failing receive-direction test** — append a new test to `formacOS/Tests/AnyClipDaemonTests/InteropTests.swift` (reuses the module's top-level `scriptsDir()` helper):
```swift
@Test func interopReceivesMultipleFilesFromFakePeer() async throws {
    let port: UInt16 = 28493
    let outFile = FileManager.default.temporaryDirectory
        .appendingPathComponent("fake-peer-\(UUID().uuidString).jsonl")

    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    process.arguments = [
        "python3", scriptsDir().appendingPathComponent("fake_peer.py").path,
        "--port", "\(port)", "--token", "interop-token",
        "--out", outFile.path, "--send-files",
    ]
    let stdout = Pipe()
    process.standardOutput = stdout
    try process.run()
    defer { if process.isRunning { process.terminate() } }

    var readyReceived = false
    let readyDeadline = Date().addingTimeInterval(10)
    var accumulated = Data()
    while Date() < readyDeadline {
        let chunk = stdout.fileHandleForReading.availableData
        if !chunk.isEmpty { accumulated.append(chunk) }
        if let s = String(data: accumulated, encoding: .utf8), s.contains("READY") {
            readyReceived = true
            break
        }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
    try #require(readyReceived)

    let clips = Locked<[ClipPayload]>([])
    let events = Locked<[DaemonEvent]>([])
    let link = PeerLink(
        config: PeerLink.LinkConfig(
            token: "interop-token", port: 28494, name: "swift-interop",
            appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased())
    await link.setHandlers(
        onClip: { clips.set(clips.get() + [$0]) },
        emit: { events.set(events.get() + [$0]) })

    let sessionTask = Task {
        await link.tryConnect(
            to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!),
            label: "127.0.0.1:\(port)")
    }
    defer { sessionTask.cancel() }

    func waitUntil(_ timeout: Double, _ cond: @escaping () async -> Bool) async -> Bool {
        let deadline = monotonicNow() + timeout
        while monotonicNow() < deadline {
            if await cond() { return true }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        return await cond()
    }

    #expect(await waitUntil(5) { await link.isActive })

    // Python -> Swift: the two-file batch surfaces with intact names, incl. the
    // parens/ampersand name the OLD whitelist would have mangled.
    #expect(await waitUntil(5) {
        clips.get().contains {
            if case .files(let fs) = $0 {
                return fs.count == 2
                    && fs.contains { $0.name == "노트.txt" && $0.data == Data("multi body one".utf8) }
                    && fs.contains { $0.name == "(E&S) plan.txt" && $0.data == Data("multi body two".utf8) }
            }
            return false
        }
    })
    // The received name is denylist-safe end-to-end: sanitize keeps it verbatim.
    #expect(sanitizeFilename("(E&S) plan.txt") == "(E&S) plan.txt")

    // Aggregate recomputed from the decoded bytes matches the CONTRACT formula.
    let expected = aggregateFilesHash([
        sha256Hex(Data("multi body one".utf8)), sha256Hex(Data("multi body two".utf8))])
    #expect(clips.get().contains {
        if case .files(let fs) = $0 {
            return aggregateFilesHash(fs.map { sha256Hex($0.data) }) == expected
        }
        return false
    })

    await link.shutdown()
}
```

- [ ] **Step 5: Run test to verify it fails** — `swift test --package-path formacOS --filter interopReceivesMultipleFilesFromFakePeer`. Expected: fails — `fake_peer.py: error: unrecognized arguments: --send-files` (process exits, READY never seen, `#require(readyReceived)` fails).

- [ ] **Step 6: Add `--send-files` to fake_peer** — in `formacOS/Scripts/fake_peer.py`:

(a) Add imports. Change the import block (lines 16-23) so it also imports `base64` and `unicodedata`:
```python
import argparse
import base64
import hashlib
import json
import socket
import struct
import sys
import time
import unicodedata
import uuid
```

(b) Add the flag after line 58 (`ap.add_argument("--out", required=True)`):
```python
    ap.add_argument("--send-files", action="store_true",
                    help="send one kind:\"files\" clip with two entries after handshake")
```

(c) Add an aggregate helper near the other frame helpers (e.g. after `recv_frame`, before `def main`):
```python
def aggregate_files_hash(hashes) -> str:
    """Sorted-hex-concat sha256 — must match anyclip / Swift / C#."""
    return hashlib.sha256("".join(sorted(hashes)).encode("ascii")).hexdigest()
```

(d) Send the batch after the existing text clip (after line 93, the `send_frame(conn, {...text...})` block) and before the `while True:` recv loop at line 95:
```python
    if args.send_files:
        entries_src = [
            ("노트.txt", b"multi body one"),
            ("(E&S) plan.txt", b"multi body two"),
        ]
        files = []
        hashes = []
        total = 0
        for name, body in entries_src:
            h = hashlib.sha256(body).hexdigest()
            files.append({
                "name": unicodedata.normalize("NFC", name),
                "content": base64.b64encode(body).decode("ascii"),
                "hash": h,
                "bytes": len(body),
            })
            hashes.append(h)
            total += len(body)
        send_frame(conn, {
            "type": "clip", "kind": "files", "files": files,
            "hash": aggregate_files_hash(hashes), "ts": time.time(),
            "bytes": total,
        })
```

- [ ] **Step 7: Run test to verify it passes** — `swift test --package-path formacOS --filter interopReceivesMultipleFilesFromFakePeer`. Expected: `passed`. Also re-run the send-direction test to confirm the fake_peer edit didn't regress it: `swift test --package-path formacOS --filter interopWithPythonFakePeer`. Expected: `passed` (that test does not pass `--send-files`, so the extra branch stays dormant).

- [ ] **Step 8: Commit** —
```
git add formacOS/Scripts/fake_peer.py formacOS/Tests/AnyClipDaemonTests/InteropTests.swift
git commit -m "$(cat <<'EOF'
test(interop-swift): fake_peer --send-files + Python->Swift receive test

Fake peer can emit a two-entry kind:"files" clip (NFC names, CONTRACT
aggregate); the daemon surfaces both files intact, incl. "(E&S) plan.txt".

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 9: Full-suite sanity + wire lockstep** — run everything the Swift port owns: `swift test --package-path formacOS`. Expected: all suites pass (core, daemon, interop, golden). If any golden/interop test fails, do NOT hand-edit fixtures — reconcile against Task 5's committed `clip_files.bin`/`manifest.json` and the fake_peer contract above. Restart the live AnyClip app if its icon went red during the daemon/interop runs.

### Task 9: C# core — wire kind "files", aggregate hash, sanitizer fix, uniquify, minor bump, golden vectors

**Files:**
- Modify: `forwindows/src/AnyClipCore/Wire.cs` (bump `ProtocolMinor`, verified line 8)
- Modify: `forwindows/src/AnyClipCore/Hashing.cs` (add `AggregateFilesHash`, verified 12 lines total)
- Modify: `forwindows/src/AnyClipCore/WireMessage.cs` (add `WireFileEntry`, `Files` property, `ClipFiles`, `Clip` switch case — verified lines 14, 29-33, 62-80)
- Modify: `forwindows/src/AnyClipCore/ClipPayload.cs` (add `FilesClip`, verified lines 21-25)
- Modify: `forwindows/src/AnyClipCore/TextHelpers.cs` (rewrite `SanitizeFilename` lines 28-43, add `UniquifyNames`)
- Modify: `forwindows/src/AnyClipCore/PeerLink.cs` (add `PeerProtocolMinor` prop + set/reset at verified lines 42, 294, 353, 434; add `"files"` case in `HandleClipAsync` verified lines 375-386)
- Test: `forwindows/tests/AnyClipCore.Tests/WireMessageTests.cs` (verified: line 47 asserts minor 0; add ClipFiles/aggregate/FilesClip tests)
- Test: `forwindows/tests/AnyClipCore.Tests/PureLogicTests.cs` (verified: `TextHelpersTests` class, line 185 old sanitizer assert)
- Test: `forwindows/tests/AnyClipCore.Tests/PeerLinkTests.cs` (verified: `MakeLink`/`WaitUntil` helpers lines 8-30)
- Test: `forwindows/tests/AnyClipCore.Tests/GoldenVectorTests.cs` (verified: `DecodeGolden`/`Manifest` helpers lines 18-28)

**Interfaces:**
- Consumes (from Task 5, the golden-vectors task): `formacOS/Tests/AnyClipCoreTests/Fixtures/clip_files.bin` and `manifest.json` keys `files_names` (array), `files_hashes` (array, wire order), `files_aggregate` (string), `files_total_bytes` (int) — defined verbatim in the CONTRACT's GOLDEN VECTOR section.
- Produces (later tasks rely on):
  - `WireMessage.WireFileEntry` record — nullable init props `Name`/`Content`/`Hash`/`Bytes` with `[JsonPropertyName]` in order name, content, hash, bytes.
  - `WireMessage.Files { get; init; }` → `IReadOnlyList<WireFileEntry>?`
  - `static WireMessage WireMessage.ClipFiles(IReadOnlyList<(string Name, byte[] Data)> files, double ts)`
  - `static string Hashing.AggregateFilesHash(IEnumerable<string> hashes)`
  - `sealed record FilesClip(IReadOnlyList<(string Name, byte[] Data)> Files) : ClipPayload` — `Kind => "files"`, `PayloadHash` = aggregate
  - `TextHelpers.SanitizeFilename(string)` (rewritten), `static IReadOnlyList<string> TextHelpers.UniquifyNames(IReadOnlyList<string> names)`
  - `PeerLink.PeerProtocolMinor { get; }` → `int`
  - `Wire.ProtocolMinor` = 1

---

- [ ] **Step 1: Write the failing test for the minor bump** — In `WireMessageTests.cs`, change the assertion at line 47 inside `HelloCarriesAllProtocolFieldsInSnakeCase`:
  ```csharp
  // was: Assert.Equal(0, root.GetProperty("protocol_minor").GetInt32());
  Assert.Equal(1, root.GetProperty("protocol_minor").GetInt32());
  ```

- [ ] **Step 2: Run test to verify it fails** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter HelloCarriesAllProtocolFieldsInSnakeCase`
  Expected: FAIL — `Assert.Equal() Failure: Expected: 1, Actual: 0`.

- [ ] **Step 3: Bump the constant** — In `Wire.cs`, line 8:
  ```csharp
  public const int ProtocolMinor = 1;
  ```

- [ ] **Step 4: Run test to verify it passes** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter HelloCarriesAllProtocolFieldsInSnakeCase`
  Expected: PASS (1 test). (`VersionNegotiatorTests` use explicit minor literals and are unaffected; `GoldenHelloDecodes` does not assert minor.)

- [ ] **Step 5: Commit** — `git add forwindows/src/AnyClipCore/Wire.cs forwindows/tests/AnyClipCore.Tests/WireMessageTests.cs`
  ```
  feat(win): bump protocol_minor 0->1 for multi-file sync

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 6: Write the failing test for the aggregate hash** — Add to `WireMessageTests.cs` (known-answer computed from `sha256("a")`, `sha256("b")` sorted+concatenated+sha256):
  ```csharp
  [Fact]
  public void AggregateFilesHashIsOrderIndependentKnownAnswer()
  {
      var ha = Hashing.Sha256Hex("a"u8.ToArray());
      var hb = Hashing.Sha256Hex("b"u8.ToArray());
      const string expected =
          "ab19ec537f09499b26f0f62eed7aefad46ab9f498e06a7328ce8e8ef90da6d86";
      Assert.Equal(expected, Hashing.AggregateFilesHash(new[] { ha, hb }));
      Assert.Equal(expected, Hashing.AggregateFilesHash(new[] { hb, ha })); // order-independent
      Assert.NotEqual(expected, Hashing.AggregateFilesHash(new[] { ha, ha }));
  }
  ```

- [ ] **Step 7: Run test to verify it fails** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter AggregateFilesHashIsOrderIndependentKnownAnswer`
  Expected: FAIL to compile — `'Hashing' does not contain a definition for 'AggregateFilesHash'`.

- [ ] **Step 8: Implement `AggregateFilesHash`** — In `Hashing.cs`, add before the closing brace (after the `Sha256Hex(string)` overload). `using System.Linq` is covered by ImplicitUsings; hex is ASCII so the UTF-8 bytes taken by `Sha256Hex(string)` equal the CONTRACT's "ASCII bytes":
  ```csharp
      /// Echo-suppression key for a multi-file clip: sort per-file sha256 hex
      /// strings by ordinal, concatenate with no separator, sha256 the bytes.
      /// Order-independent. Keep in lockstep with Swift/Python.
      public static string AggregateFilesHash(IEnumerable<string> hashes) =>
          Sha256Hex(string.Concat(hashes.OrderBy(h => h, StringComparer.Ordinal)));
  ```

- [ ] **Step 9: Run test to verify it passes** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter AggregateFilesHashIsOrderIndependentKnownAnswer`
  Expected: PASS (1 test).

- [ ] **Step 10: Commit** — `git add forwindows/src/AnyClipCore/Hashing.cs forwindows/tests/AnyClipCore.Tests/WireMessageTests.cs`
  ```
  feat(win): AggregateFilesHash for multi-file echo suppression

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 11: Write the failing tests for `ClipFiles` encode/decode/NFC** — Add to `WireMessageTests.cs` (`using System.Text;` and `using System.Text.Json;` are already present at lines 1-2):
  ```csharp
  [Fact]
  public void ClipFilesEncodesEntriesAndAggregateInContractOrder()
  {
      var files = new List<(string, byte[])>
      {
          ("노트.txt", "one"u8.ToArray()),
          ("réport.bin", new byte[] { 2, 3 }),
      };
      var frame = WireMessage.ClipFiles(files, 7.5).EncodeFrame();
      using var doc = JsonDocument.Parse(frame.AsSpan(4).ToArray());
      var root = doc.RootElement;
      Assert.Equal(new[] { "type", "kind", "files", "hash", "ts", "bytes" },
          root.EnumerateObject().Select(p => p.Name).ToArray());
      Assert.Equal("clip", root.GetProperty("type").GetString());
      Assert.Equal("files", root.GetProperty("kind").GetString());
      Assert.Equal(5, root.GetProperty("bytes").GetInt32()); // 3 + 2 raw bytes
      var arr = root.GetProperty("files");
      Assert.Equal(2, arr.GetArrayLength());
      var e0 = arr[0];
      Assert.Equal(new[] { "name", "content", "hash", "bytes" },
          e0.EnumerateObject().Select(p => p.Name).ToArray());
      Assert.Equal("노트.txt", e0.GetProperty("name").GetString());
      Assert.Equal(Convert.ToBase64String("one"u8.ToArray()),
          e0.GetProperty("content").GetString());
      Assert.Equal(Hashing.Sha256Hex("one"u8.ToArray()),
          e0.GetProperty("hash").GetString());
      Assert.Equal(3, e0.GetProperty("bytes").GetInt32());
      var expectedAgg = Hashing.AggregateFilesHash(new[]
      {
          Hashing.Sha256Hex("one"u8.ToArray()),
          Hashing.Sha256Hex(new byte[] { 2, 3 }),
      });
      Assert.Equal(expectedAgg, root.GetProperty("hash").GetString());
  }

  [Fact]
  public void ClipFilesRoundTripsThroughDecode()
  {
      var files = new List<(string, byte[])>
      {
          ("a.txt", "aa"u8.ToArray()),
          ("b.bin", new byte[] { 0, 1, 2 }),
      };
      var frame = WireMessage.ClipFiles(files, 1.0).EncodeFrame();
      var msg = WireMessage.DecodeBody(frame.AsSpan(4).ToArray())!;
      Assert.Equal("files", msg.Kind);
      Assert.NotNull(msg.Files);
      Assert.Equal(2, msg.Files!.Count);
      Assert.Equal("a.txt", msg.Files[0].Name);
      Assert.Equal("aa", Encoding.UTF8.GetString(
          WireMessage.StrictBase64Decode(msg.Files[0].Content!)!));
      Assert.Equal(new byte[] { 0, 1, 2 },
          WireMessage.StrictBase64Decode(msg.Files[1].Content!));
  }

  [Fact]
  public void ClipFilesNormalizesEachNameToNFC()
  {
      var baseName = "결과보고서";
      var nfd = baseName.Normalize(NormalizationForm.FormD) + ".pdf";
      var nfc = baseName.Normalize(NormalizationForm.FormC) + ".pdf";
      Assert.NotEqual(nfd, nfc);
      var frame = WireMessage.ClipFiles(
          new List<(string, byte[])> { (nfd, new byte[] { 1 }) }, 0).EncodeFrame();
      var msg = WireMessage.DecodeBody(frame.AsSpan(4).ToArray())!;
      Assert.Equal(nfc, msg.Files![0].Name);
  }
  ```

- [ ] **Step 12: Run tests to verify they fail** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter ClipFiles`
  Expected: FAIL to compile — `'WireMessage' does not contain a definition for 'ClipFiles'` and `'Files'`.

- [ ] **Step 13: Add `WireFileEntry`, the `Files` property, `ClipFiles`, and the `Clip` switch case** — In `WireMessage.cs`:
  (a) After the `VersionInfo` record (line 14), add the entry type. Fields are **nullable** so a decode of a malformed entry does not throw and tear down the link — validation happens in `HandleClipAsync`:
  ```csharp
  /// One entry of a kind:"files" clip. Field order name, content, hash, bytes
  /// is golden-vector material. Nullable so a malformed inbound entry decodes
  /// (then gets rejected in PeerLink) rather than failing the whole frame parse.
  public sealed record WireFileEntry
  {
      [JsonPropertyName("name")] public string? Name { get; init; }
      [JsonPropertyName("content")] public string? Content { get; init; }
      [JsonPropertyName("hash")] public string? Hash { get; init; }
      [JsonPropertyName("bytes")] public int? Bytes { get; init; }
  }
  ```
  (b) Insert the `Files` property **between** `Content` (line 30) and `Hash` (line 31) so serialization (declaration order, nulls omitted) yields type, kind, files, hash, ts, bytes:
  ```csharp
      [JsonPropertyName("content")] public string? Content { get; init; }
      [JsonPropertyName("files")] public IReadOnlyList<WireFileEntry>? Files { get; init; }
      [JsonPropertyName("hash")] public string? Hash { get; init; }
  ```
  (c) After `ClipFile` (ends line 72), before `public static WireMessage Clip(` (line 74), add:
  ```csharp
      public static WireMessage ClipFiles(
          IReadOnlyList<(string Name, byte[] Data)> files, double ts)
      {
          var entries = new List<WireFileEntry>(files.Count);
          var hashes = new List<string>(files.Count);
          int total = 0;
          foreach (var (name, data) in files)
          {
              var h = Hashing.Sha256Hex(data);
              hashes.Add(h);
              // NFC per name, same rule as ClipFile. Keep in lockstep with
              // Swift WireMessage.clipFiles and anyclip.send_clip.
              entries.Add(new WireFileEntry
              {
                  Name = TextHelpers.ToNfc(name),
                  Content = Convert.ToBase64String(data),
                  Hash = h, Bytes = data.Length,
              });
              total += data.Length;
          }
          return new WireMessage
          {
              Type = "clip", Kind = "files", Files = entries,
              Hash = Hashing.AggregateFilesHash(hashes), Ts = ts, Bytes = total,
          };
      }
  ```
  (d) In the `Clip` switch (lines 74-80), add the `FilesClip` arm before the `_ =>` fallthrough (this is how `SendClipAsync` gains FilesClip support — no edit to `SendClipAsync` itself):
  ```csharp
          FileClip f => ClipFile(f.Name, f.Data, ts),
          FilesClip fs => ClipFiles(fs.Files, ts),
          _ => throw new ArgumentOutOfRangeException(nameof(payload)),
  ```

- [ ] **Step 14: Run tests to verify they pass** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter ClipFiles`
  Expected: FAIL to compile — `The type or namespace name 'FilesClip' could not be found` (the `Clip` switch arm references `FilesClip`, added next). Add `FilesClip` in Step 15 before re-running.

- [ ] **Step 15: Add `FilesClip` payload** — In `ClipPayload.cs`, after `FileClip` (ends line 25):
  ```csharp
  public sealed record FilesClip(IReadOnlyList<(string Name, byte[] Data)> Files) : ClipPayload
  {
      public override string Kind => "files";
      public override string PayloadHash =>
          Hashing.AggregateFilesHash(Files.Select(f => Hashing.Sha256Hex(f.Data)));
  }
  ```

- [ ] **Step 16: Write the failing test for `FilesClip` payload** — Add to `WireMessageTests.cs`:
  ```csharp
  [Fact]
  public void FilesClipKindAndAggregateHash()
  {
      var f = new FilesClip(new List<(string, byte[])>
      {
          ("a", "one"u8.ToArray()),
          ("b", "two"u8.ToArray()),
      });
      Assert.Equal("files", f.Kind);
      Assert.Equal(Hashing.AggregateFilesHash(new[]
      {
          Hashing.Sha256Hex("one"u8.ToArray()),
          Hashing.Sha256Hex("two"u8.ToArray()),
      }), f.PayloadHash);
  }
  ```

- [ ] **Step 17: Run tests to verify they pass** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter "ClipFiles|FilesClip"`
  Expected: PASS (4 tests: `ClipFilesEncodesEntriesAndAggregateInContractOrder`, `ClipFilesRoundTripsThroughDecode`, `ClipFilesNormalizesEachNameToNFC`, `FilesClipKindAndAggregateHash`).

- [ ] **Step 18: Commit** — `git add forwindows/src/AnyClipCore/WireMessage.cs forwindows/src/AnyClipCore/ClipPayload.cs forwindows/tests/AnyClipCore.Tests/WireMessageTests.cs`
  ```
  feat(win): wire kind "files" (WireFileEntry, ClipFiles, FilesClip)

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 19: Write the failing sanitizer/uniquify tests** — In `PureLogicTests.cs`: first update the now-wrong assertion in `SanitizeFilenameMatchesPython` (line 185) — `!` is no longer denylisted, only `:` and `?` become `_`:
  ```csharp
  // was: Assert.Equal("we_rd_na_me", TextHelpers.SanitizeFilename("we!rd:na?me"));
  Assert.Equal("we!rd_na_me", TextHelpers.SanitizeFilename("we!rd:na?me"));
  ```
  Then add these new methods inside the `TextHelpersTests` class (before its closing brace, ~line 217):
  ```csharp
  [Fact]
  public void SanitizeFilenamePreservesParensAmpersandAndKorean()
  {
      // The old alnum-whitelist mangled ( & ) to underscores; the denylist keeps them.
      Assert.Equal("(E&S)_SCM 마스터플랜_20250915_공유6.pptx",
          TextHelpers.SanitizeFilename("(E&S)_SCM 마스터플랜_20250915_공유6.pptx"));
  }

  [Fact]
  public void SanitizeFilenameStripsTraversalAndDenylistChars()
  {
      Assert.Equal("passwd", TextHelpers.SanitizeFilename("../../etc/passwd"));
      Assert.Equal("received.bin", TextHelpers.SanitizeFilename(".."));
      Assert.Equal("received.bin", TextHelpers.SanitizeFilename("."));
      Assert.Equal("x_y_z", TextHelpers.SanitizeFilename("x<y>z"));
      Assert.Equal("a_b_c_d_e_f", TextHelpers.SanitizeFilename("a\"b|c?d*e:f"));
      Assert.Equal("tab_here.txt", TextHelpers.SanitizeFilename("tab\there.txt")); // \t < U+0020
      Assert.Equal("del_.txt", TextHelpers.SanitizeFilename("del.txt"));      // U+007F
  }

  [Fact]
  public void SanitizeFilenameTrimsTrailingDotsAndSpaces()
  {
      Assert.Equal("report", TextHelpers.SanitizeFilename("report... "));
      Assert.Equal("a.txt", TextHelpers.SanitizeFilename("a.txt.  "));
      Assert.Equal(".gitignore", TextHelpers.SanitizeFilename(".gitignore")); // leading dot kept
  }

  [Fact]
  public void SanitizeFilenamePrefixesWindowsReservedNames()
  {
      Assert.Equal("_CON", TextHelpers.SanitizeFilename("CON"));
      Assert.Equal("_con.txt", TextHelpers.SanitizeFilename("con.txt"));   // case-insensitive
      Assert.Equal("_COM1.log", TextHelpers.SanitizeFilename("COM1.log"));
      Assert.Equal("_lpt9", TextHelpers.SanitizeFilename("lpt9"));
      Assert.Equal("com10.txt", TextHelpers.SanitizeFilename("com10.txt")); // NOT reserved
      Assert.Equal("console.txt", TextHelpers.SanitizeFilename("console.txt"));
  }

  [Fact]
  public void UniquifyNamesSuffixesCollisionsBeforeLastExtension()
  {
      Assert.Equal(new[] { "a.txt", "a (2).txt", "a (3).txt" },
          TextHelpers.UniquifyNames(new[] { "a.txt", "a.txt", "a.txt" }).ToArray());
      Assert.Equal(new[] { "note", "note (2)" },
          TextHelpers.UniquifyNames(new[] { "note", "note" }).ToArray());
      Assert.Equal(new[] { "a.txt", "b.txt" },
          TextHelpers.UniquifyNames(new[] { "a.txt", "b.txt" }).ToArray());
      Assert.Equal(new[] { "archive.tar.gz", "archive.tar (2).gz" },
          TextHelpers.UniquifyNames(new[] { "archive.tar.gz", "archive.tar.gz" }).ToArray());
      Assert.Equal(new[] { ".env", ".env (2)" },
          TextHelpers.UniquifyNames(new[] { ".env", ".env" }).ToArray());
      Assert.Equal(new[] { "a (2).txt", "a.txt", "a (3).txt" },
          TextHelpers.UniquifyNames(new[] { "a (2).txt", "a.txt", "a.txt" }).ToArray());
  }
  ```

- [ ] **Step 20: Run tests to verify they fail** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter TextHelpersTests`
  Expected: FAIL to compile — `'TextHelpers' does not contain a definition for 'UniquifyNames'`.

- [ ] **Step 21: Rewrite `SanitizeFilename` and add `UniquifyNames`** — In `TextHelpers.cs`, replace the whole `SanitizeFilename` method (lines 28-43; keep `ToNfc` lines 22-26 unchanged) with the denylist implementation plus the reserved-name set and the uniquifier:
  ```csharp
      private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
      {
          "CON", "PRN", "AUX", "NUL",
          "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
      };

      /// Cross-platform denylist sanitizer (receive side). Keep in lockstep with
      /// Swift sanitizeFilename and anyclip.update_local_file:
      /// NFC; basename; replace \ / < > : " | ? *, controls (< U+0020), U+007F;
      /// trim trailing dots/spaces; empty/./.. -> received.bin; Windows reserved
      /// device names (stem before first dot, case-insensitive) -> "_" prefix.
      public static string SanitizeFilename(string name)
      {
          name = ToNfc(name);
          int cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
          var basename = cut >= 0 ? name[(cut + 1)..] : name;
          var sb = new StringBuilder(basename.Length);
          foreach (var ch in basename)
              sb.Append(
                  ch is '\\' or '/' or '<' or '>' or ':' or '"' or '|' or '?' or '*'
                      || ch < ' ' || ch == ''
                  ? '_' : ch);
          var cleaned = sb.ToString().TrimEnd('.', ' ');
          if (cleaned.Length == 0 || cleaned == "." || cleaned == "..") return "received.bin";
          int dot = cleaned.IndexOf('.');
          var stem = dot >= 0 ? cleaned[..dot] : cleaned;
          if (ReservedDeviceNames.Contains(stem)) cleaned = "_" + cleaned;
          return cleaned;
      }

      /// De-duplicate already-sanitized names within one received batch:
      /// first wins, later dupes get " (2)", " (3)" before the LAST extension
      /// (no extension -> appended). Keep in lockstep with Swift/Python.
      public static IReadOnlyList<string> UniquifyNames(IReadOnlyList<string> names)
      {
          // First occurrence keeps its name; later duplicates get " (2)", " (3)"
          // before the LAST extension (a leading dot is not an extension:
          // ".env" -> ".env (2)"). Candidates colliding with an already-emitted
          // name are bumped further. Lockstep with Swift/Python.
          var used = new HashSet<string>(StringComparer.Ordinal);
          var result = new List<string>(names.Count);
          foreach (var name in names)
          {
              if (used.Add(name)) { result.Add(name); continue; }
              int dot = name.LastIndexOf('.');
              string stem = dot <= 0 ? name : name[..dot];
              string ext = dot <= 0 ? "" : name[dot..];
              int n = 2;
              string candidate = $"{stem} ({n}){ext}";
              while (!used.Add(candidate))
              {
                  n++;
                  candidate = $"{stem} ({n}){ext}";
              }
              result.Add(candidate);
          }
          return result;
      }
  ```

- [ ] **Step 22: Run tests to verify they pass** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter TextHelpersTests`
  Expected: PASS. `SanitizeFilenameMatchesPython`, `SanitizeFilenameNormalizesDecomposedUnicodeToNFC`, `SanitizeFilenameDoesNotThrowOnInvalidUnicode` (existing) plus the 5 new methods all green.

- [ ] **Step 23: Commit** — `git add forwindows/src/AnyClipCore/TextHelpers.cs forwindows/tests/AnyClipCore.Tests/PureLogicTests.cs`
  ```
  feat(win): denylist filename sanitizer + batch uniquify

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 24: Write the failing PeerLink tests (files round-trip, minor retention, invalid/empty frame)** — Add to `PeerLinkTests.cs` (uses the file's existing `MakeLink`/`WaitUntil` helpers; ports 28641-28643 are unused elsewhere):
  ```csharp
  [Fact]
  public async Task TwoLinksExchangeFilesClipAndRetainPeerMinor()
  {
      var (a, aClips, _) = MakeLink("tok", 28641, "node-a");
      var (b, bClips, _) = MakeLink("tok", 28642, "node-b");
      using var cts = new CancellationTokenSource();
      var serveA = a.ServeAsync(cts.Token);
      Assert.True(await WaitUntil(() => a.IsServing));
      _ = b.TryConnectAsync("127.0.0.1", 28641, "127.0.0.1:28641", cts.Token);
      Assert.True(await WaitUntil(() => a.IsActive && b.IsActive));

      // Both peers are this build (minor 1); retained from the hello.
      Assert.Equal(1, a.PeerProtocolMinor);
      Assert.Equal(1, b.PeerProtocolMinor);

      await b.SendClipAsync(new FilesClip(new List<(string, byte[])>
      {
          ("노트.txt", "one"u8.ToArray()),
          ("réport.bin", new byte[] { 2, 3 }),
      }));
      Assert.True(await WaitUntil(() => { lock (aClips) return aClips.Any(c => c is FilesClip); }));
      FilesClip got;
      lock (aClips) got = aClips.OfType<FilesClip>().First();
      Assert.Equal(2, got.Files.Count);
      Assert.Equal("노트.txt", got.Files[0].Name);
      Assert.Equal("one"u8.ToArray(), got.Files[0].Data);
      Assert.Equal("réport.bin", got.Files[1].Name);
      Assert.Equal(new byte[] { 2, 3 }, got.Files[1].Data);

      cts.Cancel(); a.Shutdown(); b.Shutdown();
      try { await serveA; } catch (OperationCanceledException) { }
  }

  [Fact]
  public async Task FilesClipInvalidOrEmptyFrameIgnoredAndLinkStaysUp()
  {
      var (a, aClips, _) = MakeLink("tok", 28643, "a");
      using var cts = new CancellationTokenSource();
      var serveA = a.ServeAsync(cts.Token);
      Assert.True(await WaitUntil(() => a.IsServing));

      using var raw = await FramedConnection.ConnectAsync("127.0.0.1", 28643, 5, cts.Token);
      await raw.SendFrameAsync(WireMessage.Hello(
          Hashing.Sha256Hex("tok"), "ffffffff-raw", "raw", "0.0.0-test"), cts.Token);
      _ = await raw.ReceiveMessageAsync(cts.Token); // a's hello
      Assert.True(await WaitUntil(() => a.IsActive));

      // (1) empty files array -> ignored.
      await raw.SendFrameAsync(new WireMessage
      {
          Type = "clip", Kind = "files", Files = new List<WireFileEntry>(), Hash = "x", Ts = 1,
      }, cts.Token);
      // (2) one entry has non-strict base64 -> whole frame ignored.
      await raw.SendFrameAsync(new WireMessage
      {
          Type = "clip", Kind = "files",
          Files = new List<WireFileEntry>
          {
              new() { Name = "ok.txt", Content = Convert.ToBase64String("ok"u8.ToArray()),
                      Hash = "x", Bytes = 2 },
              new() { Name = "bad.txt", Content = "!!!not-base64!!!", Hash = "x", Bytes = 0 },
          },
          Hash = "x", Ts = 1,
      }, cts.Token);
      // (3) a valid 2-entry frame proves the link survived both bad frames.
      await raw.SendFrameAsync(WireMessage.ClipFiles(
          new List<(string, byte[])> { ("a.txt", "aa"u8.ToArray()), ("b.txt", "bb"u8.ToArray()) },
          1), cts.Token);

      Assert.True(await WaitUntil(() =>
      {
          lock (aClips) return aClips.OfType<FilesClip>().Any(f => f.Files.Count == 2);
      }));
      lock (aClips) Assert.DoesNotContain(aClips, c => c is FilesClip f && f.Files.Count != 2);
      Assert.True(a.IsActive);

      cts.Cancel(); a.Shutdown();
      try { await serveA; } catch (OperationCanceledException) { }
  }
  ```

- [ ] **Step 25: Run tests to verify they fail** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter "FilesClip|PeerMinor"`
  Expected: FAIL to compile — `'PeerLink' does not contain a definition for 'PeerProtocolMinor'`.

- [ ] **Step 26: Add `PeerProtocolMinor` retention + the `"files"` receive branch to `PeerLink.cs`** —
  (a) After `public string? PeerName { get; private set; }` (line 42):
  ```csharp
      public string? PeerName { get; private set; }
      public int PeerProtocolMinor { get; private set; }
  ```
  (b) In the registration critical section, after `PeerName = displayName;` (line 294):
  ```csharp
              PeerName = displayName;
              PeerProtocolMinor = peerVersion.ProtocolMinor;
  ```
  (c) In the teardown `finally`, after `PeerName = null;` (line 353):
  ```csharp
                      PeerName = null;
                      PeerProtocolMinor = 0;
  ```
  (d) In `Shutdown()`, after `PeerName = null;` (line 434):
  ```csharp
          PeerName = null;
          PeerProtocolMinor = 0;
  ```
  (e) In `HandleClipAsync`, add the `"files"` case after the `"file"` case (line 382), before `default:` (line 383):
  ```csharp
              case "files":
                  if (msg.Files is null || msg.Files.Count == 0)
                  {
                      RotatingLog.Shared.Warning("ignoring files clip with no entries");
                      break;
                  }
                  var decoded = new List<(string Name, byte[] Data)>(msg.Files.Count);
                  bool bad = false;
                  foreach (var entry in msg.Files)
                  {
                      if (entry.Content is null ||
                          WireMessage.StrictBase64Decode(entry.Content) is not { } fbytes)
                      {
                          RotatingLog.Shared.Warning("bad file payload in files clip; ignoring frame");
                          bad = true;
                          break;
                      }
                      var fname = string.IsNullOrEmpty(entry.Name) ? "received.bin" : entry.Name!;
                      decoded.Add((fname, fbytes)); // hash NOT trusted from wire; recomputed downstream
                  }
                  if (!bad)
                      await (OnClip?.Invoke(new FilesClip(decoded)) ?? Task.CompletedTask);
                  break;
  ```

- [ ] **Step 27: Run tests to verify they pass** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter "FilesClipInvalidOrEmptyFrameIgnoredAndLinkStaysUp|TwoLinksExchangeFilesClipAndRetainPeerMinor"`
  Expected: PASS (2 tests).

- [ ] **Step 28: Commit** — `git add forwindows/src/AnyClipCore/PeerLink.cs forwindows/tests/AnyClipCore.Tests/PeerLinkTests.cs`
  ```
  feat(win): PeerLink retains peer minor + decodes kind "files"

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 29: Add the golden-vector test** — Add to `GoldenVectorTests.cs` (uses the file's existing `DecodeGolden`/`Manifest`/`Fixture` helpers). This locks the C# decoder against the byte-exact `clip_files.bin` and extended `manifest.json` produced by Task 5:
  ```csharp
  [Fact]
  public void GoldenClipFilesDecodes()
  {
      var m = DecodeGolden("clip_files.bin");
      var man = Manifest();
      Assert.Equal("files", m.Kind);
      Assert.NotNull(m.Files);
      var names = man.GetProperty("files_names").EnumerateArray()
          .Select(e => e.GetString()).ToArray();
      var hashes = man.GetProperty("files_hashes").EnumerateArray()
          .Select(e => e.GetString()!).ToArray();
      Assert.Equal(names.Length, m.Files!.Count);
      for (int i = 0; i < m.Files.Count; i++)
      {
          var entry = m.Files[i];
          Assert.Equal(names[i], entry.Name);
          var data = WireMessage.StrictBase64Decode(entry.Content!)!;
          Assert.Equal(hashes[i], Hashing.Sha256Hex(data)); // recomputed == manifest
          Assert.Equal(hashes[i], entry.Hash);               // wire hash == manifest
          Assert.Equal(entry.Bytes, data.Length);
      }
      Assert.Equal(man.GetProperty("files_aggregate").GetString(),
          Hashing.AggregateFilesHash(hashes));
      Assert.Equal(man.GetProperty("files_aggregate").GetString(), m.Hash);
      Assert.Equal(man.GetProperty("files_total_bytes").GetInt32(), m.Bytes);
  }
  ```

- [ ] **Step 30: Run the golden test** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter GoldenClipFilesDecodes`
  Expected once Task 5 has committed the fixture + manifest keys: PASS (1 test). If Task 5 has NOT landed yet, this test throws `FileNotFoundException: clip_files.bin` (or a `KeyNotFoundException` on `files_names`) — that is the dependency signal, not a logic bug; do not "fix" it here, land Task 5 first. (The reference `files_aggregate` for the CONTRACT entries is `7d04c7a5d04332ff7e657a1046d2e3c22e808f5fccba7dbf321d9738dcb2979c` and `files_total_bytes` is `38`.)

- [ ] **Step 31: Commit** — `git add forwindows/tests/AnyClipCore.Tests/GoldenVectorTests.cs`
  ```
  test(win): golden-vector assert for clip_files.bin

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 32: Full core suite green** — `dotnet test forwindows/tests/AnyClipCore.Tests`
  Expected: all tests PASS (interop tests in this project are covered in Task 10). Commit nothing; this is a gate.

---

### Task 10: C# interop — files clip both directions

**Files:**
- Test: `forwindows/tests/AnyClipCore.Tests/InteropTests.cs` (verified: `RepoRoot` line 10-11, `ReadShared` lines 20-26, single-file spawn pattern lines 28-110)

**Interfaces:**
- Consumes: `PeerLink` + `FilesClip` (Task 9); `fake_peer.py --send-files` flag (Task 8) — after handshake sends exactly one kind:"files" clip with entries in order `("노트.txt", b"multi body one")`, `("(E&S) plan.txt", b"multi body two")`, then continues its record-frames loop (per CONTRACT FAKE PEER).
- Produces: none (test-only).

- [ ] **Step 1: Write the failing send-direction test** — Add to `InteropTests.cs` (mirrors `InteropWithPythonFakePeer` lines 28-110; ports 28633/28634 are unused):
  ```csharp
  [Fact]
  public async Task InteropSendsFilesClipToPythonPeer()
  {
      int port = 28633;
      string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
      var psi = new ProcessStartInfo
      {
          FileName = "python3",
          ArgumentList =
          {
              Path.Combine(RepoRoot(), "formacOS", "Scripts", "fake_peer.py"),
              "--port", port.ToString(),
              "--token", "interop-token",
              "--out", outFile,
          },
          RedirectStandardOutput = true,
      };
      using var proc = Process.Start(psi)!;
      try
      {
          var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
          Assert.Equal("READY", ready);

          var link = new PeerLink(
              new PeerLink.LinkConfig("interop-token", 28634, "csharp-interop", "0.0.0-test"),
              Guid.NewGuid().ToString().ToLowerInvariant());
          link.OnClip = _ => Task.CompletedTask;
          link.Emit = _ => { };
          using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
          _ = link.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);

          async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
          {
              var deadline = DateTime.UtcNow.AddSeconds(seconds);
              while (DateTime.UtcNow < deadline) { if (cond()) return true; await Task.Delay(50); }
              return cond();
          }
          Assert.True(await WaitUntil(() => link.IsActive));

          await link.SendClipAsync(new FilesClip(new List<(string, byte[])>
          {
              ("노트.txt", "multi body one"u8.ToArray()),
              ("(E&S) plan.txt", "multi body two"u8.ToArray()),
          }));

          Assert.True(await WaitUntil(() =>
          {
              if (!File.Exists(outFile)) return false;
              var lines = ReadShared(outFile);
              return lines.Contains("\"kind\": \"files\"")
                  && lines.Contains("노트.txt")
                  && lines.Contains("(E&S) plan.txt");
          }));
          link.Shutdown();
      }
      finally { if (!proc.HasExited) proc.Kill(); }
  }
  ```

- [ ] **Step 2: Run to verify it passes** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter InteropSendsFilesClipToPythonPeer`
  Expected: PASS (1 test). fake_peer's generic `record("recv", clipped)` writes the frame verbatim (`ensure_ascii=False`, `": "` separators), so `"kind": "files"` and both NFC names appear in the JSONL. (Requires `python3` on PATH — CI installs it on the .NET jobs; this file already depends on it.)

- [ ] **Step 3: Commit** — `git add forwindows/tests/AnyClipCore.Tests/InteropTests.cs`
  ```
  test(win): interop send kind "files" to python fake peer

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 4: Write the failing receive-direction test** — Add to `InteropTests.cs` (ports 28635/28636 unused; passes `--send-files` from Task 8):
  ```csharp
  [Fact]
  public async Task InteropReceivesFilesClipFromPythonPeer()
  {
      int port = 28635;
      string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
      var psi = new ProcessStartInfo
      {
          FileName = "python3",
          ArgumentList =
          {
              Path.Combine(RepoRoot(), "formacOS", "Scripts", "fake_peer.py"),
              "--port", port.ToString(),
              "--token", "interop-token",
              "--out", outFile,
              "--send-files",
          },
          RedirectStandardOutput = true,
      };
      using var proc = Process.Start(psi)!;
      try
      {
          var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
          Assert.Equal("READY", ready);

          var clips = new List<ClipPayload>();
          var link = new PeerLink(
              new PeerLink.LinkConfig("interop-token", 28636, "csharp-interop", "0.0.0-test"),
              Guid.NewGuid().ToString().ToLowerInvariant());
          link.OnClip = p => { lock (clips) clips.Add(p); return Task.CompletedTask; };
          link.Emit = _ => { };
          using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
          _ = link.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);

          async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
          {
              var deadline = DateTime.UtcNow.AddSeconds(seconds);
              while (DateTime.UtcNow < deadline) { if (cond()) return true; await Task.Delay(50); }
              return cond();
          }
          Assert.True(await WaitUntil(() => link.IsActive));
          Assert.True(await WaitUntil(() =>
          {
              lock (clips) return clips.OfType<FilesClip>().Any(f => f.Files.Count == 2);
          }));

          FilesClip got;
          lock (clips) got = clips.OfType<FilesClip>().First(f => f.Files.Count == 2);
          Assert.Equal("노트.txt", got.Files[0].Name);
          Assert.Equal("multi body one", System.Text.Encoding.UTF8.GetString(got.Files[0].Data));
          Assert.Equal("(E&S) plan.txt", got.Files[1].Name);
          Assert.Equal("multi body two", System.Text.Encoding.UTF8.GetString(got.Files[1].Data));
          link.Shutdown();
      }
      finally { if (!proc.HasExited) proc.Kill(); }
  }
  ```

- [ ] **Step 5: Run to verify it passes** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter InteropReceivesFilesClipFromPythonPeer`
  Expected once Task 8's `--send-files` flag exists: PASS (1 test). If Task 8 has not landed, fake_peer exits with `error: unrecognized arguments: --send-files` and the test fails at the `READY` read — land Task 8 first.

- [ ] **Step 6: Commit** — `git add forwindows/tests/AnyClipCore.Tests/InteropTests.cs`
  ```
  test(win): interop receive kind "files" from python fake peer

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

---

### Task 11: C# app layer — multi-grab, greedy filter, multi-place, old-peer fallback wiring

**Files:**
- Modify: `forwindows/src/AnyClipApp/ClipboardWatcher.cs` (verified: interface lines 11-19; `GetFirstFilePath` 55-60; `SetFilePath` 77-86; `FileBudget` 96-97; `_lastFileFingerprint` 109; ctor seeding 128; `Fingerprint` 232-247; `CheckFileClipboardAsync` 249-305; `ApplyRemoteAsync` 308-341)
- Modify: `forwindows/src/AnyClipCore/Daemon.cs` (verified: `OnClip` 111-136; `OnLocalChange` 138-163)
- Test: `forwindows/tests/AnyClipCore.Tests/DaemonTests.cs` (verified: `FakeClipboard`/`FakeMdns`/`FakePidLock` lines 6-51; `SyncCoordinatorSuppressesEcho` 55-63) — runs on macOS
- Test: `forwindows/tests/AnyClipApp.Tests/ClipboardLogicTests.cs` (verified: `FakeClipboard` lines 7-23, `Make`/`TempDir` 27-46) — Windows-only CI

**Interfaces:**
- Consumes: `FilesClip`, `TextHelpers.SanitizeFilename`/`UniquifyNames`, `PeerLink.PeerProtocolMinor`, `Hashing.AggregateFilesHash`/`Sha256Hex` (Task 9); `Daemon`/`FakeClipboard`/`FramedConnection`/`WireMessage.Hello`/`LinkUp` (existing).
- Produces:
  - `IWin32Clipboard.GetFilePaths()` → `IReadOnlyList<string>?` (replaces `GetFirstFilePath`)
  - `IWin32Clipboard.SetFilePaths(IReadOnlyList<string> paths)` → `bool` (replaces `SetFilePath`)
  - `ClipboardWatcher.MaxFilesPerClip` const int = 100

- [ ] **Step 1: Write the failing Daemon-level tests (macOS)** — Add to `DaemonTests.cs`. First a suppressor rule test, then the old-peer downgrade integration test. Add the retry helper too (the raw peer may race the listener bind):
  ```csharp
  [Fact]
  public void SyncCoordinatorSuppressesFilesAggregateAndSingleFile()
  {
      var c = new SyncCoordinator();
      var agg = Hashing.AggregateFilesHash(new[]
      {
          Hashing.Sha256Hex("one"u8.ToArray()),
          Hashing.Sha256Hex("two"u8.ToArray()),
      });
      c.MarkReceived("files", agg);
      Assert.False(c.ShouldSend("files", agg));        // echo of just-received set suppressed
      Assert.True(c.ShouldSend("files", "other-agg"));  // a different set still sends
      var h = Hashing.Sha256Hex("solo"u8.ToArray());
      c.MarkReceived("file", h);                        // 1-placed receive rule also marks "file"
      Assert.False(c.ShouldSend("file", h));
  }

  private static async Task<FramedConnection> ConnectWithRetry(int port, CancellationToken ct)
  {
      for (int i = 0; ; i++)
      {
          try { return await FramedConnection.ConnectAsync("127.0.0.1", port, 5, ct); }
          catch when (i < 40) { await Task.Delay(100, ct); }
      }
  }

  [Fact]
  public async Task OldPeerDowngradesFilesClipToFirstFileWithNotification()
  {
      var stateDir = Path.Combine(Path.GetTempPath(), "anyclip-downgrade-" + Guid.NewGuid());
      var clip = new FakeClipboard();
      var notes = new List<string>();
      var daemon = new Daemon(
          new DaemonConfig("dg-token", 28625, "dg", NotificationsEnabled: true),
          appVersion: "0.0.0-test", stateDir: stateDir,
          clipboard: clip, mdns: new FakeMdns(), pidLock: new FakePidLock(),
          primaryIPv4: () => "127.0.0.1",
          notify: (_, body) => { lock (notes) notes.Add(body); }, onFatal: _ => { });

      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
      var run = daemon.RunForeverAsync(cts.Token);

      using var raw = await ConnectWithRetry(28625, cts.Token);
      var oldHello = WireMessage.Hello(
          Hashing.Sha256Hex("dg-token"), "ffffffff-old", "old-peer", "1.0.0")
          with { ProtocolMinor = 0 };
      await raw.SendFrameAsync(oldHello, cts.Token);
      _ = await raw.ReceiveMessageAsync(cts.Token); // daemon hello

      // Wait for the link to register (guarantees link.IsActive before we fire).
      async Task<bool> WaitForLinkUp(double seconds = 10)
      {
          using var to = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
          try
          {
              while (await daemon.Events.WaitToReadAsync(to.Token))
                  while (daemon.Events.TryRead(out var ev))
                      if (ev is LinkUp) return true;
          }
          catch (OperationCanceledException) { }
          return false;
      }
      Assert.True(await WaitForLinkUp());
      Assert.NotNull(clip.OnLocalChange);

      // Simulate a local 2-file copy through the daemon's wired handler.
      await clip.OnLocalChange!(new FilesClip(new List<(string, byte[])>
      {
          ("first.txt", "one"u8.ToArray()),
          ("second.txt", "two"u8.ToArray()),
      }));

      // Old peer receives a legacy single "file" clip (the first file).
      var got = await raw.ReceiveMessageAsync(cts.Token);
      Assert.Equal("clip", got!.Type);
      Assert.Equal("file", got.Kind);
      Assert.Equal("first.txt", got.Name);

      async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
      {
          var deadline = DateTime.UtcNow.AddSeconds(seconds);
          while (DateTime.UtcNow < deadline) { if (cond()) return true; await Task.Delay(50); }
          return cond();
      }
      Assert.True(await WaitUntil(() => { lock (notes) return notes.Any(n => n.Contains("1 file")); }));

      cts.Cancel();
      try { await run; } catch (OperationCanceledException) { }
  }
  ```

- [ ] **Step 2: Run to verify they fail** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter "SyncCoordinatorSuppressesFilesAggregateAndSingleFile|OldPeerDowngrades"`
  Expected: `SyncCoordinator...` PASSES already (mechanism exists). `OldPeerDowngrades...` FAILS: the daemon has no old-peer gate yet, so the peer receives a raw kind:"files" frame — `Assert.Equal("file", got.Kind)` fails with `Expected: file, Actual: files`.

- [ ] **Step 3: Add the `"files"` branches to `Daemon.cs`** —
  (a) In `link.OnClip` (the `switch (payload)` at lines 116-135), add before its closing brace (after the `FileClip f:` case). The generic `coordinator.MarkReceived(payload.Kind, payload.PayloadHash)` at line 113 already records ("files", aggregate); this adds the single-placed "file" mark:
  ```csharp
                  case FilesClip fsc:
                      // MarkReceived above recorded ("files", aggregate). A single
                      // placed file re-detects as a legacy "file" clip; suppress that
                      // too. (Windows places all N; N==1 only for a lenient 1-entry frame.)
                      if (fsc.Files.Count == 1)
                          coordinator.MarkReceived("file", Hashing.Sha256Hex(fsc.Files[0].Data));
                      RotatingLog.Shared.Info(
                          $"<- received {fsc.Files.Count} files from {peer} "
                          + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                      toast($"AnyClip ← {peer}", $"{fsc.Files.Count} files");
                      break;
  ```
  (b) In `clipboard.OnLocalChange` (lines 138-163): insert the old-peer downgrade between the `ShouldSend` guard (ends line 145) and `await link.SendClipAsync(payload);` (line 146):
  ```csharp
              // Old-peer fallback: a peer on protocol minor 0 can't parse a
              // "files" clip. Downgrade to the first file as a legacy "file" clip
              // and report the dropped count via the skip-notification path.
              if (payload is FilesClip multi && link.PeerProtocolMinor < 1)
              {
                  int dropped = multi.Files.Count - 1;
                  var (fname, fdata) = multi.Files[0];
                  payload = new FileClip(fname, fdata);
                  if (!coordinator.ShouldSend(payload.Kind, payload.PayloadHash))
                  {
                      RotatingLog.Shared.Debug("skip echo of just-received file (old-peer downgrade)");
                      return;
                  }
                  RotatingLog.Shared.Info(
                      $"peer protocol minor {link.PeerProtocolMinor} < 1: sending 1 of "
                      + $"{multi.Files.Count} files, {dropped} dropped");
                  if (dropped > 0)
                      _ = clipboard.OnFileSkipped?.Invoke(
                          $"{dropped} file(s) not synced — update the peer's AnyClip for multi-file sync");
              }
  ```
  (c) In the same handler's `switch (payload)` toast block (after the `FileClip f:` case, before its closing brace at line 162), add:
  ```csharp
                  case FilesClip fsc:
                      RotatingLog.Shared.Info($"-> sent {fsc.Files.Count} files to {peer}");
                      toast($"AnyClip → {peer}", $"{fsc.Files.Count} files");
                      break;
  ```

- [ ] **Step 4: Run to verify they pass** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter "SyncCoordinatorSuppressesFilesAggregateAndSingleFile|OldPeerDowngrades"`
  Expected: PASS (2 tests).

- [ ] **Step 5: Commit** — `git add forwindows/src/AnyClipCore/Daemon.cs forwindows/tests/AnyClipCore.Tests/DaemonTests.cs`
  ```
  feat(win): daemon gates kind "files" on peer minor + suppressor

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 6: Migrate the app interface + `WinFormsClipboard` to multi-path** — In `ClipboardWatcher.cs`:
  (a) Interface (lines 11-19) — replace the two file lines:
  ```csharp
      IReadOnlyList<string>? GetFilePaths();
  ```
  ```csharp
      bool SetFilePaths(IReadOnlyList<string> paths);
  ```
  (b) `WinFormsClipboard.GetFirstFilePath` (lines 55-60) → `GetFilePaths`:
  ```csharp
      public IReadOnlyList<string>? GetFilePaths() => OnSta<IReadOnlyList<string>?>(() =>
      {
          if (!Clipboard.ContainsFileDropList()) return null;
          var list = Clipboard.GetFileDropList();
          if (list.Count == 0) return null;
          var result = new List<string>(list.Count);
          foreach (var p in list) if (p is not null) result.Add(p);
          return result.Count > 0 ? result : null;
      });
  ```
  (c) `WinFormsClipboard.SetFilePath` (lines 77-86) → `SetFilePaths`:
  ```csharp
      public bool SetFilePaths(IReadOnlyList<string> paths) => OnSta(() =>
      {
          try
          {
              var sc = new System.Collections.Specialized.StringCollection();
              foreach (var p in paths) sc.Add(p);
              Clipboard.SetFileDropList(sc);
              return true;
          }
          catch (Exception) { return false; }
      });
  ```

- [ ] **Step 7: Convert the watcher's fingerprint state to an ordered list** — In `ClipboardWatcher.cs`:
  (a) After `FileBudget` (lines 96-97), add the file-count cap:
  ```csharp
      public const int MaxFilesPerClip = 100; // sender-side cap; receiver stays lenient
  ```
  (b) Field at line 109 — replace:
  ```csharp
      private List<(string Path, long Size, long MTimeTicks)> _lastFileFingerprints = new();
  ```
  (c) Constructor seeding at line 128 — replace:
  ```csharp
          if (SafeRead(clipboard.GetFilePaths) is { } paths)
              _lastFileFingerprints = FingerprintList(paths);
  ```
  (d) After the `Fingerprint` method (ends line 247), add the list helper:
  ```csharp
      private static List<(string Path, long Size, long MTimeTicks)> FingerprintList(
          IReadOnlyList<string> paths)
      {
          var list = new List<(string Path, long Size, long MTimeTicks)>(paths.Count);
          foreach (var p in paths)
              if (Fingerprint(p) is { } fp) list.Add(fp);
          return list;
      }
  ```

- [ ] **Step 8: Rewrite `CheckFileClipboardAsync` for multi-file grab + greedy filter** — Replace the whole method (lines 249-305) with:
  ```csharp
      private async Task CheckFileClipboardAsync()
      {
          var paths = SafeRead(_clipboard.GetFilePaths);
          if (paths is null || paths.Count == 0) return;
          var fps = FingerprintList(paths);
          if (fps.Count == 0 || fps.SequenceEqual(_lastFileFingerprints)) return;
          // Record FIRST — the fingerprint is always taken (even if everything is
          // skipped) so nothing retry-loops.
          _lastFileFingerprints = fps;

          // Split folders (skip + notify) from files.
          var files = new List<string>();
          foreach (var p in paths)
          {
              if (Directory.Exists(p))
              {
                  var display = Path.GetFileName(p.TrimEnd('/', '\\'));
                  if (string.IsNullOrEmpty(display)) display = p; // drive roots
                  RotatingLog.Shared.Warning($"folder on clipboard not synced (unsupported): {p}");
                  await SafeSkipAsync($"folder not synced — folders are not supported: {display}");
              }
              else files.Add(p);
          }

          // Greedy: keep files in selection order while sum(raw) <= budget and
          // count <= cap. Skipped (too large, over-cap, or unreadable) -> one toast.
          var sendable = new List<(string Name, byte[] Data)>();
          long cumulative = 0;
          int skipped = 0;
          foreach (var path in files)
          {
              if (sendable.Count >= MaxFilesPerClip) { skipped++; continue; }
              long size;
              try { size = new FileInfo(path).Length; }
              catch (Exception e) when (e is IOException or UnauthorizedAccessException)
              {
                  RotatingLog.Shared.Warning($"file stat failed for {path}: {e.Message}; skipping");
                  skipped++; continue;
              }
              if (cumulative + size > FileBudget) { skipped++; continue; }
              byte[] data;
              try { data = await File.ReadAllBytesAsync(path); }
              catch (Exception e) when (e is IOException or UnauthorizedAccessException)
              {
                  RotatingLog.Shared.Warning($"file read failed for {path}: {e.Message}; skipping");
                  skipped++; continue;
              }
              cumulative += size;
              sendable.Add((Path.GetFileName(path), data));
          }
          if (skipped > 0)
              await SafeSkipAsync($"{skipped} file(s) skipped (too large to sync)");

          if (sendable.Count == 0) return;
          ClipPayload payload = sendable.Count == 1
              ? new FileClip(sendable[0].Name, sendable[0].Data)
              : new FilesClip(sendable);
          try { await (OnLocalChange?.Invoke(payload) ?? Task.CompletedTask); }
          catch (Exception e)
          { RotatingLog.Shared.Error($"on_change(files) handler failed: {e}"); }
      }

      private async Task SafeSkipAsync(string message)
      {
          try { await (OnFileSkipped?.Invoke(message) ?? Task.CompletedTask); }
          catch (Exception e)
          { RotatingLog.Shared.Error($"on_file_skipped handler failed: {e}"); }
      }
  ```
  (This removes the last uses of the `_oversizeWarned` field; delete its declaration at line 110 — `private bool _oversizeWarned;` — since the fingerprint-list gate already dedupes per selection.)

- [ ] **Step 9: Rewrite `ApplyRemoteAsync`'s file paths for multi-place** — In `ApplyRemoteAsync` (lines 308-341): replace the `FileClip f:` case body's two changed lines and add a `FilesClip` case before `default:`. New `FileClip` case:
  ```csharp
              case FileClip f:
                  try
                  {
                      Directory.CreateDirectory(_receivedDir);
                      string target = Path.Combine(_receivedDir, TextHelpers.SanitizeFilename(f.Name));
                      File.WriteAllBytes(target, f.Data);
                      _lastFileFingerprints = FingerprintList(new[] { target });
                      bool fileOk = _clipboard.SetFilePaths(new[] { target });
                      if (!fileOk) RotatingLog.Shared.Warning("clipboard write (file) failed");
                      return Task.FromResult(fileOk);
                  }
                  catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                  {
                      RotatingLog.Shared.Warning($"file write to {_receivedDir} failed: {e.Message}");
                      return Task.FromResult(false);
                  }
              case FilesClip fs:
                  try
                  {
                      Directory.CreateDirectory(_receivedDir);
                      var sanitized = TextHelpers.UniquifyNames(
                          fs.Files.Select(x => TextHelpers.SanitizeFilename(x.Name)).ToList());
                      var placed = new List<string>(fs.Files.Count);
                      for (int i = 0; i < fs.Files.Count; i++)
                      {
                          string target = Path.Combine(_receivedDir, sanitized[i]);
                          File.WriteAllBytes(target, fs.Files[i].Data);
                          placed.Add(target);
                      }
                      // Baseline to the paths actually PLACED on the clipboard.
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

- [ ] **Step 10: Verify the App project cross-compiles (macOS)** — `dotnet build forwindows/src/AnyClipApp`
  Expected: `Build succeeded. 0 Error(s)` (net8.0-windows via `EnableWindowsTargeting`). `Program.cs` only constructs `WinFormsClipboard`/`ClipboardWatcher` (lines 57-59) and never called the renamed methods, so no other call site breaks. If it errors on `GetFirstFilePath`/`SetFilePath`, a reference was missed — fix before continuing.

- [ ] **Step 11: Commit** — `git add forwindows/src/AnyClipApp/ClipboardWatcher.cs`
  ```
  feat(win): watcher multi-file grab, greedy budget, multi-place

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 12: Update the Windows-only test fake and existing call sites** — In `ClipboardLogicTests.cs`, replace the `FakeClipboard` (lines 7-23) file members to match the new interface:
  ```csharp
  internal sealed class FakeClipboard : IWin32Clipboard
  {
      public string? Text;
      public byte[]? ImagePng;
      public List<string>? FilePaths;
      public bool ThrowOnRead; // simulates CLIPBRD_E_CANT_OPEN lock contention
      public List<string> Written = new();
      public string? GetText() =>
          ThrowOnRead ? throw new InvalidOperationException("clipboard locked") : Text;
      public byte[]? GetImagePng() =>
          ThrowOnRead ? throw new InvalidOperationException("clipboard locked") : ImagePng;
      public IReadOnlyList<string>? GetFilePaths() =>
          ThrowOnRead ? throw new InvalidOperationException("clipboard locked") : FilePaths;
      public bool SetText(string text) { Written.Add($"text:{text}"); Text = text; return true; }
      public bool SetImagePng(byte[] png) { Written.Add("image"); ImagePng = png; return true; }
      public bool SetFilePaths(IReadOnlyList<string> paths)
      { Written.Add($"files:{string.Join(";", paths)}"); FilePaths = paths.ToList(); return true; }
  }
  ```
  Then update the three `clip.FilePath = X;` assignments to lists: in `FolderSkippedOnceWithToastAndFileSent` (lines 92 and 102), `OversizedFileSkipped` (line 114), and `OverlappingUpdatesSendFileOnlyOnce` (line 178) — each `clip.FilePath = X;` becomes `clip.FilePaths = new List<string> { X };`.

- [ ] **Step 13: Add the Windows-only watcher tests** — Append to `ClipboardLogicTests.cs` (inside `ClipboardLogicTests`):
  ```csharp
  [Fact]
  public async Task MultipleFilesEmitFilesClipWithAllEntries()
  {
      var (w, clip, changes, _) = Make(TempDir());
      var d = TempDir();
      var f1 = Path.Combine(d, "a.txt"); File.WriteAllText(f1, "aaa");
      var f2 = Path.Combine(d, "b.txt"); File.WriteAllText(f2, "bbbb");
      clip.FilePaths = new List<string> { f1, f2 };
      await w.HandleClipboardUpdateAsync();
      var fc = Assert.IsType<FilesClip>(Assert.Single(changes));
      Assert.Equal(new[] { "a.txt", "b.txt" }, fc.Files.Select(f => f.Name).ToArray());
      Assert.Equal("aaa", System.Text.Encoding.UTF8.GetString(fc.Files[0].Data));
  }

  [Fact]
  public async Task SingleSendableFileStillEmitsLegacyFileClip()
  {
      var (w, clip, changes, _) = Make(TempDir());
      var f1 = Path.Combine(TempDir(), "solo.txt"); File.WriteAllText(f1, "x");
      clip.FilePaths = new List<string> { f1 };
      await w.HandleClipboardUpdateAsync();
      Assert.IsType<FileClip>(Assert.Single(changes));
  }

  [Fact]
  public async Task GreedyBudgetDropsOversizeKeepsFittingFilesInOrder()
  {
      var (w, clip, changes, skipped) = Make(TempDir());
      var d = TempDir();
      var s1 = Path.Combine(d, "s1.txt"); File.WriteAllText(s1, "a");
      var s2 = Path.Combine(d, "s2.txt"); File.WriteAllText(s2, "b");
      var big = Path.Combine(d, "big.bin");
      using (var fs = File.Create(big)) fs.SetLength((long)ClipboardWatcher.FileBudget + 1);
      clip.FilePaths = new List<string> { s1, big, s2 }; // big in the middle
      await w.HandleClipboardUpdateAsync();
      var fc = Assert.IsType<FilesClip>(Assert.Single(changes));
      Assert.Equal(new[] { "s1.txt", "s2.txt" }, fc.Files.Select(f => f.Name).ToArray());
      Assert.Contains(skipped, s => s.Contains("1 file"));
  }

  [Fact]
  public async Task FolderMixedWithFilesSkipsFolderSyncsFiles()
  {
      var (w, clip, changes, skipped) = Make(TempDir());
      var d = TempDir();
      var folder = TempDir();
      var f1 = Path.Combine(d, "keep.txt"); File.WriteAllText(f1, "k");
      var f2 = Path.Combine(d, "keep2.txt"); File.WriteAllText(f2, "k2");
      clip.FilePaths = new List<string> { folder, f1, f2 };
      await w.HandleClipboardUpdateAsync();
      var fc = Assert.IsType<FilesClip>(Assert.Single(changes));
      Assert.Equal(2, fc.Files.Count);
      Assert.Contains(skipped, s => s.Contains("folders are not supported"));
  }

  [Fact]
  public async Task ApplyRemoteFilesClipWritesAllUniquifiesPlacesAllNoEcho()
  {
      var dir = TempDir();
      var (w, clip, changes, _) = Make(dir);
      var payload = new FilesClip(new List<(string, byte[])>
      {
          ("note.txt", "one"u8.ToArray()),
          ("note.txt", "two"u8.ToArray()),       // same sanitized name -> uniquified
          ("(E&S) plan.txt", "three"u8.ToArray()),
      });
      Assert.True(await w.ApplyRemoteAsync(payload));
      Assert.True(File.Exists(Path.Combine(dir, "note.txt")));
      Assert.True(File.Exists(Path.Combine(dir, "note (2).txt")));
      Assert.True(File.Exists(Path.Combine(dir, "(E&S) plan.txt")));
      Assert.Equal("three", File.ReadAllText(Path.Combine(dir, "(E&S) plan.txt")));
      Assert.Contains(clip.Written, x => x.StartsWith("files:")
          && x.Contains("note.txt") && x.Contains("note (2).txt") && x.Contains("(E&S) plan.txt"));
      // Baseline set to placed paths -> re-detect does not echo.
      await w.HandleClipboardUpdateAsync();
      Assert.Empty(changes);
  }
  ```

- [ ] **Step 14: Run the Windows-only watcher tests** — `dotnet test forwindows/tests/AnyClipApp.Tests --filter "MultipleFilesEmitFilesClipWithAllEntries|SingleSendableFileStillEmitsLegacyFileClip|GreedyBudgetDropsOversizeKeepsFittingFilesInOrder|FolderMixedWithFilesSkipsFolderSyncsFiles|ApplyRemoteFilesClipWritesAllUniquifiesPlacesAllNoEcho"`
  Expected on Windows CI: PASS (5 tests). This project targets `net8.0-windows` and only runs on the Windows CI job — it cannot execute on this macOS box (`dotnet test` here reports it cannot run Windows-targeted tests). Mark these CI-verified; the local gate for this change is the `dotnet build forwindows/src/AnyClipApp` in Step 10 plus the full `AnyClipApp.Tests` build.

- [ ] **Step 15: Verify the whole App test project still builds** — `dotnet build forwindows/tests/AnyClipApp.Tests`
  Expected: `Build succeeded. 0 Error(s)` (confirms the migrated `FakeClipboard` and all existing tests — `TextChangeFires…`, `FolderSkippedOnceWithToastAndFileSent`, `OversizedFileSkipped`, `ApplyRemoteWritesWithoutEcho`, `OverlappingUpdatesSendFileOnlyOnce` — compile against the new interface).

- [ ] **Step 16: Commit** — `git add forwindows/tests/AnyClipApp.Tests/ClipboardLogicTests.cs`
  ```
  test(win): watcher multi-file grab + multi-place (CI: Windows)

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  ```

- [ ] **Step 17: Final gate — full macOS-runnable suite** — `dotnet test forwindows/tests/AnyClipCore.Tests`
  Expected: all tests PASS (Task 9 + Task 10 + Task 11 Core/Daemon tests). No commit; this is the closing gate for the C# work. (`AnyClipApp.Tests` runs only on the Windows CI job.)

### Task 12: Docs — CLAUDE.md correction + README multi-file bullet

**Files:**
- Modify: `CLAUDE.md:24` and `CLAUDE.md:34` (⚠️ this file is currently **untracked** — update its content but do NOT `git add` it; whether to commit it is the user's call)
- Modify: `README.md` (after the `버전 협상` bullet in "How it works", currently line 152)

**Interfaces:**
- Consumes: nothing (docs only; do this after Tasks 1–11 so the docs describe shipped behavior)
- Produces: nothing

- [ ] **Step 1: Fix the incorrect omission claim in CLAUDE.md** — line 34 currently reads:

```
The native ports **deliberately omit**: Sparkle/WinSparkle auto-update, `--headless` CLI mode, multi-file/folder sync (and on Windows, the macOS Local Network permission probe). Those live only in the Python build.
```

Replace with:

```
The native ports **deliberately omit**: Sparkle/WinSparkle auto-update and `--headless` CLI mode (and on Windows, the macOS Local Network permission probe). Those live only in the Python build. Multi-file sync (wire kind `"files"`, protocol 1.1) is supported by all three implementations — except the Python build's macOS side, which sends first-file-only and places only the first received file on the clipboard (AppleScript clipboard limitations). Folder sync remains scoped out everywhere.
```

- [ ] **Step 2: Extend the CLAUDE.md wire-essentials line** — line 24 ends with:

```
protocol major/minor exchanged in the handshake (major mismatch → link refused).
```

Replace that ending with:

```
protocol major/minor exchanged in the handshake (major mismatch → link refused). Multi-file selections ship as a single `kind:"files"` frame; senders fall back to first-file `kind:"file"` for protocol 1.0 peers.
```

- [ ] **Step 3: Add the README bullet** — in `README.md`, directly after:

```
- **버전 협상**: handshake에서 양쪽이 protocol_major/minor를 교환해 메이저 불일치 시 link 거부 + 메뉴바 경고.
```

add:

```
- **여러 파일 동기화**: 파일을 2개 이상 복사하면 하나의 `kind:"files"` 프레임(프로토콜 1.1)으로 묶어 전송. 합계 예산(~12MB)을 넘거나 100개를 초과하는 파일은 건너뛰고 알림으로 표시. 상대가 프로토콜 1.0(구버전)이면 첫 파일만 전송. 폴더는 동기화하지 않음.
```

- [ ] **Step 4: Commit (README only)**

```bash
git add README.md
git commit -m "$(cat <<'EOF'
docs: document multi-file sync (kind "files", protocol 1.1)

CLAUDE.md updated in place but left uncommitted (file is untracked).

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 13: Full verification sweep

**Files:**
- No new files. Runs every suite that this plan touched and proves the golden vectors are stable.

**Interfaces:**
- Consumes: everything from Tasks 1–12.
- Produces: a green build; nothing new.

- [ ] **Step 1: Python suite**

Run: `source .venv/bin/activate && pytest tests/ -v`
Expected: all tests pass, including the new `test_wire_files.py`, `test_sanitize.py`, and the extended `test_clipboard_watcher.py`. No skips beyond any pre-existing ones.

- [ ] **Step 2: Golden-vector idempotency**

Run: `python3 formacOS/Scripts/gen-golden-vectors.py && git status --short formacOS/Tests/AnyClipCoreTests/Fixtures/`
Expected: no output from `git status --short` — regeneration is byte-identical to what Task 5 committed (old vectors untouched, `clip_files.bin` + `manifest.json` stable).

- [ ] **Step 3: Swift suite** (⚠️ may flip the live AnyClip menu-bar app into a false auth-error — restart AnyClip.app afterwards; see Global Constraints)

Run: `swift test --package-path formacOS`
Expected: all suites pass, including the new golden/aggregate/sanitize/uniquify tests, the watcher multi-file tests, and both new InteropTests directions.

- [ ] **Step 4: C# core suite (runs on macOS)**

Run: `dotnet test forwindows/tests/AnyClipCore.Tests`
Expected: `Passed!` — including `GoldenClipFilesDecodes`, the aggregate/sanitizer/uniquify tests, and both new interop directions.

- [ ] **Step 5: C# app cross-build (tests are Windows-CI-only)**

Run: `dotnet build forwindows/src/AnyClipApp && dotnet build forwindows/tests/AnyClipApp.Tests`
Expected: `Build succeeded.` for both. The five new watcher tests execute in the `windows-native` CI job, not locally.

- [ ] **Step 6: Dead-symbol check**

Run: `git grep -n "GetFirstFilePath\|grabFileURL\b" -- forwindows formacOS`
Expected: no matches — the renamed single-file grab symbols are fully gone.

- [ ] **Step 7: Confirm a clean tree**

Run: `git status --short`
Expected: only `?? CLAUDE.md` remains (deliberately uncommitted). Anything else means a task forgot to commit — fix that task's commit, do not batch-commit here.
