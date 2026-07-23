using System.Diagnostics;
using System.Runtime.CompilerServices;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class InteropTests
{
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    // fake_peer.py keeps the out-file open for write for the whole session;
    // File.ReadAllText opens with FileShare.Read, a sharing violation on Windows
    // against that write handle. Do NOT "simplify" back to File.ReadAllText.
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static ProcessStartInfo FakePeerPsi(int port, string outFile, bool sendFiles = false)
    {
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
        if (sendFiles) psi.ArgumentList.Add("--send-files");
        return psi;
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public async Task InteropWithPythonFakePeer()
    {
        int port = 28631;
        string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
        using var proc = Process.Start(FakePeerPsi(port, outFile))!;
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            var clips = new List<(ClipPayload Payload, string Peer)>();
            var events = new List<DaemonEvent>();
            var manager = new LinkManager(
                new LinkConfig("interop-token", 28632, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (p, peer) => { lock (clips) clips.Add((p, peer)); return Task.CompletedTask; };
            manager.Emit = e => { lock (events) events.Add(e); };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);

            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 1));
            lock (events) Assert.Contains(events, e => e is LinkUp u && u.PeerName == "fake-peer");
            Assert.True(await WaitUntil(() =>
            { lock (clips) return clips.Any(c => c.Payload is TextClip t && t.Text == "hello-from-python"); }));

            await manager.BroadcastAsync(new TextClip("hello-from-csharp"));
            await manager.BroadcastAsync(new ImageClip(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 }));
            await manager.BroadcastAsync(new FileClip("노트.txt", "file-content"u8.ToArray()));

            Assert.True(await WaitUntil(() =>
            {
                if (!File.Exists(outFile)) return false;
                var lines = ReadShared(outFile);
                return lines.Contains("hello-from-csharp")
                    && lines.Contains("\"kind\": \"file\"")
                    && lines.Contains("노트.txt")
                    && lines.Contains("\"kind\": \"image\"");
            }));

            var outText = ReadShared(outFile);
            var helloLine = outText.Split('\n').FirstOrDefault(l => l.Contains("\"event\": \"hello\""));
            Assert.NotNull(helloLine);
            Assert.Contains("\"version\": 1", helloLine);
            Assert.Contains("\"protocol_major\": 1", helloLine);

            manager.Shutdown();
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }

    [Fact]
    public async Task InteropDowngradesFilesClipToOldPythonPeer()
    {
        int port = 28633;
        string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
        using var proc = Process.Start(FakePeerPsi(port, outFile))!;
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            var manager = new LinkManager(
                new LinkConfig("interop-token", 28634, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (_, _) => Task.CompletedTask;
            manager.Emit = _ => { };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);
            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 1));

            // fake_peer advertises protocol_minor 0 -> the broadcast downgrades the
            // 2-file clip to the first file as a legacy "file", and reports 1 drop.
            var res = await manager.BroadcastAsync(new FilesClip(new List<(string, byte[])>
            {
                ("노트.txt", "multi body one"u8.ToArray()),
                ("(E&S) plan.txt", "multi body two"u8.ToArray()),
            }));
            Assert.Equal(1, res.OldPeerDrops);

            Assert.True(await WaitUntil(() =>
            {
                if (!File.Exists(outFile)) return false;
                var lines = ReadShared(outFile);
                return lines.Contains("\"kind\": \"file\"") && lines.Contains("노트.txt");
            }));
            // Never a multi-file "files" frame to a minor-0 peer.
            Assert.DoesNotContain("\"kind\": \"files\"", ReadShared(outFile));

            manager.Shutdown();
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }

    [Fact]
    public async Task InteropReceivesFilesClipFromPythonPeer()
    {
        int port = 28635;
        string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
        using var proc = Process.Start(FakePeerPsi(port, outFile, sendFiles: true))!;
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            var clips = new List<(ClipPayload Payload, string Peer)>();
            var manager = new LinkManager(
                new LinkConfig("interop-token", 28636, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (p, peer) => { lock (clips) clips.Add((p, peer)); return Task.CompletedTask; };
            manager.Emit = _ => { };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);
            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 1));
            Assert.True(await WaitUntil(() =>
            { lock (clips) return clips.Any(c => c.Payload is FilesClip f && f.Files.Count == 2); }));

            FilesClip got;
            lock (clips) got = clips.Select(c => c.Payload).OfType<FilesClip>().First(f => f.Files.Count == 2);
            Assert.Equal("노트.txt", got.Files[0].Name);
            Assert.Equal("multi body one", System.Text.Encoding.UTF8.GetString(got.Files[0].Data));
            Assert.Equal("(E&S) plan.txt", got.Files[1].Name);
            Assert.Equal("multi body two", System.Text.Encoding.UTF8.GetString(got.Files[1].Data));

            manager.Shutdown();
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }
}
