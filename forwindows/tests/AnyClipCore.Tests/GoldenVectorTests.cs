using System.Runtime.CompilerServices;
using System.Text.Json;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class GoldenVectorTests
{
    private static string FixturesDir([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(path)!, "..", "..", "..",
            "formacOS", "Tests", "AnyClipCoreTests", "Fixtures"));

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(FixturesDir(), name));

    private static JsonElement Manifest() =>
        JsonDocument.Parse(Fixture("manifest.json")).RootElement;

    private static WireMessage DecodeGolden(string name)
    {
        var frame = Fixture(name);
        Assert.Equal(frame.Length - 4, WireMessage.FrameLength(frame[..4]));
        var msg = WireMessage.DecodeBody(frame[4..]);
        Assert.NotNull(msg);
        return msg!;
    }

    [Fact]
    public void GoldenHelloDecodes()
    {
        var m = DecodeGolden("hello.bin");
        var man = Manifest();
        Assert.Equal("hello", m.Type);
        Assert.Equal(man.GetProperty("token_hash").GetString(), m.Token);
        Assert.Equal(man.GetProperty("node_id").GetString(), m.NodeId);
        Assert.Equal(1, m.Version);
        Assert.Equal(1, m.ProtocolMajor);
        Assert.Equal(Hashing.Sha256Hex(man.GetProperty("token").GetString()!),
            man.GetProperty("token_hash").GetString());
    }

    [Fact]
    public void GoldenClipTextDecodes()
    {
        var m = DecodeGolden("clip_text.bin");
        var man = Manifest();
        Assert.Equal(man.GetProperty("text").GetString(), m.Content);
        Assert.Equal(man.GetProperty("text_hash").GetString(),
            Hashing.Sha256Hex(m.Content!));
    }

    [Fact]
    public void GoldenClipImageDecodes()
    {
        var m = DecodeGolden("clip_image.bin");
        var man = Manifest();
        var data = WireMessage.StrictBase64Decode(m.Content!)!;
        Assert.Equal(man.GetProperty("image_hash").GetString(), Hashing.Sha256Hex(data));
        Assert.Equal(m.Bytes, data.Length);
        Assert.Equal(man.GetProperty("image_b64").GetString(),
            Convert.ToBase64String(data));
    }

    [Fact]
    public void GoldenClipFileDecodes()
    {
        var m = DecodeGolden("clip_file.bin");
        var man = Manifest();
        Assert.Equal(man.GetProperty("file_name").GetString(), m.Name);
        var data = WireMessage.StrictBase64Decode(m.Content!)!;
        Assert.Equal(man.GetProperty("file_hash").GetString(), Hashing.Sha256Hex(data));
        Assert.Equal(m.Bytes, data.Length);
    }

    [Fact]
    public void GoldenPingDecodes()
    {
        var m = DecodeGolden("ping.bin");
        Assert.Equal("ping", m.Type);
        Assert.Equal(1718000000.5, m.Ts);
    }
}
