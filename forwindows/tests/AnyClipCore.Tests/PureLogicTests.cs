using System.Text;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class EchoSuppressorTests
{
    private const double Window = EchoSuppressor.SuppressWindowSeconds;

    [Fact]
    public void TracksPerKind()
    {
        var s = new EchoSuppressor();
        Assert.True(s.ShouldSend("text", "h1", now: 0));
        s.MarkReceived("text", "h1", now: 0);
        Assert.False(s.ShouldSend("text", "h1", now: 0));
        Assert.True(s.ShouldSend("text", "h2", now: 0));
        Assert.True(s.ShouldSend("image", "h1", now: 0));
    }

    [Fact]
    public void SuppressesEchoWithinWindow()
    {
        var s = new EchoSuppressor();
        s.MarkReceived("text", "h1", now: 0);
        Assert.False(s.ShouldSend("text", "h1", now: Window));
    }

    [Fact]
    public void DeliberateRecopySendsAfterWindow()
    {
        // The 2026-09-02 password bug: the exact string last received from the
        // peer could never be re-sent, however much later the user re-copied it.
        var s = new EchoSuppressor();
        s.MarkReceived("text", "h1", now: 0);
        Assert.True(s.ShouldSend("text", "h1", now: Window + 0.001));
        Assert.True(s.ShouldSend("text", "h1", now: 87));
    }

    [Fact]
    public void RemarkRearmsWindow()
    {
        var s = new EchoSuppressor();
        s.MarkReceived("text", "h1", now: 0);
        s.MarkReceived("text", "h1", now: 40);
        Assert.False(s.ShouldSend("text", "h1", now: 60));
        Assert.True(s.ShouldSend("text", "h1", now: 40 + Window + 0.001));
    }

    [Fact]
    public void DefaultClockSuppressesFreshReceive()
    {
        // No explicit now: the real monotonic clock applies; a receive marked
        // an instant ago must still be suppressed.
        var s = new EchoSuppressor();
        s.MarkReceived("text", "h1");
        Assert.False(s.ShouldSend("text", "h1"));
    }
}

public class AuthGateTests
{
    [Fact]
    public void BlocksAtFiveWithinCooldown()
    {
        double t = 1000;
        var gate = new AuthGate(() => t);
        for (int i = 0; i < 4; i++) gate.RecordFail("10.0.0.1");
        Assert.False(gate.IsBlocked("10.0.0.1"));
        gate.RecordFail("10.0.0.1");
        Assert.True(gate.IsBlocked("10.0.0.1"));
        Assert.False(gate.IsBlocked("10.0.0.2"));
        t += 61;
        Assert.False(gate.IsBlocked("10.0.0.1"));
    }

    [Fact]
    public void SuccessClears()
    {
        double t = 1000;
        var gate = new AuthGate(() => t);
        for (int i = 0; i < 5; i++) gate.RecordFail("ip");
        gate.RecordOk("ip");
        Assert.False(gate.IsBlocked("ip"));
    }

    [Fact]
    public void StaleCountDoesNotCarryIntoNewWindow()
    {
        // Regression pinned from the Swift port live test.
        double t = 1000;
        var gate = new AuthGate(() => t);
        for (int i = 0; i < 4; i++) gate.RecordFail("ip");
        t += 61;
        gate.RecordFail("ip"); // sweep first → restarts at 1
        Assert.False(gate.IsBlocked("ip"));
    }
}

public class ConfigStoreTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "anyclip-test-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void RoundTripsAndToleratesCorruption()
    {
        var dir = TempDir();
        Assert.Null(ConfigStore.Load(dir));
        ConfigStore.Save("secret-token", dir);
        Assert.Equal("secret-token", ConfigStore.Load(dir));

        File.WriteAllText(Path.Combine(dir, "config.json"), "not json{{{");
        Assert.Null(ConfigStore.Load(dir));
        File.WriteAllText(Path.Combine(dir, "config.json"), "{\"other\":1}");
        Assert.Null(ConfigStore.Load(dir));
        File.WriteAllText(Path.Combine(dir, "config.json"), "{\"token\":\"\"}");
        Assert.Null(ConfigStore.Load(dir));
        File.WriteAllText(Path.Combine(dir, "config.json"), "{\"token\":123}");
        Assert.Null(ConfigStore.Load(dir)); // non-string token tolerated
    }

    [Fact]
    public void ReadsPythonWrittenFile()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"),
            "{\n  \"token\": \"from-python\"\n}\n");
        Assert.Equal("from-python", ConfigStore.Load(dir));
    }

    [Fact]
    public void GeneratedTokensAreUrlSafe43Chars()
    {
        var a = ConfigStore.GenerateToken();
        Assert.NotEqual(a, ConfigStore.GenerateToken());
        Assert.True(a.Length >= 42);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }
}

public class TxtCodecTests
{
    [Fact]
    public void RoundTripAndWireFormat()
    {
        var data = TxtCodec.Encode(new[] { ("k", "v") });
        Assert.Equal(new byte[] { 3, (byte)'k', (byte)'=', (byte)'v' }, data);
        var entries = new[] { ("id", "1111"), ("protocol_major", "1") };
        Assert.Equal("1111", TxtCodec.Decode(TxtCodec.Encode(entries))["id"]);
    }

