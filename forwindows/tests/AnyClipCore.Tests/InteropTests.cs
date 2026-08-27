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

    [Fact]
    public async Task InteropTwoPeersReceiveBroadcastAndNoRelay()
    {
        int portA = 28637, portB = 28638;
        string outA = Path.Combine(Path.GetTempPath(), $"fake-peer-A-{Guid.NewGuid()}.jsonl");
        string outB = Path.Combine(Path.GetTempPath(), $"fake-peer-B-{Guid.NewGuid()}.jsonl");
        using var procA = Process.Start(FakePeerPsi(portA, outA))!;
        using var procB = Process.Start(FakePeerPsi(portB, outB))!;
        try
        {
            Assert.Equal("READY", await procA.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("READY", await procB.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            var clips = new List<(ClipPayload Payload, string Peer)>();
            var manager = new LinkManager(
                new LinkConfig("interop-token", 28639, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (p, peer) => { lock (clips) clips.Add((p, peer)); return Task.CompletedTask; };
            manager.Emit = _ => { };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", portA, $"127.0.0.1:{portA}", cts.Token);
            await manager.TryConnectAsync("127.0.0.1", portB, $"127.0.0.1:{portB}", cts.Token);
            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 2));

            // Both peers pushed their own "hello-from-python" clip; both applied locally.
            Assert.True(await WaitUntil(() =>
            { lock (clips) return clips.Count(c => c.Payload is TextClip t && t.Text == "hello-from-python") >= 2; }));

            // One local clip broadcasts to BOTH peers.
            await manager.BroadcastAsync(new TextClip("mesh-broadcast"));
            Assert.True(await WaitUntil(() => File.Exists(outA) && ReadShared(outA).Contains("mesh-broadcast")));
            Assert.True(await WaitUntil(() => File.Exists(outB) && ReadShared(outB).Contains("mesh-broadcast")));

            // Non-relay: peer A's clip was applied locally, NEVER forwarded to B.
            // Drain B for a bounded interval; every text clip B received is our
            // broadcast, never a relayed "hello-from-python" (this doubles as the
            // echo-suppression-under-mesh check).
            await Task.Delay(1000);
            var recvTextFrames = ReadShared(outB).Split('\n')
                .Where(l => l.Contains("\"event\": \"recv\"") && l.Contains("\"kind\": \"text\""))
                .ToList();
            Assert.NotEmpty(recvTextFrames);
            Assert.All(recvTextFrames, l => Assert.DoesNotContain("hello-from-python", l));
            Assert.Contains(recvTextFrames, l => l.Contains("mesh-broadcast"));

            manager.Shutdown();
        }
        finally
        {
            if (!procA.HasExited) procA.Kill();
            if (!procB.HasExited) procB.Kill();
        }
    }

    [Fact]
    public async Task InteropFolderOnlyClipSendsNothingToTheMinorZeroPythonPeer()
    {
        int port = 28641;
        string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
        using var proc = Process.Start(FakePeerPsi(port, outFile))!;
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            var manager = new LinkManager(
                new LinkConfig("interop-token", 28642, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (_, _) => Task.CompletedTask;
            manager.Emit = _ => { };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);
            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 1));

            // Every entry came from a copied folder, and protocol 1.0's single
            // kind:"file" frame has nowhere to put a path -> nothing is sent to
            // this peer at all.
            var res = await manager.BroadcastAsync(new FilesClip(new List<FileEntry>
            {
                new("a.txt", "one"u8.ToArray(), "docs/a.txt"),
                new("b.txt", "two"u8.ToArray(), "docs/sub/b.txt"),
            }));
            Assert.Empty(res.Delivered);
            Assert.Equal(0, res.OldPeerDrops);
            // Skipping is NOT dropping the link: the peer stays connected and a
            // following ordinary clip still reaches it.
            Assert.Equal(1, manager.ActiveLinkCount);

            // Sentinel: waiting on a clip that DOES arrive is what turns
            // "nothing was written" into a real assertion instead of a race
            // against a still-empty file.
            await manager.BroadcastAsync(new TextClip("after-folder"));
            Assert.True(await WaitUntil(() =>
                File.Exists(outFile) && ReadShared(outFile).Contains("after-folder")));
            var seen = ReadShared(outFile);
            Assert.DoesNotContain("\"kind\": \"files\"", seen);
            Assert.DoesNotContain("a.txt", seen);

            manager.Shutdown();
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }
}
