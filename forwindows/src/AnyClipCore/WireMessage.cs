using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnyClip.Core;

public sealed class PayloadTooLargeException(int size)
    : Exception($"payload too large ({size} bytes)")
{
    public int Size { get; } = size;
}

public readonly record struct VersionInfo(
    string AppVersion, int ProtocolMajor, int ProtocolMinor);

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

/// One wire message. Optional-field class covers hello/clip/ping/pong —
/// nulls omitted on encode, unknown fields ignored on decode, matching the
/// Python dict protocol. JsonPropertyName values ARE the wire field names.
public sealed record WireMessage
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("node_id")] public string? NodeId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("version")] public int? Version { get; init; }
    [JsonPropertyName("app_version")] public string? AppVersion { get; init; }
    [JsonPropertyName("protocol_major")] public int? ProtocolMajor { get; init; }
    [JsonPropertyName("protocol_minor")] public int? ProtocolMinor { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("files")] public IReadOnlyList<WireFileEntry>? Files { get; init; }
    [JsonPropertyName("hash")] public string? Hash { get; init; }
    [JsonPropertyName("ts")] public double? Ts { get; init; }
    [JsonPropertyName("bytes")] public int? Bytes { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Raw UTF-8 like Python's ensure_ascii=False.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static WireMessage Hello(
        string tokenHash, string nodeId, string name, string appVersion) => new()
    {
        Type = "hello", Token = tokenHash, NodeId = nodeId, Name = name,
        Version = Wire.LegacyVersion, AppVersion = appVersion,
        ProtocolMajor = Wire.ProtocolMajor, ProtocolMinor = Wire.ProtocolMinor,
    };

    public static WireMessage ClipText(string text, double ts) => new()
    {
        Type = "clip", Kind = "text", Content = text,
        Hash = Hashing.Sha256Hex(text), Ts = ts,
    };

    public static WireMessage ClipImage(byte[] png, double ts) => new()
    {
        Type = "clip", Kind = "image", Content = Convert.ToBase64String(png),
        Hash = Hashing.Sha256Hex(png), Ts = ts, Bytes = png.Length,
    };

    public static WireMessage ClipFile(string name, byte[] data, double ts) => new()
    {
        // NFC on the wire: a macOS sender reads filenames in NFD (conjoining
        // jamo U+11xx a Windows peer can't render). Normalize so every
        // receiver gets a composed, renderable name. ToNfc tolerates ill-formed
        // UTF-16 (NTFS allows unpaired surrogates) so a send never throws here.
        // Keep in lockstep with Swift WireMessage.clipFile and anyclip.send_clip.
        Type = "clip", Kind = "file", Name = TextHelpers.ToNfc(name),
        Content = Convert.ToBase64String(data),
        Hash = Hashing.Sha256Hex(data), Ts = ts, Bytes = data.Length,
    };

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

    public static WireMessage Clip(ClipPayload payload, double ts) => payload switch
    {
        TextClip t => ClipText(t.Text, ts),
        ImageClip i => ClipImage(i.Png, ts),
        FileClip f => ClipFile(f.Name, f.Data, ts),
        FilesClip fs => ClipFiles(fs.Files, ts),
        _ => throw new ArgumentOutOfRangeException(nameof(payload)),
    };

    public static WireMessage Ping(double ts) => new() { Type = "ping", Ts = ts };
    public static WireMessage Pong(double ts) => new() { Type = "pong", Ts = ts };

    /// 4-byte big-endian length prefix + UTF-8 JSON body.
    public byte[] EncodeFrame()
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (body.Length > Wire.MaxPayload) throw new PayloadTooLargeException(body.Length);
        var frame = new byte[4 + body.Length];
        frame[0] = (byte)(body.Length >> 24);
        frame[1] = (byte)(body.Length >> 16);
        frame[2] = (byte)(body.Length >> 8);
        frame[3] = (byte)body.Length;
        body.CopyTo(frame, 4);
        return frame;
    }

    /// Big-endian length from a 4-byte header; 0 (= invalid) on short input.
    public static int FrameLength(byte[] header)
    {
        if (header.Length < 4) return 0;
        return (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
    }

    /// null on malformed JSON — the caller treats that as end-of-session.
    public static WireMessage? DecodeBody(byte[] body)
    {
        try { return JsonSerializer.Deserialize<WireMessage>(body, Options); }
        catch (JsonException) { return null; }
    }

    /// Legacy fallback: an old peer only sends `version` (= protocol_major).
    public VersionInfo PeerVersionInfo() => new(
        string.IsNullOrEmpty(AppVersion) ? "unknown" : AppVersion,
        ProtocolMajor ?? Version ?? 0,
        ProtocolMinor ?? 0);

    /// Strict base64, mirroring Python b64decode(validate=True).
    public static byte[]? StrictBase64Decode(string s)
    {
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}
