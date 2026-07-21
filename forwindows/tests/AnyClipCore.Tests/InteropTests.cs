using System.Diagnostics;
using System.Runtime.CompilerServices;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class InteropTests
{
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    /// fake_peer.py keeps the out-file open for write for the whole
    /// session; File.ReadAllText opens with FileShare.Read, which on
    /// Windows is a sharing violation against that write handle. This
    /// helper exists specifically for Windows sharing semantics — do NOT
    /// "simplify" it back to File.ReadAllText. (fake_peer.py itself is the
    /// shared wire reference also used by the Swift interop test; the
    /// C#-side reader is the minimal, isolated change.)
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    [Fact]
    public async Task InteropWithPythonFakePeer()
    {
        int port = 28631;
        string outFile = Path.Combine(Path.GetTempPath(),
            $"fake-peer-{Guid.NewGuid()}.jsonl");
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
            // Wait for READY.
            var ready = await proc.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            var clips = new List<ClipPayload>();
            var link = new PeerLink(
                new PeerLink.LinkConfig("interop-token", 28632, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            link.OnClip = p => { lock (clips) clips.Add(p); return Task.CompletedTask; };
            link.Emit = _ => { };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var session = link.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);

            async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
            {
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < deadline)
                { if (cond()) return true; await Task.Delay(50); }
                return cond();
            }

            Assert.True(await WaitUntil(() => link.IsActive));
            Assert.Equal("fake-peer", link.PeerName);
            Assert.True(await WaitUntil(() =>
            {
                lock (clips) return clips.Any(c =>
                    c is TextClip t && t.Text == "hello-from-python");
            }));

            await link.SendClipAsync(new TextClip("hello-from-csharp"));
            await link.SendClipAsync(new ImageClip(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 }));
            await link.SendClipAsync(new FileClip("노트.txt", "file-content"u8.ToArray()));
            await link.SendPingAsync();

            Assert.True(await WaitUntil(() =>
            {
                if (!File.Exists(outFile)) return false;
                var lines = ReadShared(outFile);
                return lines.Contains("hello-from-csharp")
                    && lines.Contains("\"kind\": \"file\"")
                    && lines.Contains("노트.txt")
                    && lines.Contains("\"kind\": \"image\"")
                    && lines.Contains("\"type\": \"ping\"");
            }));

            // Our hello satisfied Python's expectations (incl. legacy version).
            var outText = ReadShared(outFile);
            var helloLine = outText.Split('\n')
                .FirstOrDefault(l => l.Contains("\"event\": \"hello\""));
            Assert.NotNull(helloLine);
            Assert.Contains("\"version\": 1", helloLine);
            Assert.Contains("\"protocol_major\": 1", helloLine);

            link.Shutdown();
        }
        finally
        {
            if (!proc.HasExited) proc.Kill();
        }
    }

    [Fact]
    public async Task InteropSendsFilesClipToPythonPeer()
    {
        int port = 28633;
        string outFile = Path.Combine(Path.GetTempPath(),
            $"fake-peer-{Guid.NewGuid()}.jsonl");
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
            var ready = await proc.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
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
                while (DateTime.UtcNow < deadline)
                { if (cond()) return true; await Task.Delay(50); }
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

            // The recorded files frame carries exactly the two entries we sent,
            // with the aggregate hash Python recomputes from their bodies.
            var frame = ReadShared(outFile).Split('\n')
                .FirstOrDefault(l => l.Contains("\"kind\": \"files\""));
            Assert.NotNull(frame);
            var doc = System.Text.Json.JsonDocument.Parse(frame!);
            var files = doc.RootElement.GetProperty("data").GetProperty("files");
            Assert.Equal(2, files.GetArrayLength());
            Assert.Equal("노트.txt", files[0].GetProperty("name").GetString());
            Assert.Equal("(E&S) plan.txt", files[1].GetProperty("name").GetString());

            var expectedAgg = Hashing.AggregateFilesHash(new[]
            {
                Hashing.Sha256Hex("multi body one"u8.ToArray()),
                Hashing.Sha256Hex("multi body two"u8.ToArray()),
            });
            Assert.Equal(expectedAgg,
                doc.RootElement.GetProperty("data").GetProperty("hash").GetString());

            link.Shutdown();
        }
        finally
        {
            if (!proc.HasExited) proc.Kill();
        }
    }
}
