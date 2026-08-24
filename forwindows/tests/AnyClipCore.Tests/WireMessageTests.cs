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
        // frames up to 64 MiB (see LargeFrameTests).
        Assert.Equal(2, root.GetProperty("protocol_minor").GetInt32());
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
        // Unicode) — that would let SendClipAsync's catch silently drop the
        // file. Fall back to the raw name instead of crashing.
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
}
