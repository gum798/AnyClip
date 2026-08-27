namespace AnyClip.Core;

/// Protocol constants — keep in lockstep with anyclip.py / formacOS.
public static class Wire
{
    /// 64 MiB hard cap per frame (fits a ~16 MB pptx).
    public const int MaxPayload = 64 * 1024 * 1024;
    /// The receive cap enforced by peers on protocol minor < 2: they CLOSE the
    /// session on a bigger frame, so the broadcast fan-out gates per link on
    /// this value rather than letting an oversize clip tear an old peer's link
    /// down. See LinkAcceptsFrame.
    public const int LegacyMaxPayload = 16 * 1024 * 1024;
    public const int ProtocolMajor = 1;
    /// Cumulative feature level: minor >= 1 accepts kind:"files", minor >= 2
    /// accepts frames up to MaxPayload (64 MiB) instead of LegacyMaxPayload,
    /// minor >= 3 rebuilds folder trees from the optional per-entry "path".
    /// Minor 3 is a capability MARKER only — it gates nothing on the send path.
    public const int ProtocolMinor = 3;
    public const int LegacyVersion = 1;
    public const int DefaultPort = 24816;
    public const string ServiceType = "_anyclip._tcp";
    public const double HandshakeTimeoutSeconds = 5.0;
    public const double ConnectTimeoutSeconds = 5.0;
    // BASE upper bound on a single app-initiated send. A write that parks past
    // the budget (full TCP buffer of a half-open/wedged peer) would otherwise
    // freeze the caller's loop -- the clipboard poll loop and the heartbeat
    // self-heal both await sends inline. On timeout the connection is dropped to
    // reconnect. The EFFECTIVE budget scales with the frame; see SendTimeoutFor.
    public const double SendTimeoutSeconds = 10.0;
    public const double RaceWindowSeconds = 1.5;
    public const int MaxReconnectFails = 3;

    /// Drain budget for a frame body of `bytes`: the base timeout plus one
    /// second per MiB (a 1 MiB/s floor). A fixed 10 s could not carry a 64 MiB
    /// frame over a slow LAN, and a timeout drops the connection.
    ///
    /// Invariant: worst case 64 MiB -> 10 + 64 = 74 s, which stays below the
    /// 90 s per-link staleness deadline (Watchdogs.LinkPingLoopAsync: 30 s ping
    /// x dead factor 3), so a legitimately slow big send can never be mistaken
    /// for a half-open link. Keep in lockstep with anyclip.send_timeout_for.
    public static double SendTimeoutFor(int bytes, double baseSeconds = SendTimeoutSeconds)
        => baseSeconds + bytes / (1024.0 * 1024.0);

    /// False when a frame body of `bytes` would breach the legacy 16 MiB receive
    /// cap that a peer on protocol minor < 2 still enforces. Such a peer closes
    /// the session on an over-cap frame, so the fan-out skips the send and KEEPS
    /// the link instead.
    public static bool LinkAcceptsFrame(int bytes, int peerMinor)
        => bytes <= LegacyMaxPayload || peerMinor >= 2;

    /// Receive-side frame-length guard: a body of 1...MaxPayload bytes. Peers on
    /// protocol minor < 2 apply this same rule against LegacyMaxPayload, which is
    /// exactly what LinkAcceptsFrame protects them from.
    public static bool AcceptsFrameLength(int n) => n > 0 && n <= MaxPayload;

    /// Folder-tree limits for the optional per-entry "path" (protocol 1.3).
    /// Keep in lockstep with anyclip.MAX_PATH_SEGMENTS / MAX_PATH_CHARS and
    /// Swift Wire.maxPathSegments / Wire.maxPathLength.
    public const int MaxPathSegments = 32;
    public const int MaxSanitizedPathLength = 240;

    /// True when `path` is a legal wire "path" for an entry named `name`:
    /// POSIX '/' separators, NFC, relative, no drive letter, no '.'/'..'/empty
    /// segment, no backslash, last segment == name, <= MaxPathSegments
    /// segments, sanitized total <= MaxSanitizedPathLength CODE POINTS.
    ///
    /// Senders MUST emit only paths that pass. Receivers MUST verify and fall
    /// back to FLAT placement for the failing ENTRY — never drop the frame.
    ///
    /// NFC IS A REJECTION RULE, not a normalization: a decomposed path is
    /// refused outright and that entry lands flat, exactly as
    /// anyclip.is_valid_wire_path does
    /// (`if path != unicodedata.normalize("NFC", path): return False`) and as
    /// Swift isValidWirePath does on utf8 bytes. The comparison MUST be
    /// ORDINAL — a culture-sensitive/canonical compare would call NFD equal to
    /// NFC and could not express the check at all — and it must survive
    /// ill-formed UTF-16 (NTFS permits unpaired surrogates), which makes
    /// string.IsNormalized/Normalize throw: such a path is INVALID, not fatal.
    ///
    /// LENGTH IS COUNTED IN CODE POINTS, matching Python's len(): string.Length
    /// counts UTF-16 units and would double-count an astral segment, so the
    /// same clip would rebuild a tree on one receiver and land flat here.
    /// Separator scans and the '/' split run on chars, which for ASCII
    /// separators is exactly a code-point scan (no ASCII char is ever half of
    /// a surrogate pair) — the equivalent of Swift's unicodeScalars scan, and
    /// unlike a grapheme-level scan a combining mark can never hide a '/'.
    ///
    /// Keep in lockstep with anyclip.is_valid_wire_path and Swift
    /// isValidWirePath (Python/Swift call this validator IsValidWirePath in
    /// their cross-reference comments; the C# name is IsValidRelPath).
    public static bool IsValidRelPath(string? path, string name)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!IsNfc(path)) return false;
        if (path.Contains('\\')) return false;                  // POSIX separators only
        if (path[0] == '/') return false;                       // must be relative
        // ASCII-only letter test, matching Swift. Python's str.isalpha() is
        // Unicode-wide, so "é:/a.txt" diverges — a recorded, flat-fallback-safe
        // minor (Task 4 ledger), not a new one.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return false;                                       // "C:/..." drive letter
        var segments = path.Split('/');
        if (segments.Length > MaxPathSegments) return false;
        foreach (var s in segments)
            if (s.Length == 0 || s == "." || s == "..") return false;
        // ORDINAL, never canonical: Python compares `segments[-1] != name`
        // exactly, so a composed path with a decomposed `name` must NOT match.
        if (!string.Equals(segments[^1], name, StringComparison.Ordinal))
            return false;
        int sanitized = -1;                                     // n segments -> n-1 separators
        foreach (var s in TextHelpers.SanitizePathSegments(path))
            sanitized += CodePointCount(s) + 1;
        return sanitized <= MaxSanitizedPathLength;
    }

    /// Ordinal "is this string already NFC?". IsNormalized throws
    /// ArgumentException on ill-formed UTF-16 — an unpaired surrogate is not a
    /// path we can reason about, so it is simply invalid.
    private static bool IsNfc(string s)
    {
        try { return s.IsNormalized(System.Text.NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }
    }

    /// Unicode code points (== Python len()), not UTF-16 code units.
    private static int CodePointCount(string s)
    {
        int n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }
}
