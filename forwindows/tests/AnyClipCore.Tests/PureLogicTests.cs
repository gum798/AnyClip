using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class EchoSuppressorTests
{
    [Fact]
    public void TracksPerKind()
    {
        var s = new EchoSuppressor();
        Assert.True(s.ShouldSend("text", "h1"));
        s.MarkReceived("text", "h1");
        Assert.False(s.ShouldSend("text", "h1"));
        Assert.True(s.ShouldSend("text", "h2"));
        Assert.True(s.ShouldSend("image", "h1"));
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
        Assert.Equal("we_rd_na_me", TextHelpers.SanitizeFilename("we!rd:na?me"));
        Assert.Equal("received.bin", TextHelpers.SanitizeFilename(""));
        Assert.Equal("received.bin", TextHelpers.SanitizeFilename("   "));
        Assert.Equal("한글파일.txt", TextHelpers.SanitizeFilename("한글파일.txt"));
        Assert.Equal("___", TextHelpers.SanitizeFilename("???"));
    }
}