    [Fact]
    public void DecodeStopsAtMidStreamTruncation()
    {
        // A length byte claiming more bytes than remain makes every later
        // offset untrustworthy — decode keeps what it has and stops
        // (intentional; the Swift port behaves identically).
        var data = new List<byte>();
        data.AddRange(TxtCodec.Encode(new[] { ("a", "1") }));
        data.Add(50); // claims 50 bytes; only a few follow
        data.AddRange("xx"u8.ToArray());
        data.AddRange(TxtCodec.Encode(new[] { ("b", "2") })); // unreachable
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1" },
            TxtCodec.Decode(data.ToArray()));
    }

    [Fact]
    public void DecodeIgnoresMalformedTailAndOversized()
    {
        var data = TxtCodec.Encode(new[] { ("a", "1") }).ToList();
        data.Add(250);
        data.AddRange("xx"u8.ToArray());
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1" },
            TxtCodec.Decode(data.ToArray()));
        var big = TxtCodec.Encode(new[] { ("big", new string('v', 300)), ("ok", "1") });
        Assert.Equal(new Dictionary<string, string> { ["ok"] = "1" }, TxtCodec.Decode(big));
    }
}

public class RotatingLogTests
{
    [Fact]
    public void WritesPythonShapedLinesAndRotates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anyclip-log-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "anyclip.log");
        var log = new RotatingLog(file, maxBytes: 200, backupCount: 3);
        log.Info("hello world");
        var line = File.ReadAllText(file);
        Assert.Contains(" INFO hello world\n", line);
        Assert.Equal('-', line[4]);
        Assert.Equal(',', line[19]); // "yyyy-MM-dd HH:mm:ss,fff"

        for (int i = 0; i < 30; i++) log.Info($"line {i} padding padding padding");
        Assert.True(File.Exists(file + ".1"));
        Assert.False(File.Exists(file + ".4"));
        Assert.True(new FileInfo(file).Length <= 300);
        log.Debug("dbg-marker");
        Assert.Contains("DEBUG dbg-marker", File.ReadAllText(file));
    }
}

public class TextHelpersTests
{
    [Fact]
    public void PreviewCollapsesAndTruncates()
    {
        Assert.Equal("a b c", TextHelpers.Preview("a\nb\rc"));
        Assert.Equal("(empty)", TextHelpers.Preview(""));
        Assert.Equal(new string('x', 80) + "...", TextHelpers.Preview(new string('x', 100)));
    }

    [Fact]
    public void SanitizeFilenameMatchesPython()
    {
        Assert.Equal("report v2.txt", TextHelpers.SanitizeFilename("report v2.txt"));
        Assert.Equal("c.txt", TextHelpers.SanitizeFilename("a/b/c.txt"));
        Assert.Equal("lid.txt", TextHelpers.SanitizeFilename("in:va/lid.txt"));
        Assert.Equal("we!rd_na_me", TextHelpers.SanitizeFilename("we!rd:na?me"));
        Assert.Equal("received.bin", TextHelpers.SanitizeFilename(""));
        Assert.Equal("received.bin", TextHelpers.SanitizeFilename("   "));
        Assert.Equal("한글파일.txt", TextHelpers.SanitizeFilename("한글파일.txt"));
        Assert.Equal("___", TextHelpers.SanitizeFilename("???"));
    }

    [Fact]
    public void SanitizeFilenameNormalizesDecomposedUnicodeToNFC()
    {
        // A macOS peer sends filenames in NFD (decomposed Hangul = conjoining
        // jamo U+11xx Windows can't render). Normalize to NFC so a received
        // file lands with the correct, renderable name. Keep in lockstep with
        // Swift sanitizeFilename and anyclip.update_local_file.
        var baseName = "KPI후보_2x2_매트릭스_v2_결과지";
        var nfd = baseName.Normalize(NormalizationForm.FormD) + ".pdf";
        var nfc = baseName.Normalize(NormalizationForm.FormC) + ".pdf";
        Assert.NotEqual(nfd, nfc);                                    // forms genuinely differ
        Assert.Equal(nfc, TextHelpers.SanitizeFilename(nfd));         // decomposed in → composed out
        Assert.Equal(nfc, TextHelpers.SanitizeFilename(nfc));         // idempotent on NFC
    }

    [Fact]
    public void SanitizeFilenameDoesNotThrowOnInvalidUnicode()
    {
        // NFC normalization must not throw on ill-formed UTF-16 (unpaired
        // surrogate) — that would escape ApplyRemoteAsync's narrow catch and
        // tear down the link. Fall back to the raw name.
        var lone = "bad\uD800name.txt"; // unpaired high surrogate
        var ex = Record.Exception(() => TextHelpers.SanitizeFilename(lone));
        Assert.Null(ex);
    }

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
        Assert.Equal("received.bin", TextHelpers.SanitizeFilename("a/"));    // trailing sep -> empty basename
        Assert.Equal("received.bin", TextHelpers.SanitizeFilename("dir\\")); // rsplit("/",1)[-1] yields ""
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
}
