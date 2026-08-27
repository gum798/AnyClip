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
            Assert.Null(entry.Path);   // the pre-1.3 vector stays path-free
        }
        Assert.Equal(man.GetProperty("files_aggregate").GetString(),
            Hashing.AggregateFilesHash(hashes));
        Assert.Equal(man.GetProperty("files_aggregate").GetString(), m.Hash);
        Assert.Equal(man.GetProperty("files_total_bytes").GetInt32(), m.Bytes);
    }

    /// The 1.3 vector: ONE kind:"files" frame carrying both shapes — entries
    /// derived from a copied folder (with "path") and a file the user selected
    /// directly (no "path"). Values come from the Python-canonical manifest, so
    /// the generator owns the sample data. Byte-exact re-encoding is NOT
    /// asserted: Python's json.dumps writes ", "/": " separators, System.Text.Json
    /// writes them compact — the frames are JSON-equivalent, not byte-equal.
    /// Mirrors Swift goldenClipFilesTreeDecodes.
    [Fact]
    public void GoldenClipFilesWithPathDecodes()
    {
        var m = DecodeGolden("clip_files_path.bin");
        var man = Manifest();
        Assert.Equal("files", m.Kind);
        Assert.NotNull(m.Files);
        var entries = m.Files!;
        var names = man.GetProperty("files_path_names").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        // JSON null for the entry whose frame carries no "path" key at all.
        var paths = man.GetProperty("files_path_paths").EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString()).ToArray();
        var hashes = man.GetProperty("files_path_hashes").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Equal(paths.Length, entries.Count);
        int total = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            Assert.Equal(names[i], entry.Name);
            Assert.Equal(paths[i], entry.Path);                 // Python-canonical path
            var data = WireMessage.StrictBase64Decode(entry.Content!)!;
            Assert.Equal(hashes[i], Hashing.Sha256Hex(data));   // recomputed == manifest
            Assert.Equal(hashes[i], entry.Hash);                // wire hash == manifest
            Assert.Equal(data.Length, entry.Bytes);
            total += data.Length;
            if (entry.Path is null) continue;
            Assert.True(Wire.IsValidRelPath(entry.Path, entry.Name!),
                $"golden path rejected by the validator: {entry.Path}");
            Assert.Contains("/", entry.Path);                   // real subdirectory depth
            Assert.EndsWith("/" + entry.Name, entry.Path);      // last segment == name
        }
        // Both shapes must actually be exercised by the vector.
        Assert.Contains(entries, e => e.Path is not null);      // folder entries
        Assert.Contains(entries, e => e.Path is null);          // a loose file
        // The Python encoder OMITTED "path" for the loose entry — it did not
        // emit null. That omission is what keeps every protocol-1.2 frame
        // byte-identical, so assert it on the RAW fixture JSON: the decoded
        // record cannot tell "absent" from "null" apart.
        var raw = Fixture("clip_files_path.bin");
        using var doc = JsonDocument.Parse(raw.AsMemory(4));
        var rawEntries = doc.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(entries.Count, rawEntries.Length);
        for (int i = 0; i < rawEntries.Length; i++)
            Assert.Equal(
                paths[i] is null
                    ? new[] { "name", "content", "hash", "bytes" }
                    : new[] { "name", "content", "hash", "bytes", "path" },
                rawEntries[i].EnumerateObject().Select(p => p.Name).ToArray());
        // Aggregate + total match the manifest: adding "path" must not change
        // how a files clip hashes.
        Assert.Equal(man.GetProperty("files_path_aggregate").GetString(),
            Hashing.AggregateFilesHash(hashes));
        Assert.Equal(man.GetProperty("files_path_aggregate").GetString(), m.Hash);
        Assert.Equal(man.GetProperty("files_path_total_bytes").GetInt32(), m.Bytes);
        Assert.Equal(total, m.Bytes);
    }

    [Fact]
    public void GoldenPingDecodes()
    {
        var m = DecodeGolden("ping.bin");
        Assert.Equal("ping", m.Type);
        Assert.Equal(1718000000.5, m.Ts);
    }
}
