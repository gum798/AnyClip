using System.Text;
using System.Text.Json;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class WireMessageTests
{
    [Fact]
    public void Sha256MatchesPython()
    {
        Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            Hashing.Sha256Hex("hello"));
        Assert.Equal(
            "e8f817f346d1d411cc59d5bdda64fab3763890e1f0f8f4c15805cf78874d68bf",
            Hashing.Sha256Hex("안녕"));
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Hashing.Sha256Hex(Array.Empty<byte>()));
    }

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

    [Fact]
    public void FrameIsFourByteBigEndianLengthPlusJson()
    {
        var frame = WireMessage.Ping(1.5).EncodeFrame();
        int n = WireMessage.FrameLength(frame.AsSpan(0, 4).ToArray());
        Assert.Equal(frame.Length - 4, n);
        using var doc = JsonDocument.Parse(frame.AsSpan(4).ToArray());
        Assert.Equal("ping", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1.5, doc.RootElement.GetProperty("ts").GetDouble());
    }

    [Fact]
    public void HelloCarriesAllProtocolFieldsInSnakeCase()
    {
        var frame = WireMessage.Hello(
            Hashing.Sha256Hex("tok"), "node-1", "win", "1.2.3").EncodeFrame();
        using var doc = JsonDocument.Parse(frame.AsSpan(4).ToArray());
        var root = doc.RootElement;
        Assert.Equal("hello", root.GetProperty("type").GetString());
        Assert.Equal(Hashing.Sha256Hex("tok"), root.GetProperty("token").GetString());
        Assert.Equal("node-1", root.GetProperty("node_id").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32()); // legacy field
        Assert.Equal(1, root.GetProperty("protocol_major").GetInt32());
        // Cumulative feature level: >= 1 accepts kind:"files", >= 2 accepts
        // frames up to 64 MiB (see LargeFrameTests), >= 3 rebuilds folder trees
        // from the optional per-entry "path".
        Assert.Equal(3, root.GetProperty("protocol_minor").GetInt32());
        Assert.Equal("1.2.3", root.GetProperty("app_version").GetString());
        // null fields are omitted entirely
        Assert.False(root.TryGetProperty("kind", out _));
    }

    [Fact]
    public void NonAsciiContentIsNotEscaped()
    {
        var frame = WireMessage.ClipText("안녕 👋", 2.0).EncodeFrame();
        var body = System.Text.Encoding.UTF8.GetString(frame.AsSpan(4));
        Assert.Contains("안녕", body); // UnsafeRelaxedJsonEscaping, like ensure_ascii=False
    }

    [Fact]
    public void ClipFileDoesNotThrowOnInvalidUnicodeName()
    {
        // NTFS permits unpaired UTF-16 surrogates in filenames, so a name read
        // from the clipboard file-drop can be ill-formed. NFC normalization
        // must NOT throw (String.Normalize throws ArgumentException on invalid
        // Unicode) — that would take the whole broadcast down while building the
        // frame. Fall back to the raw name instead of crashing.
        var lone = "bad\uD800name.txt"; // unpaired high surrogate
        var ex = Record.Exception(() => WireMessage.ClipFile(lone, new byte[] { 1 }, 0));
        Assert.Null(ex);
    }

    [Fact]
    public void ClipFileNameIsNFCOnTheWire()
    {
        // Filenames must leave as NFC (composed) bytes so a Windows peer
        // renders Korean names instead of broken conjoining jamo. A macOS
        // sender reads them in NFD; the wire builder normalizes. Keep in
        // lockstep with Swift WireMessage.clipFile and anyclip.send_clip.
        var baseName = "결과보고서";
        var nfd = baseName.Normalize(NormalizationForm.FormD) + ".pdf";
        var nfc = baseName.Normalize(NormalizationForm.FormC) + ".pdf";
        Assert.NotEqual(nfd, nfc);
        var frame = WireMessage.ClipFile(nfd, new byte[] { 1, 2, 3 }, 0).EncodeFrame();
        using var doc = JsonDocument.Parse(frame.AsSpan(4).ToArray());
        Assert.Equal(nfc, doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void ClipFactoriesMatchPythonShapes()
    {
        var img = WireMessage.ClipImage(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, 3.0);
        Assert.Equal("image", img.Kind);
        Assert.Equal(4, img.Bytes);
        Assert.Equal(Hashing.Sha256Hex(new byte[] { 0x89, 0x50, 0x4E, 0x47 }), img.Hash);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            Convert.FromBase64String(img.Content!));

        var file = WireMessage.ClipFile("réport.txt", "body"u8.ToArray(), 4.0);
        Assert.Equal("file", file.Kind);
        Assert.Equal("réport.txt", file.Name);
        Assert.Equal(4, file.Bytes);
    }

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

    [Fact]
    public void DecodeToleratesUnknownFieldsAndRejectsBadJson()
    {
        var msg = WireMessage.DecodeBody(
            "{\"type\":\"hello\",\"token\":\"t\",\"future\":[1]}"u8.ToArray());
        Assert.Equal("hello", msg!.Type);
        Assert.Equal("t", msg.Token);
        Assert.Null(WireMessage.DecodeBody("{notjson"u8.ToArray()));
        Assert.Null(WireMessage.DecodeBody("null"u8.ToArray())); // valid JSON null literal → null
    }

    [Fact]
    public void PeerVersionInfoLegacyFallback()
    {
        var legacy = WireMessage.DecodeBody(
            "{\"type\":\"hello\",\"version\":1}"u8.ToArray())!;
        var v = legacy.PeerVersionInfo();
        Assert.Equal(1, v.ProtocolMajor);
        Assert.Equal(0, v.ProtocolMinor);
        Assert.Equal("unknown", v.AppVersion);

        var explicitMsg = WireMessage.DecodeBody(
            "{\"type\":\"hello\",\"version\":1,\"protocol_major\":2,\"protocol_minor\":3,\"app_version\":\"9\"}"u8.ToArray())!;
        var v2 = explicitMsg.PeerVersionInfo();
        Assert.Equal(2, v2.ProtocolMajor);
        Assert.Equal(3, v2.ProtocolMinor);
    }

    [Fact]
    public void OversizedPayloadThrows()
    {
        var big = WireMessage.ClipText(new string('x', Wire.MaxPayload + 1), 0);
        var ex = Assert.Throws<PayloadTooLargeException>(() => big.EncodeFrame());
        Assert.True(ex.Size > Wire.MaxPayload);
    }

    [Fact]
    public void FrameLengthGuardsShortInput()
    {
        Assert.Equal(258, WireMessage.FrameLength(new byte[] { 0, 0, 1, 2 }));
        Assert.Equal(0, WireMessage.FrameLength(new byte[] { 1, 2 })); // short → invalid
    }

    [Fact]
    public void ClipPayloadKindsAndHashes()
    {
        ClipPayload p = new TextClip("abc");
        Assert.Equal("text", p.Kind);
        Assert.Equal(Hashing.Sha256Hex("abc"), p.PayloadHash);
        Assert.Equal("image", new ImageClip(new byte[] { 1 }).Kind);
        Assert.Equal("file", new FileClip("a.txt", new byte[] { 1 }).Kind);
    }

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

    // ---- protocol 1.3: the optional per-entry folder "path" ---------------

    [Fact]
    public void RelPathValidationMatchesTheWireRules()
    {
        // Accepted shapes.
        Assert.True(Wire.IsValidRelPath("docs/a.txt", "a.txt"));
        Assert.True(Wire.IsValidRelPath("docs/sub dir/a.txt", "a.txt"));
        Assert.True(Wire.IsValidRelPath("보고서/메모.txt", "메모.txt"));
        // A single segment is a legal PATH (the validator constrains
        // separators/segments, not a minimum depth); the placement step is
        // what treats it as a loose file. Mirrors Swift isValidWirePath +
        // ReceivedTree.plan's `segments.count >= 2` tree test.
        Assert.True(Wire.IsValidRelPath("a.txt", "a.txt"));

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
    public void RelPathLengthIsCountedInCodePointsLikePython()
    {
        // Python counts len() = CODE POINTS; C# string.Length counts UTF-16
        // units, so an astral segment would be double-counted and the SAME
        // clip would rebuild a tree on one receiver and land flat here.
        const string astral = "😀"; // U+1F600: 1 code point, 2 UTF-16 units
        var seg240 = string.Concat(Enumerable.Repeat(astral, 234));
        Assert.Equal(468, seg240.Length);                       // UTF-16 units
        Assert.True(Wire.IsValidRelPath(seg240 + "/a.txt", "a.txt"));   // 240 code points
        var seg241 = seg240 + astral;
        Assert.False(Wire.IsValidRelPath(seg241 + "/a.txt", "a.txt"));  // 241 code points
    }

    [Fact]
    public void NonNfcPathsAreRejectedNotNormalized()
    {
        // NFC is a REJECTION rule, matching anyclip.is_valid_wire_path
        // (`path != unicodedata.normalize("NFC", path) -> False`) and Swift
        // isValidWirePath. A decomposed path lands FLAT; it is never silently
        // repaired, or the three implementations would disagree on the tree.
        var nfcPath = "보고서/메모.txt".Normalize(NormalizationForm.FormC);
        var nfdPath = nfcPath.Normalize(NormalizationForm.FormD);
        var nfcName = "메모.txt".Normalize(NormalizationForm.FormC);
        var nfdName = nfcName.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(nfcPath, nfdPath);
        Assert.False(Wire.IsValidRelPath(nfdPath, nfcName));
        Assert.False(Wire.IsValidRelPath(nfdPath, nfdName));
        // The last-segment == name check is ORDINAL, never canonical: Python
        // compares `segments[-1] != name` exactly, so a composed path with a
        // decomposed name must NOT match either.
        Assert.True(Wire.IsValidRelPath(nfcPath, nfcName));
        Assert.False(Wire.IsValidRelPath(nfcPath, nfdName));
        // Ill-formed UTF-16 (NTFS permits unpaired surrogates) is an INVALID
        // path, not an exception: string.IsNormalized/Normalize throw on it.
        var lone = "docs/bad\uD800.txt";
        var ex = Record.Exception(() => Wire.IsValidRelPath(lone, "bad\uD800.txt"));
        Assert.Null(ex);
        Assert.False(Wire.IsValidRelPath(lone, "bad\uD800.txt"));
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
    public void ClipFilesDropsAnInvalidPathAndSendsThatEntryFlat()
    {
        // Sender rule, in lockstep with anyclip.send_clip (`if
        // is_valid_wire_path(...) else log.warning("dropping invalid folder
        // path")`) and Swift clipFiles: an entry whose path breaks a wire rule
        // ships WITHOUT "path" rather than poisoning the frame — the peer
        // would reject that path anyway and place it flat.
        var frame = WireMessage.ClipFiles(new List<FileEntry>
        {
            new("a.txt", "one"u8.ToArray(), "../a.txt"),        // traversal
            new("b.txt", "two"u8.ToArray(), "docs\\b.txt"),     // backslash
            new("c.txt", "three"u8.ToArray(), "docs/other.txt"),// name mismatch
        }, 1.0).EncodeFrame();
        using var doc = JsonDocument.Parse(frame.AsSpan(4).ToArray());
        foreach (var entry in doc.RootElement.GetProperty("files").EnumerateArray())
            Assert.False(entry.TryGetProperty("path", out _));
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
}
