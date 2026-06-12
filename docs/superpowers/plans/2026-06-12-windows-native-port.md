# Windows Native Port (C#/.NET 8, ./forwindows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Native C# Windows tray app in `forwindows/` that is wire-compatible with the Python/Swift AnyClip (protocol 1.0) and shares `~/.anyclip/`.

**Architecture:** .NET 8 solution with a platform-neutral `AnyClipCore` (wire codec, PeerLink over cross-platform sockets, watchdogs, daemon assembly behind injected `IClipboardSync`/`IMdnsService`/`IPidLock` interfaces — all built and tested ON macOS, including the fake_peer.py interop test) and a `net8.0-windows` WinForms `AnyClipApp` (clipboard events, DnsService P/Invoke mDNS, tray, dialogs, registry autostart — validated on the CI windows runner and by manual smoke).

**Tech Stack:** .NET 8 (`~/.dotnet/dotnet`, installed), System.Text.Json, xunit (test-only packages), WinForms NotifyIcon, dnsapi.dll P/Invoke. Zero third-party runtime packages.

**Spec:** `docs/superpowers/specs/2026-06-12-windows-native-port-design.md`
**Behavioral references:** `anyclip.py`, `app/tray_win.py`, `app/onboarding_win.py`, `autostart.py` (Python); `formacOS/` (Swift — the constants/semantics there are already parity-verified, mirror them).

**Conventions for every task:**
- Working dir: repo root `/Users/seojeonghwa/project/AnyClip`. `DOTNET="$HOME/.dotnet/dotnet"` — always invoke as `"$HOME/.dotnet/dotnet"` (not on PATH).
- Test command (macOS-runnable): `"$HOME/.dotnet/dotnet" test forwindows/tests/AnyClipCore.Tests`
- The App project cross-builds on macOS (`-p:EnableWindowsTargeting=true` is set in the csproj) but NEVER runs here. `AnyClipApp.Tests` is built but NOT run on macOS (`dotnet build`, not `dotnet test`).
- NEVER touch `~/.anyclip` or port 24816 in tests; use 28xxx loopback ports (28600+ range to avoid collision with the Swift suite).
- Commit after every green step; messages end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

---

### Task 1: Solution scaffold + toolchain gate

The gate proves: `dotnet restore` works through the corporate TLS-intercepting
proxy, xunit runs on macOS, and a failing test actually fails the run.

**Files:**
- Create: `forwindows/.gitignore`, `forwindows/AnyClip.sln`
- Create: `forwindows/src/AnyClipCore/AnyClipCore.csproj` + `Placeholder.cs`
- Create: `forwindows/tests/AnyClipCore.Tests/AnyClipCore.Tests.csproj` + `SmokeTests.cs`

- [ ] **Step 1: gitignore + projects**

`forwindows/.gitignore`:
```
bin/
obj/
dist/
*.user
```

```bash
cd forwindows
"$HOME/.dotnet/dotnet" new sln -n AnyClip
"$HOME/.dotnet/dotnet" new classlib -o src/AnyClipCore -n AnyClipCore -f net8.0
"$HOME/.dotnet/dotnet" new xunit -o tests/AnyClipCore.Tests -n AnyClipCore.Tests -f net8.0
"$HOME/.dotnet/dotnet" sln add src/AnyClipCore tests/AnyClipCore.Tests
"$HOME/.dotnet/dotnet" add tests/AnyClipCore.Tests reference src/AnyClipCore
```

Then overwrite `src/AnyClipCore/AnyClipCore.csproj` with:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>AnyClip.Core</RootNamespace>
  </PropertyGroup>
</Project>
```

Delete the template `Class1.cs`/`UnitTest1.cs`; add `src/AnyClipCore/Placeholder.cs`:
```csharp
namespace AnyClip.Core;

public static class CoreMarker
{
    public const bool Present = true;
}
```

`tests/AnyClipCore.Tests/SmokeTests.cs`:
```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void ToolchainSmoke() => Assert.True(CoreMarker.Present);
}
```

- [ ] **Step 2: Restore + test (THE GATE)**

Run: `"$HOME/.dotnet/dotnet" test forwindows/tests/AnyClipCore.Tests`
Expected: restore succeeds (nuget.org over the corp proxy), 1 test passes.

**If restore fails with TLS errors:** .NET on macOS trusts the system
keychains. Remediation order: (a) check whether the corp root is in the
login keychain (`security find-certificate -a -c "SK holdings" ~/Library/Keychains/login.keychain-db`);
(b) if absent, surface to the controller — the user may need to run
`! sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain /tmp/corp-root.pem`
(the root was extracted during setup at /tmp/dotnet-chain.pem, 2nd block).
Do not proceed past this gate until restore works.

- [ ] **Step 3: Failing-test experiment**

Temporarily add `[Fact] public void MustFail() => Assert.Equal(1, 2);`, run,
confirm non-zero exit + failure report, revert, confirm clean `git status`.

- [ ] **Step 4: Commit**

```bash
git add forwindows
git commit -m "forwindows: scaffold .NET solution (Core + xunit gate)"
```

---

### Task 2: Core — Hashing, Wire constants, WireMessage codec

**Files:**
- Create: `forwindows/src/AnyClipCore/Hashing.cs`
- Create: `forwindows/src/AnyClipCore/Wire.cs`
- Create: `forwindows/src/AnyClipCore/WireMessage.cs`
- Create: `forwindows/src/AnyClipCore/ClipPayload.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/WireMessageTests.cs`
- Delete: `forwindows/src/AnyClipCore/Placeholder.cs` (replace SmokeTests assertion with `Assert.Equal(1, Wire.ProtocolMajor);`)

- [ ] **Step 1: failing tests** — `WireMessageTests.cs`:

```csharp
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
        Assert.Equal(0, root.GetProperty("protocol_minor").GetInt32());
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
    public void DecodeToleratesUnknownFieldsAndRejectsBadJson()
    {
        var msg = WireMessage.DecodeBody(
            "{\"type\":\"hello\",\"token\":\"t\",\"future\":[1]}"u8.ToArray());
        Assert.Equal("hello", msg!.Type);
        Assert.Equal("t", msg.Token);
        Assert.Null(WireMessage.DecodeBody("{notjson"u8.ToArray()));
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
        Assert.Throws<PayloadTooLargeException>(() => big.EncodeFrame());
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
}
```

- [ ] **Step 2:** Run — compile failure expected.

- [ ] **Step 3: implement**

`Hashing.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;

namespace AnyClip.Core;

public static class Hashing
{
    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(text));
}
```

`Wire.cs`:
```csharp
namespace AnyClip.Core;

/// Protocol constants — keep in lockstep with anyclip.py / formacOS.
public static class Wire
{
    public const int MaxPayload = 16 * 1024 * 1024;
    public const int ProtocolMajor = 1;
    public const int ProtocolMinor = 0;
    public const int LegacyVersion = 1;
    public const int DefaultPort = 24816;
    public const string ServiceType = "_anyclip._tcp";
    public const double HandshakeTimeoutSeconds = 5.0;
    public const double ConnectTimeoutSeconds = 5.0;
    public const double RaceWindowSeconds = 1.5;
    public const int MaxReconnectFails = 3;
}
```

`ClipPayload.cs`:
```csharp
namespace AnyClip.Core;

public abstract record ClipPayload
{
    public abstract string Kind { get; }
    public abstract string PayloadHash { get; }
}

public sealed record TextClip(string Text) : ClipPayload
{
    public override string Kind => "text";
    public override string PayloadHash => Hashing.Sha256Hex(Text);
}

public sealed record ImageClip(byte[] Png) : ClipPayload
{
    public override string Kind => "image";
    public override string PayloadHash => Hashing.Sha256Hex(Png);
}

public sealed record FileClip(string Name, byte[] Data) : ClipPayload
{
    public override string Kind => "file";
    public override string PayloadHash => Hashing.Sha256Hex(Data);
}
```

`WireMessage.cs`:
```csharp
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
        Type = "clip", Kind = "file", Name = name,
        Content = Convert.ToBase64String(data),
        Hash = Hashing.Sha256Hex(data), Ts = ts, Bytes = data.Length,
    };

    public static WireMessage Clip(ClipPayload payload, double ts) => payload switch
    {
        TextClip t => ClipText(t.Text, ts),
        ImageClip i => ClipImage(i.Png, ts),
        FileClip f => ClipFile(f.Name, f.Data, ts),
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
```

Update `SmokeTests.cs` to `Assert.Equal(1, Wire.ProtocolMajor);` and delete `Placeholder.cs`.

- [ ] **Step 4:** Run `"$HOME/.dotnet/dotnet" test forwindows/tests/AnyClipCore.Tests` — all pass.

- [ ] **Step 5: Commit** — `forwindows: wire codec + clip payloads (Python-parity)`

---

### Task 3: Core — golden vectors (shared formacOS fixtures) + VersionNegotiator + reducer + TrayIconSpec

**Files:**
- Create: `forwindows/src/AnyClipCore/VersionNegotiator.cs`
- Create: `forwindows/src/AnyClipCore/PeerState.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/GoldenVectorTests.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/VersionNegotiatorTests.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/PeerStateTests.cs`

- [ ] **Step 1: failing tests**

`GoldenVectorTests.cs` (fixtures referenced from the formacOS tree — single source of truth):
```csharp
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
```

`VersionNegotiatorTests.cs` — port the table verbatim:
```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class VersionNegotiatorTests
{
    private static VersionInfo V(int major, int minor, string app = "1.0.0") =>
        new(app, major, minor);

    [Fact] public void Same() =>
        Assert.Equal(Compatibility.Compatible, VersionNegotiator.Negotiate(V(1, 0), V(1, 0)));

    [Fact]
    public void PeerOlderMinorLinks()
    {
        var r = VersionNegotiator.Negotiate(V(1, 2), V(1, 0));
        Assert.Equal(Compatibility.PeerOlderMinor, r);
        Assert.True(VersionNegotiator.LinkAllowed(r));
    }

    [Fact]
    public void PeerNewerMinorLinks()
    {
        var r = VersionNegotiator.Negotiate(V(1, 0), V(1, 2));
        Assert.Equal(Compatibility.PeerNewerMinor, r);
        Assert.True(VersionNegotiator.LinkAllowed(r));
    }

    [Fact]
    public void MajorMismatchRefused()
    {
        Assert.False(VersionNegotiator.LinkAllowed(
            VersionNegotiator.Negotiate(V(2, 0), V(1, 5))));
        Assert.False(VersionNegotiator.LinkAllowed(
            VersionNegotiator.Negotiate(V(1, 9), V(2, 0))));
    }

    [Fact]
    public void WireValuesMatchPython()
    {
        Assert.Equal("compatible", VersionNegotiator.WireValue(Compatibility.Compatible));
        Assert.Equal("peer_older_minor", VersionNegotiator.WireValue(Compatibility.PeerOlderMinor));
        Assert.Equal("peer_newer_minor", VersionNegotiator.WireValue(Compatibility.PeerNewerMinor));
        Assert.Equal("peer_older_major", VersionNegotiator.WireValue(Compatibility.PeerOlderMajor));
        Assert.Equal("peer_newer_major", VersionNegotiator.WireValue(Compatibility.PeerNewerMajor));
    }
}
```

`PeerStateTests.cs` — port the 9 reducer golden tests plus TrayIconSpec:
```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerStateTests
{
    [Fact] public void InitialIsIdle() =>
        Assert.Equal(PeerStateKind.Idle, PeerUiState.Initial.Kind);

    [Fact]
    public void LinkUpProducesLinked()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial,
            new LinkUp("win-pc", "abc"), 42.0);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal("win-pc", s.PeerName);
        Assert.Equal(42.0, s.Since);
        Assert.Equal(0, s.ConsecutiveHandshakeFails);
    }

    [Fact]
    public void LinkDownGoesSearching()
    {
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("p", "x"), 1);
        var s = PeerStateReducer.Reduce(linked, new LinkDown("peer disconnected"), 2);
        Assert.Equal(PeerStateKind.Searching, s.Kind);
        Assert.Equal("peer disconnected", s.Reason);
    }

    [Fact]
    public void DiscoveryMovesIdleAndErrorToSearchingOnly()
    {
        Assert.Equal(PeerStateKind.Searching,
            PeerStateReducer.Reduce(PeerUiState.Initial, new PeerDiscovered("n", "a"), 1).Kind);
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("x"), 1);
        Assert.Equal(PeerStateKind.Searching,
            PeerStateReducer.Reduce(err, new PeerDiscovered("n", "a"), 2).Kind);
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("p", "x"), 1);
        Assert.Equal(linked,
            PeerStateReducer.Reduce(linked, new PeerDiscovered("n", "a"), 2));
    }

    [Fact]
    public void FiveHandshakeFailsTripAuthError()
    {
        var s = PeerUiState.Initial;
        for (int i = 1; i < PeerStateReducer.HandshakeFailThreshold; i++)
        {
            s = PeerStateReducer.Reduce(s, new HandshakeFailed("a", "auth"), i);
            Assert.Equal(PeerStateKind.Idle, s.Kind);
            Assert.Equal(i, s.ConsecutiveHandshakeFails);
        }
        s = PeerStateReducer.Reduce(s, new HandshakeFailed("a", "auth"), 5);
        Assert.Equal(PeerStateKind.Error, s.Kind);
        Assert.Equal("auth", s.Reason);
    }

    [Fact]
    public void LinkUpResetsFailCounter()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new HandshakeFailed("a", "auth"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("p", "x"), 2);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal(0, s.ConsecutiveHandshakeFails);
    }

    // Tray icon spec — parity with formacOS MenuIcon (red attention when
    // not linked; "!" marker on error).
    [Fact]
    public void TrayIconSpecMapping()
    {
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("p", "x"), 1);
        Assert.Equal(new TrayIconSpec(false, false), TrayIconSpec.For(linked));
        Assert.Equal(new TrayIconSpec(true, false), TrayIconSpec.For(PeerUiState.Initial));
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("x"), 1);
        Assert.Equal(new TrayIconSpec(true, true), TrayIconSpec.For(err));
    }
}
```

- [ ] **Step 2:** Run — compile failure.

- [ ] **Step 3: implement**

`VersionNegotiator.cs`:
```csharp
namespace AnyClip.Core;

public enum Compatibility
{
    Compatible, PeerOlderMinor, PeerNewerMinor, PeerOlderMajor, PeerNewerMajor,
}

/// Port of version_negotiator.py — keep the table in lockstep.
public static class VersionNegotiator
{
    public static Compatibility Negotiate(VersionInfo local, VersionInfo peer)
    {
        if (peer.ProtocolMajor < local.ProtocolMajor) return Compatibility.PeerOlderMajor;
        if (peer.ProtocolMajor > local.ProtocolMajor) return Compatibility.PeerNewerMajor;
        if (peer.ProtocolMinor < local.ProtocolMinor) return Compatibility.PeerOlderMinor;
        if (peer.ProtocolMinor > local.ProtocolMinor) return Compatibility.PeerNewerMinor;
        return Compatibility.Compatible;
    }

    public static bool LinkAllowed(Compatibility c) => c is
        Compatibility.Compatible or
        Compatibility.PeerOlderMinor or
        Compatibility.PeerNewerMinor;

    /// Python enum .value strings (used in HandshakeFailed "version:<x>").
    public static string WireValue(Compatibility c) => c switch
    {
        Compatibility.Compatible => "compatible",
        Compatibility.PeerOlderMinor => "peer_older_minor",
        Compatibility.PeerNewerMinor => "peer_newer_minor",
        Compatibility.PeerOlderMajor => "peer_older_major",
        Compatibility.PeerNewerMajor => "peer_newer_major",
        _ => "unknown",
    };
}
```

`PeerState.cs`:
```csharp
namespace AnyClip.Core;

public abstract record DaemonEvent;
public sealed record PeerDiscovered(string Name, string Addr) : DaemonEvent;
public sealed record LinkUp(string PeerName, string PeerId) : DaemonEvent;
public sealed record LinkDown(string Reason) : DaemonEvent;
public sealed record HandshakeFailed(string Addr, string Reason) : DaemonEvent;
public sealed record PermissionMissing(string Kind) : DaemonEvent;

public enum PeerStateKind { Idle, Searching, Linked, Error }

public sealed record PeerUiState(
    PeerStateKind Kind,
    string? PeerName = null,
    double? Since = null,
    string? Reason = null,
    int ConsecutiveHandshakeFails = 0)
{
    public static readonly PeerUiState Initial = new(PeerStateKind.Idle);
}

/// Pure reducer — port of peer_state.py.
public static class PeerStateReducer
{
    public const int HandshakeFailThreshold = 5;

    public static PeerUiState Reduce(PeerUiState prev, DaemonEvent ev, double now) => ev switch
    {
        PermissionMissing p => new PeerUiState(PeerStateKind.Error, Reason: p.Kind),
        LinkUp u => new PeerUiState(PeerStateKind.Linked, u.PeerName, now),
        LinkDown d => new PeerUiState(PeerStateKind.Searching, Reason: d.Reason),
        PeerDiscovered when prev.Kind is PeerStateKind.Idle or PeerStateKind.Error =>
            new PeerUiState(PeerStateKind.Searching),
        PeerDiscovered => prev,
        HandshakeFailed =>
            prev.ConsecutiveHandshakeFails + 1 >= HandshakeFailThreshold
                ? new PeerUiState(PeerStateKind.Error, Reason: "auth",
                    ConsecutiveHandshakeFails: prev.ConsecutiveHandshakeFails + 1)
                : prev with { ConsecutiveHandshakeFails = prev.ConsecutiveHandshakeFails + 1 },
        _ => prev,
    };
}

/// Tray rendering spec, parity with formacOS MenuIcon: attention (red)
/// whenever not linked; ErrorBang adds the "!" overlay.
public readonly record struct TrayIconSpec(bool Attention, bool ErrorBang)
{
    public static TrayIconSpec For(PeerUiState s) => s.Kind switch
    {
        PeerStateKind.Linked => new TrayIconSpec(false, false),
        PeerStateKind.Error => new TrayIconSpec(true, true),
        _ => new TrayIconSpec(true, false),
    };
}
```

- [ ] **Step 4:** Run — all pass (golden fixtures load from formacOS/).

- [ ] **Step 5: Commit** — `forwindows: golden vectors (shared fixtures) + negotiator + reducer + tray spec`

---

### Task 4: Core — EchoSuppressor, AuthGate, ConfigStore, TxtCodec, RotatingLog, TextHelpers

Pure-logic ports; every behavior is parity-pinned by the Python source and
the already-verified Swift port (read `formacOS/Sources/AnyClipCore/` for
the exact semantics, including the AuthGate sweep-before-count fix).

**Files:**
- Create: `forwindows/src/AnyClipCore/EchoSuppressor.cs`
- Create: `forwindows/src/AnyClipCore/AuthGate.cs`
- Create: `forwindows/src/AnyClipCore/ConfigStore.cs`
- Create: `forwindows/src/AnyClipCore/TxtCodec.cs`
- Create: `forwindows/src/AnyClipCore/RotatingLog.cs`
- Create: `forwindows/src/AnyClipCore/TextHelpers.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/PureLogicTests.cs`

- [ ] **Step 1: failing tests** — `PureLogicTests.cs`:

```csharp
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
```

NOTE on `SanitizeFilename("in:va/lid.txt")`: the basename rule must split on
BOTH '/' and '\\' but NOT on ':' (parity with `os.path.basename` semantics
for wire-received names — the Swift port behaves the same). Do not use
`Path.GetFileName` blindly on Windows-style logic; implement the split
explicitly so the test passes identically on macOS and Windows.

- [ ] **Step 2:** Run — compile failure.

- [ ] **Step 3: implement**

`EchoSuppressor.cs`:
```csharp
namespace AnyClip.Core;

/// Last-received hash per kind so the watcher never echoes a peer's update
/// back. Port of anyclip.EchoSuppressor. Caller provides synchronization.
public sealed class EchoSuppressor
{
    private readonly Dictionary<string, string> _last = new();
    public void MarkReceived(string kind, string payloadHash) => _last[kind] = payloadHash;
    public bool ShouldSend(string kind, string payloadHash) =>
        !_last.TryGetValue(kind, out var h) || h != payloadHash;
}
```

`AuthGate.cs`:
```csharp
namespace AnyClip.Core;

/// Per-IP cooldown after repeated handshake failures (5 fails → 60 s).
/// Port of anyclip.AuthGate with the Swift-port fix: RecordFail sweeps
/// expired entries BEFORE reading the old count, so a stale count never
/// carries into a new window. Caller provides synchronization.
public sealed class AuthGate(Func<double>? now = null)
{
    public const int MaxFails = 5;
    public const double CooldownSeconds = 60.0;

    private readonly Func<double> _now =
        now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
    private readonly Dictionary<string, (int Count, double Last)> _fails = new();

    public bool IsBlocked(string ip)
    {
        if (!_fails.TryGetValue(ip, out var e)) return false;
        if (_now() - e.Last >= CooldownSeconds) return false; // expired never blocks
        return e.Count >= MaxFails;
    }

    public void RecordFail(string ip)
    {
        Sweep();
        var count = _fails.TryGetValue(ip, out var e) ? e.Count : 0;
        _fails[ip] = (count + 1, _now());
    }

    public void RecordOk(string ip) => _fails.Remove(ip);

    private void Sweep()
    {
        var t = _now();
        foreach (var ip in _fails.Where(kv => t - kv.Value.Last >= CooldownSeconds)
                                 .Select(kv => kv.Key).ToList())
            _fails.Remove(ip);
    }
}
```

`ConfigStore.cs`:
```csharp
using System.Text.Json;

namespace AnyClip.Core;

/// Shared ~/.anyclip/config.json ({"token": "..."}), readable/writable by
/// the Python and Swift implementations. Port of config_store.py.
public static class ConfigStore
{
    public static string DefaultDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".anyclip");

    public static string ConfigPath(string? dir = null) =>
        Path.Combine(dir ?? DefaultDir(), "config.json");

    /// 32 random bytes, base64url without padding (secrets.token_urlsafe(32)).
    public static string GenerateToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// null when missing/corrupt/empty — a damaged file never blocks startup.
    public static string? Load(string? dir = null)
    {
        string path = ConfigPath(dir);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("token", out var tok)) return null;
            var token = tok.GetString();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    /// Atomic write: same-dir temp + flush-to-disk + File.Move(overwrite).
    /// chmod 0600 on Unix (no-op on Windows, like Python).
    public static void Save(string token, string? dir = null)
    {
        string targetDir = dir ?? DefaultDir();
        Directory.CreateDirectory(targetDir);
        string target = ConfigPath(targetDir);
        string tmp = Path.Combine(targetDir, $".config.json.{Guid.NewGuid()}.tmp");
        var payload = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["token"] = token },
            new JsonSerializerOptions { WriteIndented = true }) + "\n";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true); // fsync, like config_store.py
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }
}
```

`TxtCodec.cs`:
```csharp
using System.Text;

namespace AnyClip.Core;

/// Minimal DNS TXT codec (RFC 6763 §6): [len byte]["key=value"]. Entries
/// over 255 bytes are skipped; a zero-length entry ends the scan (we only
/// decode records we or zeroconf encoded). Port of the formacOS TXTCodec.
public static class TxtCodec
{
    public static byte[] Encode(IEnumerable<(string Key, string Value)> entries)
    {
        var output = new List<byte>();
        foreach (var (key, value) in entries)
        {
            var raw = Encoding.UTF8.GetBytes($"{key}={value}");
            if (raw.Length > 255) continue;
            output.Add((byte)raw.Length);
            output.AddRange(raw);
        }
        return output.ToArray();
    }

    public static Dictionary<string, string> Decode(byte[] data)
    {
        var result = new Dictionary<string, string>();
        int i = 0;
        while (i < data.Length)
        {
            int len = data[i];
            i += 1;
            if (len == 0 || i + len > data.Length) break;
            var s = Encoding.UTF8.GetString(data, i, len);
            int eq = s.IndexOf('=');
            if (eq > 0) result[s[..eq]] = s[(eq + 1)..];
            i += len;
        }
        return result;
    }
}
```

`RotatingLog.cs`:
```csharp
namespace AnyClip.Core;

/// Rotating file logger with the Python logging line shape
/// ("yyyy-MM-dd HH:mm:ss,fff LEVEL message"), 5 MB × 3 backups, writing the
/// same ~/.anyclip/anyclip.log. File level is always DEBUG; stderr mirrors
/// INFO+ (DEBUG too when verbose). Thread-safe via lock.
public sealed class RotatingLog(
    string filePath, int maxBytes = 5 * 1024 * 1024,
    int backupCount = 3, bool verbose = false)
{
    private readonly object _lock = new();

    /// Process-wide instance configured by the app entry point; defaults to
    /// a console-only logger so library tests never touch ~/.anyclip.
    public static RotatingLog Shared { get; set; } = new(filePath: "");

    public void Debug(string msg) => Write("DEBUG", msg, console: verbose);
    public void Info(string msg) => Write("INFO", msg, console: true);
    public void Warning(string msg) => Write("WARNING", msg, console: true);
    public void Error(string msg) => Write("ERROR", msg, console: true);

    private void Write(string level, string msg, bool console)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {msg}\n";
        lock (_lock)
        {
            if (console) Console.Error.Write(line);
            if (string.IsNullOrEmpty(filePath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.AppendAllText(filePath, line);
                RotateIfNeeded();
            }
            catch (IOException) { /* logging must never crash the daemon */ }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length <= maxBytes) return;
        try { File.Delete($"{filePath}.{backupCount}"); } catch (IOException) { }
        for (int i = backupCount - 1; i >= 1; i--)
        {
            var src = $"{filePath}.{i}";
            if (File.Exists(src))
                try { File.Move(src, $"{filePath}.{i + 1}", overwrite: true); }
                catch (IOException) { }
        }
        try { File.Move(filePath, $"{filePath}.1", overwrite: true); }
        catch (IOException) { }
    }
}
```

`TextHelpers.cs`:
```csharp
using System.Text;

namespace AnyClip.Core;

public static class TextHelpers
{
    /// One-line toast preview. Port of anyclip.preview().
    public static string Preview(string text, int maxLen = 80)
    {
        var snippet = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (snippet.Length == 0) return "(empty)";
        return snippet.Length <= maxLen ? snippet : snippet[..maxLen] + "...";
    }

    /// Basename (split on '/' and '\\', never ':') then replace anything
    /// outside [unicode-alnum . _ - space] with '_'. Port of the Python
    /// sanitizer in ClipboardWatcher.update_local_file.
    public static string SanitizeFilename(string name)
    {
        int cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        var basename = (cut >= 0 ? name[(cut + 1)..] : name).Trim();
        if (basename.Length == 0) return "received.bin";
        var sb = new StringBuilder(basename.Length);
        foreach (var ch in basename)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or ' '
                ? ch : '_');
        return sb.ToString();
    }
}
```

- [ ] **Step 4:** Run — all pass.
- [ ] **Step 5: Commit** — `forwindows: pure-logic core (suppressor, gate, config, txt, log, text)`

---

### Task 5: Core — FramedConnection (loopback-tested on macOS)

**Files:**
- Create: `forwindows/src/AnyClipCore/FramedConnection.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/FramedConnectionTests.cs`

- [ ] **Step 1: failing tests** — `FramedConnectionTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class FramedConnectionTests
{
    [Fact]
    public async Task SendAndReceiveOverLoopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 28601);
        listener.Start();
        try
        {
            var serverTask = listener.AcceptTcpClientAsync();
            using var client = await FramedConnection.ConnectAsync(
                "127.0.0.1", 28601, Wire.ConnectTimeoutSeconds, CancellationToken.None);
            using var server = new FramedConnection((await serverTask).Client);

            await client.SendFrameAsync(WireMessage.ClipText("ping-pong", 1), CancellationToken.None);
            var received = await server.ReceiveMessageAsync(CancellationToken.None);
            Assert.Equal("clip", received!.Type);
            Assert.Equal("ping-pong", received.Content);
            Assert.Equal("127.0.0.1", server.RemoteIp);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task EofThrows()
    {
        var listener = new TcpListener(IPAddress.Loopback, 28602);
        listener.Start();
        try
        {
            var serverTask = listener.AcceptTcpClientAsync();
            using var client = await FramedConnection.ConnectAsync(
                "127.0.0.1", 28602, 5, CancellationToken.None);
            (await serverTask).Close();
            await Assert.ThrowsAnyAsync<Exception>(
                () => client.ReceiveMessageAsync(CancellationToken.None));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task InvalidFrameLengthReturnsNull()
    {
        var listener = new TcpListener(IPAddress.Loopback, 28603);
        listener.Start();
        try
        {
            var serverTask = listener.AcceptTcpClientAsync();
            using var rawClient = new TcpClient();
            await rawClient.ConnectAsync("127.0.0.1", 28603);
            using var server = new FramedConnection((await serverTask).Client);
            // 4-byte header promising > MaxPayload.
            await rawClient.GetStream().WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            Assert.Null(await server.ReceiveMessageAsync(CancellationToken.None));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ConnectTimeoutThrows()
    {
        // RFC 5737 TEST-NET address: connect attempts hang, then time out.
        await Assert.ThrowsAnyAsync<Exception>(() => FramedConnection.ConnectAsync(
            "192.0.2.1", 28604, 0.3, CancellationToken.None));
    }
}
```

- [ ] **Step 2:** Run — compile failure.

- [ ] **Step 3: implement** — `FramedConnection.cs`:

```csharp
using System.Net.Sockets;

namespace AnyClip.Core;

/// Async framing over one connected Socket: 4-byte BE length + JSON body.
/// Port of PeerLink._send/_recv. EOF/socket errors throw (the session loop
/// treats any throw as end-of-session); invalid frames return null.
public sealed class FramedConnection : IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    public string? RemoteIp { get; }

    public FramedConnection(Socket socket)
    {
        _socket = socket;
        EnableKeepalive(socket);
        _stream = new NetworkStream(socket, ownsSocket: true);
        RemoteIp = (socket.RemoteEndPoint as System.Net.IPEndPoint)?
            .Address.ToString();
    }

    public static async Task<FramedConnection> ConnectAsync(
        string host, int port, double timeoutSeconds, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await socket.ConnectAsync(host, port, timeoutCts.Token);
            return new FramedConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// TCP keepalive, idle 15 s — same tuning as the Python/Swift ports.
    private static void EnableKeepalive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        }
        catch (SocketException) { /* best effort, like the other ports */ }
    }

    public async Task SendFrameAsync(WireMessage message, CancellationToken ct)
    {
        byte[] frame = message.EncodeFrame(); // PayloadTooLargeException propagates
        await _stream.WriteAsync(frame, ct);
    }

    public async Task<WireMessage?> ReceiveMessageAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await _stream.ReadExactlyAsync(header, ct); // EndOfStreamException on EOF
        int n = WireMessage.FrameLength(header);
        if (n <= 0 || n > Wire.MaxPayload)
        {
            RotatingLog.Shared.Warning($"invalid frame length: {n}");
            return null;
        }
        var body = new byte[n];
        await _stream.ReadExactlyAsync(body, ct);
        var msg = WireMessage.DecodeBody(body);
        if (msg is null) RotatingLog.Shared.Warning($"bad json frame ({n} bytes)");
        return msg;
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch (IOException) { }
        try { _socket.Dispose(); } catch (ObjectDisposedException) { }
    }
}
```

- [ ] **Step 4:** Run — all pass (note: `ConnectTimeoutThrows` may take ~0.3 s).
- [ ] **Step 5: Commit** — `forwindows: framed socket connection`

---

### Task 6: Core — PeerLink (two-link loopback tests on macOS)

**Files:**
- Create: `forwindows/src/AnyClipCore/PeerLink.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/PeerLinkTests.cs`

Behavioral reference: `anyclip.py:1086-1511` and the parity-verified
`formacOS/Sources/AnyClipDaemon/PeerLink.swift` — handshake order, auth
gate (inbound only), tie-break truth table, post-window zombie replacement,
receive-loop dispatch, cleanup-only-if-active, send semantics, and the
bind-retry (4 × 0.5 s on address-in-use before FatalStartupError).

- [ ] **Step 1: failing tests** — `PeerLinkTests.cs`:

```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerLinkTests
{
    private static (PeerLink Link, List<ClipPayload> Clips, List<DaemonEvent> Events)
        MakeLink(string token, int port, string name)
    {
        var clips = new List<ClipPayload>();
        var events = new List<DaemonEvent>();
        var link = new PeerLink(
            new PeerLink.LinkConfig(token, port, name, "0.0.0-test"),
            Guid.NewGuid().ToString());
        link.OnClip = p => { lock (clips) clips.Add(p); return Task.CompletedTask; };
        link.Emit = e => { lock (events) events.Add(e); };
        return (link, clips, events);
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, double timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }

    [Fact]
    public async Task TwoLinksHandshakeAndExchangeClips()
    {
        var (a, aClips, aEvents) = MakeLink("tok", 28611, "node-a");
        var (b, bClips, _) = MakeLink("tok", 28612, "node-b");
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        _ = b.TryConnectAsync("127.0.0.1", 28611, "127.0.0.1:28611", cts.Token);
        Assert.True(await WaitUntil(() => a.IsActive && b.IsActive));
        Assert.Equal("node-b", a.PeerName);
        Assert.Equal("node-a", b.PeerName);
        lock (aEvents) Assert.Contains(aEvents, e => e is LinkUp);

        await b.SendClipAsync(new TextClip("from-b"));
        Assert.True(await WaitUntil(() =>
        {
            lock (aClips) return aClips.Any(c => c is TextClip t && t.Text == "from-b");
        }));

        await a.SendClipAsync(new ImageClip(new byte[] { 1, 2, 3 }));
        Assert.True(await WaitUntil(() =>
        {
            lock (bClips) return bClips.Any(c =>
                c is ImageClip i && i.Png.SequenceEqual(new byte[] { 1, 2, 3 }));
        }));

        a.Shutdown(); b.Shutdown();
        cts.Cancel();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task WrongTokenRejectedWithAuthEvent()
    {
        var (a, _, aEvents) = MakeLink("right", 28613, "a");
        var (b, _, _) = MakeLink("wrong", 28614, "b");
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        await b.TryConnectAsync("127.0.0.1", 28613, "127.0.0.1:28613", cts.Token);
        Assert.True(await WaitUntil(() =>
        {
            lock (aEvents) return aEvents.Any(e =>
                e is HandshakeFailed { Reason: "auth" });
        }));
        Assert.False(a.IsActive);
        a.Shutdown(); b.Shutdown(); cts.Cancel();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PingAnsweredWithPongAndMajorMismatchRefused()
    {
        var (a, _, aEvents) = MakeLink("tok", 28615, "a");
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        // Raw client completes the handshake manually, sends ping.
        using var raw = await FramedConnection.ConnectAsync("127.0.0.1", 28615, 5, cts.Token);
        await raw.SendFrameAsync(WireMessage.Hello(
            Hashing.Sha256Hex("tok"), "ffffffff-raw", "raw", "0.0.0-test"), cts.Token);
        var serverHello = await raw.ReceiveMessageAsync(cts.Token);
        Assert.Equal("hello", serverHello!.Type);
        await raw.SendFrameAsync(WireMessage.Ping(1), cts.Token);
        var reply = await raw.ReceiveMessageAsync(cts.Token);
        Assert.Equal("pong", reply!.Type);
        raw.Dispose();
        Assert.True(await WaitUntil(() => !a.IsActive));

        // Major-mismatch hello is refused with a version: event.
        using var raw2 = await FramedConnection.ConnectAsync("127.0.0.1", 28615, 5, cts.Token);
        var badHello = WireMessage.Hello(
            Hashing.Sha256Hex("tok"), "ffffffff-v2", "future", "2.0.0")
            with { ProtocolMajor = 2 };
        await raw2.SendFrameAsync(badHello, cts.Token);
        _ = await raw2.ReceiveMessageAsync(cts.Token);
        Assert.True(await WaitUntil(() =>
        {
            lock (aEvents) return aEvents.Any(e =>
                e is HandshakeFailed h && h.Reason.StartsWith("version:"));
        }));
        Assert.False(a.IsActive);
        a.Shutdown(); cts.Cancel();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ServeRetriesBindWhenPortTemporarilyHeld()
    {
        var blocker = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Any, 28616);
        blocker.Start();
        var (a, _, _) = MakeLink("t", 28616, "retry");
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        await Task.Delay(700);
        blocker.Stop();
        Assert.True(await WaitUntil(() => a.IsServing, 5));
        a.Shutdown(); cts.Cancel();
        try { await serveA; } catch (OperationCanceledException) { }
    }
}
```

- [ ] **Step 2:** Run — compile failure.

- [ ] **Step 3: implement** — `PeerLink.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AnyClip.Core;

/// Raised when the daemon cannot start and retrying will not help.
public sealed class FatalStartupException(string message) : Exception(message);

/// Owns the single active TCP link to a peer; acts as server and client;
/// resolves simultaneous-connect via lexicographic node-id tie-break.
/// Port of anyclip.PeerLink — SemaphoreSlim mirrors the asyncio.Lock and
/// the registration critical section contains no awaits.
public sealed class PeerLink(PeerLink.LinkConfig config, string nodeId)
{
    public sealed record LinkConfig(string Token, int Port, string Name, string AppVersion);

    private readonly string _tokenHash = Hashing.Sha256Hex(config.Token);
    private readonly AuthGate _authGate = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _connectingLock = new();
    private readonly HashSet<string> _connecting = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private FramedConnection? _activeConn;
    private string? _peerNodeId;
    private double _linkedAt;
    private TcpListener? _listener;

    public Func<ClipPayload, Task>? OnClip { get; set; }
    public Action<DaemonEvent>? Emit { get; set; }
    public volatile bool IsServing;
    public string? PeerName { get; private set; }
    public bool IsActive => _activeConn is not null;

    private static double MonotonicNow() => Clock.Elapsed.TotalSeconds;
    private static double UnixNow() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public async Task ServeAsync(CancellationToken ct)
    {
        if (_listener is not null)
            throw new FatalStartupException("ServeAsync called twice on the same PeerLink");

        TcpListener? listener = null;
        for (int attempt = 0; ; attempt++)
        {
            listener = new TcpListener(IPAddress.Any, config.Port);
            try
            {
                listener.Start();
                break;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                listener.Stop();
                if (attempt >= 4)
                    throw new FatalStartupException(
                        $"port {config.Port} still in use after cleanup attempt; "
                        + "another process may have grabbed it");
                RotatingLog.Shared.Info(
                    $"tcp/{config.Port} still in use; retrying bind ({attempt + 1}/4)");
                await Task.Delay(500, ct);
            }
        }
        _listener = listener;
        IsServing = true;
        RotatingLog.Shared.Info($"listening on tcp/{config.Port}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket socket;
                try { socket = await listener.AcceptSocketAsync(ct); }
                catch (SocketException) { continue; } // listener bounced
                _ = Task.Run(() => HandleInboundAsync(socket, ct), ct);
            }
        }
        finally
        {
            listener.Stop();
            _listener = null;
            IsServing = false;
        }
    }

    private async Task HandleInboundAsync(Socket socket, CancellationToken ct)
    {
        FramedConnection framed;
        try { framed = new FramedConnection(socket); }
        catch (SocketException) { socket.Dispose(); return; }
        RotatingLog.Shared.Debug($"inbound from {framed.RemoteIp ?? "?"}");
        bool blocked;
        await _lock.WaitAsync(ct);
        try { blocked = framed.RemoteIp is not null && _authGate.IsBlocked(framed.RemoteIp); }
        finally { _lock.Release(); }
        if (blocked)
        {
            RotatingLog.Shared.Info(
                $"auth gate: {framed.RemoteIp} blocked (>{AuthGate.MaxFails} failures, "
                + $"cooldown {(int)AuthGate.CooldownSeconds}s)");
            framed.Dispose();
            return;
        }
        await SessionAsync(framed, inbound: true, ct);
        framed.Dispose();
    }

    public async Task TryConnectAsync(string host, int port, string label, CancellationToken ct)
    {
        if (IsActive) return;
        lock (_connectingLock)
        {
            if (!_connecting.Add(label))
            {
                RotatingLog.Shared.Debug($"connect to {label} already in flight, skipping");
                return;
            }
        }
        try
        {
            FramedConnection framed;
            try
            {
                framed = await FramedConnection.ConnectAsync(
                    host, port, Wire.ConnectTimeoutSeconds, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                RotatingLog.Shared.Info($"connect to {label} failed: {e.Message}");
                return;
            }
            RotatingLog.Shared.Debug($"outbound connected to {label}");
            await SessionAsync(framed, inbound: false, ct);
            framed.Dispose();
        }
        finally
        {
            lock (_connectingLock) _connecting.Remove(label);
        }
    }

    private async Task SessionAsync(FramedConnection framed, bool inbound, CancellationToken ct)
    {
        try
        {
            await framed.SendFrameAsync(WireMessage.Hello(
                _tokenHash, nodeId, config.Name, config.AppVersion), ct);
        }
        catch { return; }
        string addr = framed.RemoteIp ?? "";

        WireMessage? hello;
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsCts.CancelAfter(TimeSpan.FromSeconds(Wire.HandshakeTimeoutSeconds));
            hello = await framed.ReceiveMessageAsync(hsCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RotatingLog.Shared.Warning("handshake timeout");
            Emit?.Invoke(new HandshakeFailed(addr, "timeout"));
            return;
        }
        catch { return; }

        if (hello is null || hello.Type != "hello")
        {
            RotatingLog.Shared.Warning("invalid hello, closing");
            Emit?.Invoke(new HandshakeFailed(addr, "invalid"));
            return;
        }
        string? peerIp = inbound ? framed.RemoteIp : null;
        if (hello.Token != _tokenHash)
        {
            RotatingLog.Shared.Warning($"auth failed from peer name={hello.Name ?? "?"}");
            if (peerIp is not null)
            {
                await _lock.WaitAsync(CancellationToken.None);
                try { _authGate.RecordFail(peerIp); } finally { _lock.Release(); }
            }
            Emit?.Invoke(new HandshakeFailed(peerIp ?? addr, "auth"));
            return;
        }
        var peerVersion = hello.PeerVersionInfo();
        var localVersion = new VersionInfo(
            config.AppVersion, Wire.ProtocolMajor, Wire.ProtocolMinor);
        var compat = VersionNegotiator.Negotiate(localVersion, peerVersion);
        if (!VersionNegotiator.LinkAllowed(compat))
        {
            RotatingLog.Shared.Warning(
                $"version refused: local proto={Wire.ProtocolMajor}.{Wire.ProtocolMinor} "
                + $"vs peer proto={peerVersion.ProtocolMajor}.{peerVersion.ProtocolMinor} "
                + $"app={peerVersion.AppVersion} -> {VersionNegotiator.WireValue(compat)}");
            Emit?.Invoke(new HandshakeFailed(
                addr, $"version:{VersionNegotiator.WireValue(compat)}"));
            return;
        }
        if (compat != Compatibility.Compatible)
            RotatingLog.Shared.Info(
                $"version mismatch (link kept): {VersionNegotiator.WireValue(compat)}");
        var peerId = hello.NodeId;
        if (string.IsNullOrEmpty(peerId) || peerId == nodeId)
        {
            RotatingLog.Shared.Debug("self loopback or bad node_id, dropping");
            return;
        }
        if (peerIp is not null)
        {
            await _lock.WaitAsync(CancellationToken.None);
            try { _authGate.RecordOk(peerIp); } finally { _lock.Release(); }
        }

        // Registration / tie-break — no awaits between read and write.
        string displayName = string.IsNullOrEmpty(hello.Name)
            ? peerId[..Math.Min(8, peerId.Length)] : hello.Name!;
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            if (_activeConn is not null)
            {
                bool race = MonotonicNow() - _linkedAt < Wire.RaceWindowSeconds;
                if (race)
                {
                    bool keepThisLink =
                        (!inbound && string.CompareOrdinal(nodeId, peerId) < 0) ||
                        (inbound && string.CompareOrdinal(nodeId, peerId) > 0);
                    if (!keepThisLink)
                    {
                        RotatingLog.Shared.Debug("tie-breaker: dropping duplicate link (race)");
                        return;
                    }
                    RotatingLog.Shared.Debug("tie-breaker: replacing existing link (race)");
                }
                else
                {
                    RotatingLog.Shared.Info(
                        $"tie-breaker: stale link to {PeerName ?? "?"} replaced by "
                        + $"fresh handshake from {hello.Name ?? "?"}");
                }
                _activeConn.Dispose();
            }
            _activeConn = framed;
            _peerNodeId = peerId;
            PeerName = displayName;
            _linkedAt = MonotonicNow();
        }
        finally { _lock.Release(); }

        RotatingLog.Shared.Info(
            $"linked with peer name={displayName} id={peerId[..Math.Min(8, peerId.Length)]} "
            + $"({(inbound ? "inbound" : "outbound")}) "
            + $"peer_app_version={peerVersion.AppVersion} "
            + $"peer_proto={peerVersion.ProtocolMajor}.{peerVersion.ProtocolMinor}");
        Emit?.Invoke(new LinkUp(displayName, peerId));

        // Receive loop.
        while (true)
        {
            WireMessage? msg;
            try { msg = await framed.ReceiveMessageAsync(ct); }
            catch { break; }
            if (msg is null) break;
            switch (msg.Type)
            {
                case "clip":
                    await HandleClipAsync(msg);
                    break;
                case "ping":
                    try { await framed.SendFrameAsync(WireMessage.Pong(UnixNow()), ct); }
                    catch (Exception e)
                    { RotatingLog.Shared.Info($"send failed (link likely down): {e.Message}"); }
                    break;
                case "pong":
                    break; // presence is enough
                default:
                    RotatingLog.Shared.Debug($"ignoring message type: {msg.Type}");
                    break;
            }
        }

        bool wasActive;
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            wasActive = ReferenceEquals(_activeConn, framed);
            if (wasActive)
            {
                _activeConn = null;
                _peerNodeId = null;
                PeerName = null;
            }
        }
        finally { _lock.Release(); }
        RotatingLog.Shared.Info("peer disconnected");
        if (wasActive) Emit?.Invoke(new LinkDown("peer disconnected"));
    }

    private async Task HandleClipAsync(WireMessage msg)
    {
        var kind = msg.Kind ?? "text";
        switch (kind)
        {
            case "text" when msg.Content is not null:
                await (OnClip?.Invoke(new TextClip(msg.Content)) ?? Task.CompletedTask);
                break;
            case "image" when msg.Content is not null:
                if (WireMessage.StrictBase64Decode(msg.Content) is { } png)
                    await (OnClip?.Invoke(new ImageClip(png)) ?? Task.CompletedTask);
                else RotatingLog.Shared.Warning("bad image payload from peer");
                break;
            case "file" when msg.Content is not null:
                if (WireMessage.StrictBase64Decode(msg.Content) is { } data)
                {
                    var name = string.IsNullOrEmpty(msg.Name) ? "received.bin" : msg.Name!;
                    await (OnClip?.Invoke(new FileClip(name, data)) ?? Task.CompletedTask);
                }
                else RotatingLog.Shared.Warning("bad file payload from peer");
                break;
            default:
                RotatingLog.Shared.Debug($"ignoring clip with kind={kind}");
                break;
        }
    }

    public async Task SendClipAsync(ClipPayload payload)
    {
        var conn = _activeConn;
        if (conn is null) return;
        try { await conn.SendFrameAsync(WireMessage.Clip(payload, UnixNow()), CancellationToken.None); }
        catch (PayloadTooLargeException e)
        { RotatingLog.Shared.Warning($"payload too large, dropping: {e.Message}"); }
        catch (Exception e)
        { RotatingLog.Shared.Info($"send failed (link likely down): {e.Message}"); }
    }

    public async Task SendPingAsync()
    {
        var conn = _activeConn;
        if (conn is null) return;
        try { await conn.SendFrameAsync(WireMessage.Ping(UnixNow()), CancellationToken.None); }
        catch (Exception e)
        { RotatingLog.Shared.Info($"send failed (link likely down): {e.Message}"); }
    }

    public void Shutdown()
    {
        _activeConn?.Dispose();
        _activeConn = null;
        _peerNodeId = null;
        PeerName = null;
        _listener?.Stop();
        IsServing = false;
    }
}
```

- [ ] **Step 4:** Run twice — all pass both times (real sockets).
- [ ] **Step 5: Commit** — `forwindows: PeerLink (handshake, tie-break, auth gate, bind retry)`

---

### Task 7: Core — PeerDirectory + Watchdogs

**Files:**
- Create: `forwindows/src/AnyClipCore/PeerDirectory.cs`
- Create: `forwindows/src/AnyClipCore/Watchdogs.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/PeerDirectoryTests.cs`

PeerDirectory is the mDNS bookkeeping extracted into Core (so it is
macOS-testable); the App's MdnsBeacon feeds discoveries into it.
Reference: anyclip.py:1594-1630 (_resolve) + 1767-1838 (reconnect loop)
and `formacOS/Sources/AnyClipDaemon/MdnsBeacon.swift` / `Watchdogs.swift`.

- [ ] **Step 1: failing tests** — `PeerDirectoryTests.cs`:

```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerDirectoryTests
{
    [Fact]
    public async Task SelfAdvertisementIgnoredWithoutEvidence()
    {
        var dir = new PeerDirectory("self-node", _ => { }, (_, _, _) => Task.CompletedTask);
        await dir.IngestAsync("self-node", "1.2.3.4", 24816, "x");
        Assert.Equal(0, dir.EventsSeen);
        Assert.Empty(dir.PeersSnapshot());
    }

    [Fact]
    public async Task NonSelfPeerRecordedAndOnPeerFired()
    {
        var fired = new List<string>();
        var events = new List<DaemonEvent>();
        var dir = new PeerDirectory("self",
            e => events.Add(e),
            (host, port, label) => { fired.Add($"{host}:{port}:{label}"); return Task.CompletedTask; });
        await dir.IngestAsync("other", "1.2.3.4", 24816, "peer-1");
        Assert.Equal(1, dir.EventsSeen);
        Assert.Single(dir.PeersSnapshot());
        Assert.Equal("peer-1", dir.PeersSnapshot()[0].Label);
        Assert.Contains(events, e => e is PeerDiscovered);
        Assert.Equal(new[] { "1.2.3.4:24816:peer-1" }, fired);
    }

    [Fact]
    public async Task FreshDiscoveryClearsFailCountAndPruneRemovesAllIds()
    {
        var dir = new PeerDirectory("self", _ => { }, (_, _, _) => Task.CompletedTask);
        await dir.IngestAsync("p1", "1.2.3.4", 24816, "addr");
        Assert.Equal(1, dir.RecordFail("addr"));
        Assert.Equal(2, dir.RecordFail("addr"));
        await dir.IngestAsync("p1", "1.2.3.4", 24816, "addr");
        Assert.Equal(1, dir.RecordFail("addr")); // reset by rediscovery

        await dir.IngestAsync("p2", "1.2.3.4", 24816, "addr"); // restarted peer, same addr
        Assert.Single(dir.PeersSnapshot()); // deduped by label
        dir.PruneAddress("addr");
        Assert.Empty(dir.PeersSnapshot());
    }
}
```

- [ ] **Step 2:** Run — compile failure.

- [ ] **Step 3: implement**

`PeerDirectory.cs`:
```csharp
namespace AnyClip.Core;

/// mDNS discovery bookkeeping (knownPeers / addressFails / eventsSeen),
/// platform-neutral so it is testable on macOS. The Windows MdnsBeacon
/// calls IngestAsync from its browse callbacks. Port of the bookkeeping
/// half of anyclip.MdnsBeacon. Thread-safe via lock.
public sealed class PeerDirectory(
    string nodeId,
    Action<DaemonEvent> emit,
    Func<string, int, string, Task> onPeer)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Host, int Port, string Label)> _knownPeers = new();
    private readonly Dictionary<string, int> _addressFails = new();
    public int EventsSeen { get; private set; }

    public async Task IngestAsync(string peerId, string host, int port, string label)
    {
        lock (_lock)
        {
            if (peerId == nodeId) return; // self-loopback: no evidence, no record
            EventsSeen++;
            _knownPeers[peerId] = (host, port, label);
            _addressFails.Remove(label);
        }
        RotatingLog.Shared.Info($"discovered peer {label}");
        emit(new PeerDiscovered(label, label));
        await onPeer(host, port, label);
    }

    /// Known peers deduped by address label (a restarted remote daemon
    /// leaves stale node ids behind for the same address).
    public List<(string Host, int Port, string Label)> PeersSnapshot()
    {
        lock (_lock)
        {
            var seen = new HashSet<string>();
            var result = new List<(string, int, string)>();
            foreach (var v in _knownPeers.Values)
                if (seen.Add(v.Label)) result.Add(v);
            return result;
        }
    }

    public int RecordFail(string label)
    {
        lock (_lock)
        {
            var n = _addressFails.GetValueOrDefault(label) + 1;
            _addressFails[label] = n;
            return n;
        }
    }

    public void ClearFails(string label) { lock (_lock) _addressFails.Remove(label); }

    public void PruneAddress(string label)
    {
        lock (_lock)
        {
            foreach (var id in _knownPeers.Where(kv => kv.Value.Label == label)
                                          .Select(kv => kv.Key).ToList())
                _knownPeers.Remove(id);
            _addressFails.Remove(label);
        }
    }
}
```

`Watchdogs.cs`:
```csharp
using System.Diagnostics;

namespace AnyClip.Core;

/// Thrown by watchdogs to unwind the daemon task set; the in-process
/// supervisor restarts with backoff (Python: RuntimeError → supervisor).
public sealed class DaemonRestartException(string message) : Exception(message);

/// mDNS service control implemented by the platform layer (Windows
/// MdnsBeacon over dnsapi; fakes in tests).
public interface IMdnsService
{
    string? AdvertisedIp { get; }
    Task StartAsync(string instanceName, IReadOnlyList<(string Key, string Value)> txt);
    void Refresh();
    void Stop();
}

/// Loops are exact ports of anyclip.py:1679-1862 / formacOS Watchdogs.swift.
public static class Watchdogs
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double MonotonicNow() => Clock.Elapsed.TotalSeconds;

    public static async Task LinkPingLoopAsync(
        PeerLink link, double intervalSeconds, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            if (link.IsActive) await link.SendPingAsync();
        }
    }

    public static async Task NetworkWatchdogAsync(
        IMdnsService mdns, Func<string?> primaryIPv4,
        double intervalSeconds, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            var previous = mdns.AdvertisedIp;
            if (previous is null) continue;
            var current = primaryIPv4();
            if (current is not null && current != previous)
                throw new DaemonRestartException(
                    $"local IPv4 changed: {previous} -> {current}; "
                    + "restarting daemon to re-advertise mDNS");
        }
    }

    public static async Task IdleLinkWatchdogAsync(
        IMdnsService mdns, PeerLink link,
        double idleThresholdSeconds, int refreshAttempts, CancellationToken ct)
    {
        int consecutiveIdle = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(idleThresholdSeconds), ct);
            if (link.IsActive) { consecutiveIdle = 0; continue; }
            consecutiveIdle++;
            if (consecutiveIdle <= refreshAttempts)
            {
                RotatingLog.Shared.Info(
                    $"link idle {(int)(idleThresholdSeconds * consecutiveIdle)}s; "
                    + $"refreshing mDNS (attempt {consecutiveIdle}/{refreshAttempts})");
                mdns.Refresh();
            }
            else
            {
                throw new DaemonRestartException(
                    $"link idle with no recovery after {refreshAttempts} mDNS "
                    + "refresh attempts; bouncing daemon");
            }
        }
    }

    public static async Task MdnsReconnectLoopAsync(
        PeerDirectory directory, PeerLink link, CancellationToken ct)
    {
        double backoff = 1;
        while (true)
        {
            if (link.IsActive)
            {
                backoff = 1;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            var peers = directory.PeersSnapshot();
            if (peers.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            bool attempted = false;
            foreach (var (host, port, label) in peers)
            {
                if (link.IsActive) break;
                attempted = true;
                double start = MonotonicNow();
                await link.TryConnectAsync(host, port, label, ct);
                double elapsed = MonotonicNow() - start;
                if (link.IsActive)
                {
                    directory.ClearFails(label);
                    if (elapsed > 5) backoff = 1;
                    break;
                }
                if (elapsed > 5)
                {
                    // Long session that later died — healthy peer, not a
                    // prune candidate.
                    directory.ClearFails(label);
                    continue;
                }
                int fails = directory.RecordFail(label);
                if (fails >= Wire.MaxReconnectFails)
                {
                    directory.PruneAddress(label);
                    RotatingLog.Shared.Info(
                        $"pruned stale peer address {label} after {fails} failed "
                        + "attempts; awaiting fresh mDNS discovery");
                }
            }
            if (link.IsActive) continue;
            if (attempted)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(backoff, 60)), ct);
                backoff = Math.Min(backoff * 2, 60);
            }
            else await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
```

- [ ] **Step 4:** Run — all pass.
- [ ] **Step 5: Commit** — `forwindows: peer directory + watchdog loops`

---

### Task 8: Core — Daemon assembly (injected platform interfaces)

**Files:**
- Create: `forwindows/src/AnyClipCore/Daemon.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/DaemonTests.cs`

Mirror of formacOS `Daemon.swift` (run-once task set + 1→60 s supervisor)
with the platform surface injected so the whole assembly runs in macOS
tests with fakes. Notification strings are spec-mandated and must be
byte-identical to the other ports.

- [ ] **Step 1: failing tests** — `DaemonTests.cs`:

```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

internal sealed class FakeClipboard : IClipboardSync
{
    public Func<ClipPayload, Task>? OnLocalChange { get; set; }
    public Func<string, Task>? OnFileSkipped { get; set; }
    public List<ClipPayload> Applied { get; } = new();
    public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    public Task<bool> ApplyRemoteAsync(ClipPayload payload)
    {
        lock (Applied) Applied.Add(payload);
        return Task.FromResult(true);
    }
}

internal sealed class FakeMdns : IMdnsService
{
    public string? AdvertisedIp => "127.0.0.1";
    public bool Started; public bool Stopped; public int Refreshes;
    public Task StartAsync(string instanceName, IReadOnlyList<(string, string)> txt)
    { Started = true; return Task.CompletedTask; }
    public void Refresh() => Refreshes++;
    public void Stop() => Stopped = true;
}

internal sealed class FakePidLock : IPidLock
{
    public bool Prepared; public bool Released;
    public void Prepare(int port) => Prepared = true;
    public void Release() => Released = true;
}

public class DaemonTests
{
    [Fact]
    public async Task SyncCoordinatorSuppressesEcho()
    {
        var c = new SyncCoordinator();
        c.MarkReceived("text", "h1");
        Assert.False(c.ShouldSend("text", "h1"));
        Assert.True(c.ShouldSend("text", "h2"));
        Assert.True(c.ShouldSend("image", "h1"));
    }

    [Fact]
    public void ClearDirectoryFilesKeepsSubdirs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anyclip-clear-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        Daemon.ClearDirectoryFiles(dir);
        Assert.Equal(new[] { "sub" },
            Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task DaemonStartsServesAndShutsDownCleanly()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "anyclip-daemon-" + Guid.NewGuid());
        var pid = new FakePidLock();
        var mdns = new FakeMdns();
        var clip = new FakeClipboard();
        var daemon = new Daemon(
            new DaemonConfig("test-token", 28621, "daemon-test", NotificationsEnabled: false),
            appVersion: "0.0.0-test", stateDir: stateDir,
            clipboard: clip, mdns: mdns, pidLock: pid,
            primaryIPv4: () => "127.0.0.1",
            notify: (_, _) => { }, onFatal: _ => { });

        using var cts = new CancellationTokenSource();
        var run = daemon.RunForeverAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !(pid.Prepared && mdns.Started))
            await Task.Delay(50);
        Assert.True(pid.Prepared);
        Assert.True(mdns.Started);

        cts.Cancel();
        await run; // RunForeverAsync swallows cancellation and returns
        Assert.True(pid.Released);
        Assert.True(mdns.Stopped);
    }

    [Fact]
    public async Task FatalStopsSupervisorAndCallsOnFatal()
    {
        string? fatal = null;
        var pid = new ThrowingPidLock();
        var daemon = new Daemon(
            new DaemonConfig("t", 28622, "x", NotificationsEnabled: false),
            "0.0.0-test", Path.GetTempPath(),
            new FakeClipboard(), new FakeMdns(), pid,
            () => null, (_, _) => { }, m => fatal = m);
        await daemon.RunForeverAsync(CancellationToken.None);
        Assert.NotNull(fatal);
    }

    private sealed class ThrowingPidLock : IPidLock
    {
        public void Prepare(int port) => throw new FatalStartupException("boom");
        public void Release() { }
    }
}
```

- [ ] **Step 2:** Run — compile failure.

- [ ] **Step 3: implement** — `Daemon.cs`:

```csharp
using System.Threading.Channels;

namespace AnyClip.Core;

/// Platform surface injected into the daemon so the assembly is testable
/// off-Windows. The WinForms layer provides the real implementations.
public interface IClipboardSync
{
    Func<ClipPayload, Task>? OnLocalChange { get; set; }
    Func<string, Task>? OnFileSkipped { get; set; }
    /// Long-running pump (or infinite wait when events come from the UI loop).
    Task RunAsync(CancellationToken ct);
    /// Write a remote payload to the local clipboard; false = write failed.
    Task<bool> ApplyRemoteAsync(ClipPayload payload);
}

public interface IPidLock
{
    void Prepare(int port); // throws FatalStartupException on foreign conflicts
    void Release();
}

public sealed record DaemonConfig(
    string Token,
    int Port,
    string Name,
    bool NotificationsEnabled = true,
    double PollIntervalSeconds = 0.5);

/// Echo-suppression shared by inbound and outbound paths. Lock-based.
public sealed class SyncCoordinator
{
    private readonly object _lock = new();
    private readonly EchoSuppressor _suppressor = new();
    public void MarkReceived(string kind, string hash)
    { lock (_lock) _suppressor.MarkReceived(kind, hash); }
    public bool ShouldSend(string kind, string hash)
    { lock (_lock) return _suppressor.ShouldSend(kind, hash); }
}

/// Assembles one daemon runtime and supervises it with 1→60 s backoff.
/// Port of formacOS Daemon.swift / anyclip.run()+main().
public sealed class Daemon(
    DaemonConfig config,
    string appVersion,
    string stateDir,
    IClipboardSync clipboard,
    IMdnsService mdns,
    IPidLock pidLock,
    Func<string?> primaryIPv4,
    Action<string, string> notify,
    Action<string> onFatal)
{
    private readonly Channel<DaemonEvent> _events =
        Channel.CreateUnbounded<DaemonEvent>();
    public ChannelReader<DaemonEvent> Events => _events.Reader;

    public static void ClearDirectoryFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir))
            try { File.Delete(f); } catch (IOException) { }
    }

    public async Task RunForeverAsync(CancellationToken ct)
    {
        double backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (FatalStartupException e)
            {
                RotatingLog.Shared.Error($"fatal: {e.Message}");
                onFatal(e.Message);
                return;
            }
            catch (Exception e)
            {
                if (ct.IsCancellationRequested) return;
                RotatingLog.Shared.Error(
                    $"daemon crashed: {e.Message}; restarting in {(int)backoff}s");
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
                catch (OperationCanceledException) { return; }
                backoff = Math.Min(backoff * 2, 60);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken outerCt)
    {
        pidLock.Prepare(config.Port);
        string receivedDir = Path.Combine(stateDir, "received");
        ClearDirectoryFiles(receivedDir);

        string nodeId = Guid.NewGuid().ToString().ToLowerInvariant();
        var coordinator = new SyncCoordinator();
        Action<DaemonEvent> emit = e => _events.Writer.TryWrite(e);
        Action<string, string> toast = config.NotificationsEnabled
            ? notify : (_, _) => { };

        var link = new PeerLink(
            new PeerLink.LinkConfig(config.Token, config.Port, config.Name, appVersion),
            nodeId);
        link.Emit = emit;
        link.OnClip = async payload =>
        {
            coordinator.MarkReceived(payload.Kind, payload.PayloadHash);
            string peer = link.PeerName ?? "peer";
            bool ok = await clipboard.ApplyRemoteAsync(payload);
            switch (payload)
            {
                case TextClip t:
                    RotatingLog.Shared.Info(
                        $"<- received text {t.Text.Length} chars from {peer}");
                    toast($"AnyClip ← {peer}", TextHelpers.Preview(t.Text));
                    break;
                case ImageClip i:
                    RotatingLog.Shared.Info(
                        $"<- received image {i.Png.Length} bytes from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"image ({i.Png.Length / 1024} KB)");
                    break;
                case FileClip f:
                    RotatingLog.Shared.Info(
                        $"<- received file {f.Name} {f.Data.Length} bytes from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"file: {f.Name} ({f.Data.Length / 1024} KB)");
                    break;
            }
        };

        clipboard.OnLocalChange = async payload =>
        {
            if (!link.IsActive) return;
            if (!coordinator.ShouldSend(payload.Kind, payload.PayloadHash))
            {
                RotatingLog.Shared.Debug($"skip echo of just-received {payload.Kind}");
                return;
            }
            await link.SendClipAsync(payload);
            string peer = link.PeerName ?? "peer";
            switch (payload)
            {
                case TextClip t:
                    RotatingLog.Shared.Info($"-> sent text {t.Text.Length} chars to {peer}");
                    toast($"AnyClip → {peer}", TextHelpers.Preview(t.Text));
                    break;
                case ImageClip i:
                    RotatingLog.Shared.Info($"-> sent image {i.Png.Length} bytes to {peer}");
                    toast($"AnyClip → {peer}", $"image ({i.Png.Length / 1024} KB)");
                    break;
                case FileClip f:
                    RotatingLog.Shared.Info($"-> sent file {f.Name} {f.Data.Length} bytes to {peer}");
                    toast($"AnyClip → {peer}", $"file: {f.Name} ({f.Data.Length / 1024} KB)");
                    break;
            }
        };
        clipboard.OnFileSkipped = msg => { toast("AnyClip", msg); return Task.CompletedTask; };

        var directory = new PeerDirectory(nodeId, emit,
            (host, port, label) => link.TryConnectAsync(host, port, label, outerCt));
        // The App's MdnsBeacon needs the directory to ingest into; expose it.
        CurrentDirectory = directory;

        await mdns.StartAsync(
            $"{config.Name}-{nodeId[..8]}",
            new[]
            {
                ("id", nodeId),
                ("version", Wire.LegacyVersion.ToString()),
                ("app_version", appVersion),
                ("protocol_major", Wire.ProtocolMajor.ToString()),
                ("protocol_minor", Wire.ProtocolMinor.ToString()),
            });
        RotatingLog.Shared.Info(
            $"AnyClip starting (node {nodeId[..8]}, name={config.Name})");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var tasks = new[]
        {
            link.ServeAsync(cts.Token),
            clipboard.RunAsync(cts.Token),
            Watchdogs.MdnsReconnectLoopAsync(directory, link, cts.Token),
            Watchdogs.NetworkWatchdogAsync(mdns, primaryIPv4, 15, cts.Token),
            Watchdogs.IdleLinkWatchdogAsync(mdns, link, 60, 3, cts.Token),
            Watchdogs.LinkPingLoopAsync(link, 30, cts.Token),
        };
        try
        {
            // asyncio.gather semantics: first fault wins, siblings drained.
            var first = await Task.WhenAny(tasks);
            cts.Cancel();
            try { await Task.WhenAll(tasks); } catch { /* drained below */ }
            if (first.IsFaulted)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(first.Exception!.InnerException ?? first.Exception!).Throw();
            outerCt.ThrowIfCancellationRequested();
        }
        finally
        {
            link.Shutdown();
            mdns.Stop();
            pidLock.Release();
            ClearDirectoryFiles(receivedDir);
            CurrentDirectory = null;
        }
    }

    /// The live PeerDirectory of the current runOnce (null between runs).
    /// The Windows MdnsBeacon ingests browse results into it.
    public PeerDirectory? CurrentDirectory { get; private set; }
}
```

- [ ] **Step 4:** Run — all pass.
- [ ] **Step 5: Commit** — `forwindows: daemon assembly with injected platform surface`

---

### Task 9: Core — interop test against fake_peer.py (on macOS)

**Files:**
- Test: `forwindows/tests/AnyClipCore.Tests/InteropTests.cs`

This is the acceptance gate: the C# PeerLink must handshake and exchange
clips with the Python wire rules end-to-end over TCP, on this machine.

- [ ] **Step 1: write the test** — `InteropTests.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class InteropTests
{
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

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
                var lines = File.ReadAllText(outFile);
                return lines.Contains("hello-from-csharp")
                    && lines.Contains("\"kind\": \"file\"")
                    && lines.Contains("노트.txt")
                    && lines.Contains("\"kind\": \"image\"")
                    && lines.Contains("\"type\": \"ping\"");
            }));

            // Our hello satisfied Python's expectations (incl. legacy version).
            var outText = File.ReadAllText(outFile);
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
}
```

- [ ] **Step 2:** Run: `"$HOME/.dotnet/dotnet" test forwindows/tests/AnyClipCore.Tests --filter Interop`
Expected: PASS. Then the full Core suite twice — stable.

- [ ] **Step 3: Commit** — `forwindows: interop test against Python wire implementation`

---

### Task 10: App scaffold + PidLock + Autostart (+ windows-only test project)

From here on, code targets `net8.0-windows`: **cross-build on macOS with
`"$HOME/.dotnet/dotnet" build forwindows/src/AnyClipApp` (must compile clean),
never run locally.** `AnyClipApp.Tests` is created now and RUN ONLY ON CI.

**Files:**
- Create: `forwindows/src/AnyClipApp/AnyClipApp.csproj`
- Create: `forwindows/src/AnyClipApp/PidLock.cs`
- Create: `forwindows/src/AnyClipApp/Autostart.cs`
- Create: `forwindows/tests/AnyClipApp.Tests/AnyClipApp.Tests.csproj`
- Create: `forwindows/tests/AnyClipApp.Tests/AutostartTests.cs`
- Create: `forwindows/tests/AnyClipApp.Tests/PidLockTests.cs`
- Copy: `app/icons/anyclip.ico` → `forwindows/src/AnyClipApp/anyclip.ico`

- [ ] **Step 1: projects**

`forwindows/src/AnyClipApp/AnyClipApp.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <OutputType>WinExe</OutputType>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>AnyClip.App</RootNamespace>
    <AssemblyName>AnyClip</AssemblyName>
    <ApplicationIcon>anyclip.ico</ApplicationIcon>
    <!-- Allows building (not running) this project on macOS/Linux. -->
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../AnyClipCore/AnyClipCore.csproj" />
    <Content Include="anyclip.ico" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`forwindows/tests/AnyClipApp.Tests/AnyClipApp.Tests.csproj` — same xunit
package set as Core.Tests plus:
```xml
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
```
and a ProjectReference to `AnyClipApp.csproj`.

```bash
cp app/icons/anyclip.ico forwindows/src/AnyClipApp/anyclip.ico
cd forwindows
"$HOME/.dotnet/dotnet" sln add src/AnyClipApp tests/AnyClipApp.Tests
```

- [ ] **Step 2: tests (CI-only; write them now, verify they COMPILE via build)**

`AutostartTests.cs`:
```csharp
using AnyClip.App;
using Microsoft.Win32;
using Xunit;

namespace AnyClip.App.Tests;

public class AutostartTests
{
    private static string TestSubKey() =>
        @"Software\AnyClipTest\" + Guid.NewGuid().ToString("N");

    [Fact]
    public void EnableWritesQuotedCommandAndDisableRemoves()
    {
        var subKey = TestSubKey();
        try
        {
            var auto = new Autostart(subKey);
            Assert.False(auto.IsEnabled());
            auto.Enable(@"C:\Program Files\AnyClip\AnyClip.exe");
            Assert.True(auto.IsEnabled());
            using var key = Registry.CurrentUser.OpenSubKey(subKey)!;
            Assert.Equal("\"C:\\Program Files\\AnyClip\\AnyClip.exe\"",
                key.GetValue("AnyClip"));
            auto.Disable();
            Assert.False(auto.IsEnabled());
            auto.Disable(); // idempotent
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\AnyClipTest", false); }
    }

    [Fact]
    public void DefaultSubKeyIsTheSharedRunKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run",
            Autostart.DefaultRunKey);
    }
}
```

`PidLockTests.cs`:
```csharp
using AnyClip.App;
using Xunit;

namespace AnyClip.App.Tests;

public class PidLockTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "anyclip-pid-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void PrepareWritesOwnPidAndReleaseRemoves()
    {
        var dir = TempDir();
        var pidLock = new WindowsPidLock(dir);
        pidLock.Prepare(58162);
        Assert.Equal($"{Environment.ProcessId} 58162\n",
            File.ReadAllText(Path.Combine(dir, "anyclip.pid")));
        pidLock.Release();
        Assert.False(File.Exists(Path.Combine(dir, "anyclip.pid")));
    }

    [Fact]
    public void StaleDeadPidIsOverwrittenAndForeignReleaseIgnored()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "anyclip.pid"), "999999 58162\n");
        var pidLock = new WindowsPidLock(dir);
        pidLock.Prepare(58162);
        Assert.StartsWith($"{Environment.ProcessId} ",
            File.ReadAllText(Path.Combine(dir, "anyclip.pid")));
        // Foreign pid file untouched by Release.
        File.WriteAllText(Path.Combine(dir, "anyclip.pid"), "999999 58162\n");
        pidLock.Release();
        Assert.True(File.Exists(Path.Combine(dir, "anyclip.pid")));
    }
}
```

- [ ] **Step 3: implement**

`Autostart.cs`:
```csharp
using Microsoft.Win32;

namespace AnyClip.App;

/// HKCU Run-key autostart — same value name ("AnyClip") and key as the
/// Python build, so a migrating user never has two entries. Port of
/// autostart.WindowsAutostart + format_windows_command.
public sealed class Autostart(string? subKey = null)
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "AnyClip";
    private readonly string _subKey = subKey ?? DefaultRunKey;

    public static string FormatCommand(string executablePath) =>
        $"\"{executablePath}\"";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        return key?.GetValue(ValueName) is not null;
    }

    public void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue(ValueName, FormatCommand(executablePath));
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
```

`PidLock.cs`:
```csharp
using System.Diagnostics;
using AnyClip.Core;

namespace AnyClip.App;

/// Shared ~/.anyclip/anyclip.pid ("<pid> <port>\n"). Windows semantics
/// follow anyclip.py: the pid-file evidence is trusted (_is_anyclip_pid
/// returns True on win32; no lsof port probe). Kill, wait 2 s, 0.3 s
/// socket settle.
public sealed class WindowsPidLock(string? dir = null) : IPidLock
{
    private readonly string _dir = dir ?? ConfigStore.DefaultDir();
    private string PidFile => Path.Combine(_dir, "anyclip.pid");

    public void Prepare(int port)
    {
        Directory.CreateDirectory(_dir);
        if (File.Exists(PidFile))
        {
            var first = File.ReadAllText(PidFile).Split(' ').FirstOrDefault();
            if (int.TryParse(first, out int oldPid)
                && oldPid > 0 && oldPid != Environment.ProcessId
                && TryGetProcess(oldPid) is { } proc)
            {
                RotatingLog.Shared.Info(
                    $"another anyclip detected (pid {oldPid} via PID file); terminating");
                try
                {
                    proc.Kill();
                    if (!proc.WaitForExit(2000))
                        throw new FatalStartupException(
                            $"could not terminate previous anyclip (pid {oldPid})");
                    RotatingLog.Shared.Info($"previous anyclip (pid {oldPid}) terminated");
                    Thread.Sleep(300); // let the OS release the listen socket
                }
                catch (FatalStartupException) { throw; }
                catch (Exception e)
                {
                    RotatingLog.Shared.Warning($"terminate pid {oldPid} failed: {e.Message}");
                }
                finally { proc.Dispose(); }
            }
        }
        try { File.WriteAllText(PidFile, $"{Environment.ProcessId} {port}\n"); }
        catch (IOException e)
        { RotatingLog.Shared.Warning($"could not write PID file {PidFile}: {e.Message}"); }
    }

    public void Release()
    {
        try
        {
            if (!File.Exists(PidFile)) return;
            var first = File.ReadAllText(PidFile).Split(' ').FirstOrDefault();
            if (int.TryParse(first, out int pid) && pid == Environment.ProcessId)
                File.Delete(PidFile);
        }
        catch (IOException) { }
    }

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; } // no such process
    }
}
```

- [ ] **Step 4: cross-build gate**

Run: `"$HOME/.dotnet/dotnet" build forwindows/src/AnyClipApp && "$HOME/.dotnet/dotnet" build forwindows/tests/AnyClipApp.Tests`
Expected: both compile clean on macOS. Also rerun the Core suite (unaffected).

- [ ] **Step 5: Commit** — `forwindows: app scaffold + pid lock + registry autostart`

---

### Task 11: App — ClipboardWatcher + Notifier

**Files:**
- Create: `forwindows/src/AnyClipApp/ClipboardWatcher.cs`
- Create: `forwindows/src/AnyClipApp/Notifier.cs`
- Test: `forwindows/tests/AnyClipApp.Tests/ClipboardLogicTests.cs` (CI-only)

The watcher logic (baselines, cooldown, budget, folder skip) is identical
to the other ports; only the trigger differs — `WM_CLIPBOARDUPDATE` events
instead of polling. Reads/writes go through an `IWin32Clipboard` seam so
the logic tests on CI never depend on the runner's flaky real clipboard.
Reference: anyclip.py:808-1041 + formacOS ClipboardWatcher.swift.

- [ ] **Step 1: tests (CI-only)** — `ClipboardLogicTests.cs`:

```csharp
using AnyClip.App;
using AnyClip.Core;
using Xunit;

namespace AnyClip.App.Tests;

internal sealed class FakeClipboard : IWin32Clipboard
{
    public string? Text;
    public byte[]? ImagePng;
    public string? FilePath;
    public List<string> Written = new();
    public string? GetText() => Text;
    public byte[]? GetImagePng() => ImagePng;
    public string? GetFirstFilePath() => FilePath;
    public bool SetText(string text) { Written.Add($"text:{text}"); Text = text; return true; }
    public bool SetImagePng(byte[] png) { Written.Add("image"); ImagePng = png; return true; }
    public bool SetFilePath(string path) { Written.Add($"file:{path}"); FilePath = path; return true; }
}

public class ClipboardLogicTests
{
    private static (ClipboardWatcher W, FakeClipboard C, List<ClipPayload> Changes, List<string> Skipped)
        Make(string receivedDir)
    {
        var clip = new FakeClipboard();
        var changes = new List<ClipPayload>();
        var skipped = new List<string>();
        var w = new ClipboardWatcher(clip, receivedDir)
        {
            OnLocalChange = p => { lock (changes) changes.Add(p); return Task.CompletedTask; },
            OnFileSkipped = m => { lock (skipped) skipped.Add(m); return Task.CompletedTask; },
        };
        return (w, clip, changes, skipped);
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "anyclip-clip-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task TextChangeFiresOnceAndEmptyIsSuppressed()
    {
        var (w, clip, changes, _) = Make(TempDir());
        clip.Text = "fresh";
        await w.HandleClipboardUpdateAsync();
        Assert.Single(changes);
        await w.HandleClipboardUpdateAsync(); // unchanged → no refire
        Assert.Single(changes);
        clip.Text = "";
        await w.HandleClipboardUpdateAsync();
        Assert.Single(changes); // empty not propagated
    }

    [Fact]
    public async Task PreexistingContentIsBaselined()
    {
        var dir = TempDir();
        var clip = new FakeClipboard { Text = "already there" };
        var changes = new List<ClipPayload>();
        var w = new ClipboardWatcher(clip, dir)
        { OnLocalChange = p => { changes.Add(p); return Task.CompletedTask; } };
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes); // seeded at construction
    }

    [Fact]
    public async Task ImageCooldownAbsorbsSecondChange()
    {
        var (w, clip, changes, _) = Make(TempDir());
        clip.ImagePng = new byte[] { 1 };
        await w.HandleClipboardUpdateAsync();
        Assert.Single(changes);
        clip.ImagePng = new byte[] { 2 }; // within 1.0 s cooldown
        await w.HandleClipboardUpdateAsync();
        Assert.Single(changes);
    }

    [Fact]
    public async Task FolderSkippedOnceWithToastAndFileSent()
    {
        var dir = TempDir();
        var (w, clip, changes, skipped) = Make(dir);
        var folder = TempDir();
        clip.FilePath = folder;
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
        Assert.Single(skipped);
        Assert.Contains("folders are not supported", skipped[0]);
        await w.HandleClipboardUpdateAsync();
        Assert.Single(skipped); // fingerprint recorded → never re-detected

        var file = Path.Combine(TempDir(), "note.txt");
        File.WriteAllText(file, "file-body");
        clip.FilePath = file;
        await w.HandleClipboardUpdateAsync();
        Assert.Contains(changes, c => c is FileClip f && f.Name == "note.txt");
    }

    [Fact]
    public async Task OversizedFileSkipped()
    {
        var dir = TempDir();
        var (w, clip, changes, _) = Make(dir);
        var file = Path.Combine(TempDir(), "big.bin");
        using (var fs = File.Create(file)) fs.SetLength(12L * 1024 * 1024);
        clip.FilePath = file;
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
    }

    [Fact]
    public async Task ApplyRemoteWritesWithoutEcho()
    {
        var dir = TempDir();
        var (w, clip, changes, _) = Make(dir);
        Assert.True(await w.ApplyRemoteAsync(new TextClip("from peer")));
        Assert.Equal("from peer", clip.Text);
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes); // baseline updated before write

        Assert.True(await w.ApplyRemoteAsync(new FileClip("in:va/lid.txt", "x"u8.ToArray())));
        Assert.True(File.Exists(Path.Combine(dir, "lid.txt")));
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
    }
}
```

- [ ] **Step 2: implement**

`ClipboardWatcher.cs`:
```csharp
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AnyClip.Core;

namespace AnyClip.App;

/// Thin clipboard seam so the watcher logic is testable without the real
/// (flaky-on-CI) Windows clipboard.
public interface IWin32Clipboard
{
    string? GetText();
    byte[]? GetImagePng();
    string? GetFirstFilePath();
    bool SetText(string text);
    bool SetImagePng(byte[] png);
    bool SetFilePath(string path);
}

/// Real implementation over WinForms Clipboard. The WinForms Clipboard
/// requires the STA UI thread; daemon tasks call ApplyRemote off-thread,
/// so every access is marshalled through `Invoker` (a UI-thread Control
/// set by Program after startup).
public sealed class WinFormsClipboard : IWin32Clipboard
{
    /// UI-thread control used to marshal clipboard access; set once by
    /// Program. Until set, calls run on the current thread (startup
    /// baseline seeding happens on the UI thread before the daemon runs).
    public Control? Invoker { get; set; }

    private T OnSta<T>(Func<T> f)
    {
        var inv = Invoker;
        if (inv is { InvokeRequired: true }) return (T)inv.Invoke(f)!;
        return f();
    }

    public string? GetText() => OnSta(() =>
        Clipboard.ContainsText() ? Clipboard.GetText() : null);

    public byte[]? GetImagePng() => OnSta<byte[]?>(() =>
    {
        // File copies also carry thumbnails: files take priority as their
        // own kind (mirrors PIL ImageGrab returning a path list).
        if (Clipboard.ContainsFileDropList()) return null;
        if (!Clipboard.ContainsImage()) return null;
        using var image = Clipboard.GetImage();
        if (image is null) return null;
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    });

    public string? GetFirstFilePath() => OnSta<string?>(() =>
    {
        if (!Clipboard.ContainsFileDropList()) return null;
        var list = Clipboard.GetFileDropList();
        return list.Count > 0 ? list[0] : null;
    });

    public bool SetText(string text) => OnSta(() =>
    { try { Clipboard.SetText(text); return true; } catch (Exception) { return false; } });

    public bool SetImagePng(byte[] png) => OnSta(() =>
    {
        try
        {
            using var ms = new MemoryStream(png);
            using var image = Image.FromStream(ms);
            Clipboard.SetImage(image);
            return true;
        }
        catch (Exception) { return false; }
    });

    public bool SetFilePath(string path) => OnSta(() =>
    {
        try
        {
            var sc = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(sc);
            return true;
        }
        catch (Exception) { return false; }
    });
}

/// Clipboard-change handling with the exact baselines/cooldown/budget
/// semantics of the other ports, triggered by WM_CLIPBOARDUPDATE instead
/// of polling. Implements the daemon's IClipboardSync.
public sealed class ClipboardWatcher : IClipboardSync
{
    public const double ImageCooldownSeconds = 1.0;
    public static readonly int FileBudget =
        (int)((Wire.MaxPayload - 256 * 1024) * 0.74);

    private readonly IWin32Clipboard _clipboard;
    private readonly string _receivedDir;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private string? _lastText;
    private string? _lastImageHash;
    private double _lastImageSendAt;
    private (string Path, long Size, long MTimeTicks)? _lastFileFingerprint;
    private bool _oversizeWarned;

    public Func<ClipPayload, Task>? OnLocalChange { get; set; }
    public Func<string, Task>? OnFileSkipped { get; set; }

    public ClipboardWatcher(IWin32Clipboard clipboard, string receivedDir)
    {
        _clipboard = clipboard;
        _receivedDir = receivedDir;
        // Seed baselines so startup clipboard content never fires a send.
        _lastText = clipboard.GetText();
        if (clipboard.GetImagePng() is { } png) _lastImageHash = Hashing.Sha256Hex(png);
        if (clipboard.GetFirstFilePath() is { } p) _lastFileFingerprint = Fingerprint(p);
    }

    /// The daemon's pump: events arrive from the UI message loop, so this
    /// just parks until cancelled.
    public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    /// Called (on the UI thread) for every WM_CLIPBOARDUPDATE.
    public async Task HandleClipboardUpdateAsync()
    {
        var text = _clipboard.GetText();
        if (text is not null && text != _lastText)
        {
            _lastText = text;
            if (text.Length > 0)
                await (OnLocalChange?.Invoke(new TextClip(text)) ?? Task.CompletedTask);
            else
                RotatingLog.Shared.Debug("clipboard cleared (empty text); not propagating");
        }

        if (_clipboard.GetImagePng() is { } png)
        {
            var hash = Hashing.Sha256Hex(png);
            if (hash != _lastImageHash)
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (now - _lastImageSendAt < ImageCooldownSeconds)
                {
                    _lastImageHash = hash;
                    RotatingLog.Shared.Debug("image change within cooldown, dropping");
                }
                else
                {
                    _lastImageHash = hash;
                    _lastImageSendAt = now;
                    await (OnLocalChange?.Invoke(new ImageClip(png)) ?? Task.CompletedTask);
                }
            }
        }

        await CheckFileClipboardAsync();
    }

    private static (string, long, long)? Fingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                var di = new DirectoryInfo(path);
                if (!di.Exists) return null;
                return (path, -1, di.LastWriteTimeUtc.Ticks); // folders: size -1
            }
            return (path, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (IOException) { return null; }
    }

    private async Task CheckFileClipboardAsync()
    {
        var path = _clipboard.GetFirstFilePath();
        if (path is null) return;
        var fp = Fingerprint(path);
        if (fp is null || fp == _lastFileFingerprint) return;

        if (Directory.Exists(path))
        {
            _lastFileFingerprint = fp; // record FIRST — no retry loop
            var display = Path.GetFileName(path.TrimEnd('/', '\\'));
            RotatingLog.Shared.Warning(
                $"folder on clipboard not synced (unsupported): {path}");
            await (OnFileSkipped?.Invoke(
                $"folder not synced — folders are not supported: {display}")
                ?? Task.CompletedTask);
            return;
        }
        if (fp.Value.Size > FileBudget)
        {
            if (!_oversizeWarned)
            {
                RotatingLog.Shared.Warning(
                    $"file {path} too large to sync ({fp.Value.Size} bytes > "
                    + $"limit {FileBudget}); skipping");
                _oversizeWarned = true;
            }
            _lastFileFingerprint = fp;
            return;
        }
        _oversizeWarned = false;
        byte[] data;
        try { data = await File.ReadAllBytesAsync(path); }
        catch (IOException e)
        {
            _lastFileFingerprint = fp; // unreadable now won't improve by retrying
            RotatingLog.Shared.Warning($"file read failed for {path}: {e.Message}; skipping");
            return;
        }
        _lastFileFingerprint = fp;
        await (OnLocalChange?.Invoke(new FileClip(Path.GetFileName(path), data))
            ?? Task.CompletedTask);
    }

    /// Inbound (peer → local). Baselines updated BEFORE writes.
    public Task<bool> ApplyRemoteAsync(ClipPayload payload)
    {
        switch (payload)
        {
            case TextClip t:
                _lastText = t.Text;
                return Task.FromResult(_clipboard.SetText(t.Text));
            case ImageClip i:
                _lastImageHash = Hashing.Sha256Hex(i.Png);
                bool ok = _clipboard.SetImagePng(i.Png);
                if (!ok) RotatingLog.Shared.Warning("clipboard write (image) failed");
                return Task.FromResult(ok);
            case FileClip f:
                try
                {
                    Directory.CreateDirectory(_receivedDir);
                    string target = Path.Combine(
                        _receivedDir, TextHelpers.SanitizeFilename(f.Name));
                    File.WriteAllBytes(target, f.Data);
                    _lastFileFingerprint = Fingerprint(target);
                    bool fileOk = _clipboard.SetFilePath(target);
                    if (!fileOk) RotatingLog.Shared.Warning("clipboard write (file) failed");
                    return Task.FromResult(fileOk);
                }
                catch (IOException e)
                {
                    RotatingLog.Shared.Warning(
                        $"file write to {_receivedDir} failed: {e.Message}");
                    return Task.FromResult(false);
                }
            default:
                return Task.FromResult(false);
        }
    }
}

/// Message-only window receiving WM_CLIPBOARDUPDATE; created on the UI
/// thread by Program and forwarding to the watcher.
public sealed class ClipboardListenerWindow : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly Func<Task> _onUpdate;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public ClipboardListenerWindow(Func<Task> onUpdate)
    {
        _onUpdate = onUpdate;
        CreateHandle(new CreateParams());
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
            _ = _onUpdate(); // fire-and-forget; handler logs its own errors
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}
```

`Notifier.cs`:
```csharp
namespace AnyClip.App;

/// Balloon-tip notifications over the tray NotifyIcon — the same vehicle
/// the Python build used. Body capped at 240 chars like the other ports.
public sealed class Notifier(NotifyIcon trayIcon)
{
    public void Notify(string title, string body)
    {
        try
        {
            trayIcon.ShowBalloonTip(3000, title,
                body.Length > 240 ? body[..240] : body,
                ToolTipIcon.Info);
        }
        catch (Exception) { /* notifications must never crash the app */ }
    }
}
```

- [ ] **Step 3: cross-build gate** — both projects compile on macOS; Core suite still green.
- [ ] **Step 4: Commit** — `forwindows: clipboard watcher (WM_CLIPBOARDUPDATE) + notifier`

---

### Task 12: App — MdnsBeacon (dnsapi P/Invoke)

**Files:**
- Create: `forwindows/src/AnyClipApp/DnsApi.cs`
- Create: `forwindows/src/AnyClipApp/MdnsBeacon.cs`

Highest-risk component (spec has the contingency plan). All P/Invoke is
isolated in `DnsApi`; `MdnsBeacon` implements `IMdnsService` and ingests
resolutions into the daemon's `PeerDirectory`. CI smoke is best-effort;
the real acceptance is the manual Windows test.

- [ ] **Step 1: implement** — `DnsApi.cs`:

```csharp
using System.Runtime.InteropServices;

namespace AnyClip.App;

/// Minimal dnsapi.dll mDNS surface (Windows 10 1809+). Layout per
/// windns.h. Keep every signature here; nothing else P/Invokes DNS.
internal static class DnsApi
{
    public const uint QueryRequestVersion1 = 1;
    public const int ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DNS_SERVICE_INSTANCE
    {
        public IntPtr pszInstanceName; // LPWSTR
        public IntPtr pszHostName;     // LPWSTR
        public IntPtr ip4Address;      // IP4_ADDRESS* (network byte order)
        public IntPtr ip6Address;
        public ushort wPort;
        public ushort wPriority;
        public ushort wWeight;
        public uint dwPropertyCount;
        public IntPtr keys;            // PWSTR*
        public IntPtr values;          // PWSTR*
        public uint dwInterfaceIndex;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void DnsServiceBrowseCallback(
        int status, IntPtr queryContext, IntPtr pDnsRecord);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void DnsServiceResolveCallback(
        int status, IntPtr queryContext, IntPtr pInstance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void DnsServiceRegisterCallback(
        int status, IntPtr queryContext, IntPtr pInstance);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DNS_SERVICE_BROWSE_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        [MarshalAs(UnmanagedType.LPWStr)] public string QueryName;
        public DnsServiceBrowseCallback pBrowseCallback;
        public IntPtr pQueryContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DNS_SERVICE_RESOLVE_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        [MarshalAs(UnmanagedType.LPWStr)] public string QueryName;
        public DnsServiceResolveCallback pResolveCallback;
        public IntPtr pQueryContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public DnsServiceRegisterCallback pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        [MarshalAs(UnmanagedType.Bool)] public bool unicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DNS_SERVICE_CANCEL
    {
        public IntPtr reserved;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DnsServiceConstructInstance(
        string pServiceName, string pHostName,
        IntPtr pIp4, IntPtr pIp6, ushort wPort,
        ushort wPriority, ushort wWeight,
        uint dwPropertiesCount,
        [In] string[] keys, [In] string[] values);

    [DllImport("dnsapi.dll")]
    public static extern void DnsServiceFreeInstance(IntPtr pInstance);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceRegister(
        ref DNS_SERVICE_REGISTER_REQUEST pRequest, ref DNS_SERVICE_CANCEL pCancel);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceDeRegister(
        ref DNS_SERVICE_REGISTER_REQUEST pRequest, IntPtr pCancel);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceBrowse(
        ref DNS_SERVICE_BROWSE_REQUEST pRequest, ref DNS_SERVICE_CANCEL pCancel);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceBrowseCancel(ref DNS_SERVICE_CANCEL pCancelHandle);

    [DllImport("dnsapi.dll")]
    public static extern int DnsServiceResolve(
        ref DNS_SERVICE_RESOLVE_REQUEST pRequest, ref DNS_SERVICE_CANCEL pCancel);

    // DNS_RECORD walking for the browse callback (PTR records).
    public const ushort DNS_TYPE_PTR = 12;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DNS_RECORD_HEADER
    {
        public IntPtr pNext;
        public IntPtr pName;
        public ushort wType;
        public ushort wDataLength;
        public uint Flags;
        public uint dwTtl;
        public uint dwReserved;
        public IntPtr DataFirstPointer; // PTR: pNameHost
    }

    public const int DnsFreeRecordList = 1;

    [DllImport("dnsapi.dll")]
    public static extern void DnsRecordListFree(IntPtr pRecordList, int freeType);
}
```

`MdnsBeacon.cs`:
```csharp
using System.Net;
using System.Runtime.InteropServices;
using AnyClip.Core;

namespace AnyClip.App;

/// IMdnsService over the built-in Windows mDNS (dnsapi). Advertises our
/// instance and browses for peers, ingesting resolutions into the
/// daemon's current PeerDirectory. GC-rooted delegates are kept as
/// fields — collecting them while native code holds the pointer is the
/// classic P/Invoke crash.
public sealed class MdnsBeacon(Func<PeerDirectory?> directory) : IMdnsService
{
    private DnsApi.DNS_SERVICE_REGISTER_REQUEST _registerRequest;
    private DnsApi.DNS_SERVICE_CANCEL _registerCancel;
    private DnsApi.DNS_SERVICE_BROWSE_REQUEST _browseRequest;
    private DnsApi.DNS_SERVICE_CANCEL _browseCancel;
    private IntPtr _instance = IntPtr.Zero;
    private bool _registered;
    private bool _browsing;
    private string? _instanceName;
    private (string[] Keys, string[] Values)? _txt;

    // Rooted delegates (see class doc).
    private DnsApi.DnsServiceRegisterCallback? _registerCb;
    private DnsApi.DnsServiceBrowseCallback? _browseCb;
    private readonly List<DnsApi.DnsServiceResolveCallback> _resolveCbs = new();

    public string? AdvertisedIp { get; private set; }

    public Task StartAsync(
        string instanceName, IReadOnlyList<(string Key, string Value)> txt)
    {
        _instanceName = instanceName;
        _txt = (txt.Select(t => t.Key).ToArray(), txt.Select(t => t.Value).ToArray());
        AdvertisedIp = PrimaryIPv4();
        Register();
        StartBrowse();
        return Task.CompletedTask;
    }

    public static string? PrimaryIPv4()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80); // no packet sent (UDP)
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (System.Net.Sockets.SocketException) { return null; }
    }

    private void Register()
    {
        if (_instanceName is null || _txt is null) return;
        string host = $"{Environment.MachineName}.local";
        string fullName = $"{_instanceName}.{Wire.ServiceType}.local";
        _instance = DnsApi.DnsServiceConstructInstance(
            fullName, host, IntPtr.Zero, IntPtr.Zero,
            (ushort)Wire.DefaultPort, 0, 0,
            (uint)_txt.Value.Keys.Length, _txt.Value.Keys, _txt.Value.Values);
        if (_instance == IntPtr.Zero)
        {
            RotatingLog.Shared.Warning("DnsServiceConstructInstance failed");
            return;
        }
        _registerCb = (status, _, _) =>
        {
            if (status != DnsApi.ERROR_SUCCESS)
                RotatingLog.Shared.Warning($"mDNS register completed with status {status}");
            else
                RotatingLog.Shared.Info($"mDNS advertised as {fullName}");
        };
        _registerRequest = new DnsApi.DNS_SERVICE_REGISTER_REQUEST
        {
            Version = DnsApi.QueryRequestVersion1,
            InterfaceIndex = 0,
            pServiceInstance = _instance,
            pRegisterCompletionCallback = _registerCb,
            pQueryContext = IntPtr.Zero,
            hCredentials = IntPtr.Zero,
            unicastEnabled = false,
        };
        _registerCancel = default;
        int rc = DnsApi.DnsServiceRegister(ref _registerRequest, ref _registerCancel);
        // DnsServiceRegister returns DNS_REQUEST_PENDING (9506) on success.
        _registered = rc == 9506 || rc == DnsApi.ERROR_SUCCESS;
        if (!_registered)
            RotatingLog.Shared.Warning($"DnsServiceRegister failed: {rc}");
    }

    private void StartBrowse()
    {
        _browseCb = (status, _, pRecord) =>
        {
            try
            {
                if (status != DnsApi.ERROR_SUCCESS || pRecord == IntPtr.Zero) return;
                // Walk the record list; PTR data is the instance name.
                for (IntPtr cur = pRecord; cur != IntPtr.Zero;)
                {
                    var rec = Marshal.PtrToStructure<DnsApi.DNS_RECORD_HEADER>(cur);
                    if (rec.wType == DnsApi.DNS_TYPE_PTR
                        && rec.DataFirstPointer != IntPtr.Zero)
                    {
                        string? inst = Marshal.PtrToStringUni(rec.DataFirstPointer);
                        if (inst is not null) Resolve(inst);
                    }
                    cur = rec.pNext;
                }
            }
            finally
            {
                if (pRecord != IntPtr.Zero)
                    DnsApi.DnsRecordListFree(pRecord, DnsApi.DnsFreeRecordList);
            }
        };
        _browseRequest = new DnsApi.DNS_SERVICE_BROWSE_REQUEST
        {
            Version = DnsApi.QueryRequestVersion1,
            InterfaceIndex = 0,
            QueryName = $"{Wire.ServiceType}.local",
            pBrowseCallback = _browseCb,
            pQueryContext = IntPtr.Zero,
        };
        _browseCancel = default;
        int rc = DnsApi.DnsServiceBrowse(ref _browseRequest, ref _browseCancel);
        _browsing = rc == 9506 || rc == DnsApi.ERROR_SUCCESS;
        if (!_browsing)
            RotatingLog.Shared.Warning($"DnsServiceBrowse failed: {rc}");
    }

    private void Resolve(string instanceFullName)
    {
        var resolveCb = default(DnsApi.DnsServiceResolveCallback);
        resolveCb = (status, _, pInstance) =>
        {
            try
            {
                if (status != DnsApi.ERROR_SUCCESS || pInstance == IntPtr.Zero) return;
                var inst = Marshal.PtrToStructure<DnsApi.DNS_SERVICE_INSTANCE>(pInstance);
                string? host = null;
                if (inst.ip4Address != IntPtr.Zero)
                {
                    uint raw = (uint)Marshal.ReadInt32(inst.ip4Address);
                    host = new IPAddress(raw).ToString(); // already network order
                }
                host ??= Marshal.PtrToStringUni(inst.pszHostName);
                if (host is null || inst.wPort == 0) return;

                var props = new Dictionary<string, string>();
                for (int i = 0; i < inst.dwPropertyCount; i++)
                {
                    var key = Marshal.PtrToStringUni(
                        Marshal.ReadIntPtr(inst.keys, i * IntPtr.Size));
                    var value = Marshal.PtrToStringUni(
                        Marshal.ReadIntPtr(inst.values, i * IntPtr.Size));
                    if (key is not null && value is not null) props[key] = value;
                }
                if (!props.TryGetValue("id", out var peerId)) return;
                string label = Marshal.PtrToStringUni(inst.pszInstanceName)
                    ?? instanceFullName;
                var dir = directory();
                if (dir is not null)
                    _ = dir.IngestAsync(peerId, host, inst.wPort, label);
            }
            finally
            {
                if (pInstance != IntPtr.Zero) DnsApi.DnsServiceFreeInstance(pInstance);
                lock (_resolveCbs) _resolveCbs.Remove(resolveCb!);
            }
        };
        lock (_resolveCbs) _resolveCbs.Add(resolveCb);
        var request = new DnsApi.DNS_SERVICE_RESOLVE_REQUEST
        {
            Version = DnsApi.QueryRequestVersion1,
            InterfaceIndex = 0,
            QueryName = instanceFullName,
            pResolveCallback = resolveCb,
            pQueryContext = IntPtr.Zero,
        };
        var cancel = default(DnsApi.DNS_SERVICE_CANCEL);
        int rc = DnsApi.DnsServiceResolve(ref request, ref cancel);
        if (rc != 9506 && rc != DnsApi.ERROR_SUCCESS)
        {
            RotatingLog.Shared.Warning($"DnsServiceResolve({instanceFullName}) failed: {rc}");
            lock (_resolveCbs) _resolveCbs.Remove(resolveCb);
        }
    }

    public void Refresh()
    {
        if (_browsing)
        {
            DnsApi.DnsServiceBrowseCancel(ref _browseCancel);
            _browsing = false;
        }
        StartBrowse();
        RotatingLog.Shared.Debug("mDNS: browser re-issued");
    }

    public void Stop()
    {
        if (_browsing)
        {
            DnsApi.DnsServiceBrowseCancel(ref _browseCancel);
            _browsing = false;
        }
        if (_registered && _instance != IntPtr.Zero)
        {
            DnsApi.DnsServiceDeRegister(ref _registerRequest, IntPtr.Zero);
            _registered = false;
        }
        if (_instance != IntPtr.Zero)
        {
            DnsApi.DnsServiceFreeInstance(_instance);
            _instance = IntPtr.Zero;
        }
    }
}
```

- [ ] **Step 2: cross-build gate** — `"$HOME/.dotnet/dotnet" build forwindows/src/AnyClipApp` clean.
- [ ] **Step 3: Commit** — `forwindows: mDNS beacon over built-in dnsapi (P/Invoke)`

---

### Task 13: App — TrayIcon, Dialogs, Program (composition root)

**Files:**
- Create: `forwindows/src/AnyClipApp/TrayIcon.cs`
- Create: `forwindows/src/AnyClipApp/Dialogs.cs`
- Create: `forwindows/src/AnyClipApp/Program.cs`

Reference: `app/tray_win.py` (menu set/labels/token flow) + the 2026-06-11
UI parity items (status-aware icon, enter-token flow) + formacOS
StatusItemController/AppDelegate (quit deadline 3 s, version resolution).

- [ ] **Step 1: implement**

`TrayIcon.cs`:
```csharp
using System.Drawing;
using AnyClip.Core;

namespace AnyClip.App;

/// NotifyIcon + ContextMenuStrip shell. Icon states follow TrayIconSpec:
/// normal when linked; red-tinted when not; red + "!" overlay on error.
public sealed class TrayIcon : IDisposable
{
    public NotifyIcon Notify { get; } = new();
    private readonly ToolStripMenuItem _statusItem = new("Status: Idle") { Enabled = false };
    private readonly ToolStripMenuItem _lastSyncItem = new("Last sync: —") { Enabled = false };
    private readonly ToolStripMenuItem _startAtLoginItem = new("Start at Login");
    private readonly Autostart _autostart = new();
    private readonly Icon _baseIcon;
    private readonly Icon _attentionIcon;
    private readonly Icon _errorIcon;
    private readonly string _logFile;
    private readonly Action _onQuit;

    public TrayIcon(string logFile, Action onQuit)
    {
        _logFile = logFile;
        _onQuit = onQuit;
        _baseIcon = new Icon(Path.Combine(AppContext.BaseDirectory, "anyclip.ico"));
        _attentionIcon = Tint(_baseIcon, bang: false);
        _errorIcon = Tint(_baseIcon, bang: true);

        var menu = new ContextMenuStrip();
        var tokenItem = new ToolStripMenuItem("Token…", null, (_, _) => Dialogs.TokenFlow(_onQuit));
        _startAtLoginItem.Checked = _autostart.IsEnabled();
        _startAtLoginItem.Click += (_, _) => ToggleAutostart();
        var openLogsItem = new ToolStripMenuItem("Open Logs", null, (_, _) => OpenLogs());
        var quitItem = new ToolStripMenuItem("Quit", null, (_, _) => _onQuit());

        menu.Items.Add(_statusItem);
        menu.Items.Add(_lastSyncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(tokenItem);
        menu.Items.Add(_startAtLoginItem);
        menu.Items.Add(openLogsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        Notify.ContextMenuStrip = menu;
        Notify.Visible = true;
        Apply(PeerUiState.Initial);
    }

    /// Red-tinted copy of the base icon, optionally with a "!" overlay.
    private static Icon Tint(Icon baseIcon, bool bang)
    {
        using var bmp = baseIcon.ToBitmap();
        using var tinted = new Bitmap(bmp.Width, bmp.Height);
        using (var g = Graphics.FromImage(tinted))
        {
            g.DrawImage(bmp, 0, 0);
            using var overlay = new SolidBrush(Color.FromArgb(112, 220, 40, 40));
            g.FillEllipse(overlay, 0, 0, bmp.Width - 1, bmp.Height - 1);
            if (bang)
            {
                using var font = new Font(FontFamily.GenericSansSerif,
                    bmp.Height * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString("!", font, Brushes.White,
                    new RectangleF(0, 0, bmp.Width, bmp.Height),
                    new StringFormat
                    {
                        Alignment = StringAlignment.Far,
                        LineAlignment = StringAlignment.Far,
                    });
            }
        }
        return Icon.FromHandle(tinted.GetHicon());
    }

    public void Apply(PeerUiState state)
    {
        string status = state.Kind switch
        {
            PeerStateKind.Linked => $"Linked: {state.PeerName ?? "peer"}",
            PeerStateKind.Searching => "Searching for peer",
            PeerStateKind.Error => $"Error: {state.Reason ?? "unknown"}",
            _ => "Idle",
        };
        _statusItem.Text = $"Status: {status}";
        _lastSyncItem.Text = state.Kind == PeerStateKind.Linked
            ? $"Linked since: {DateTime.Now:HH:mm:ss}"
            : "Last sync: —";
        var spec = TrayIconSpec.For(state);
        Notify.Icon = spec switch
        {
            { Attention: false } => _baseIcon,
            { ErrorBang: true } => _errorIcon,
            _ => _attentionIcon,
        };
        // NotifyIcon.Text caps at 127 chars.
        var tip = $"AnyClip — {status}";
        Notify.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private void ToggleAutostart()
    {
        if (_startAtLoginItem.Checked)
        {
            _autostart.Disable();
            _startAtLoginItem.Checked = false;
            return;
        }
        try
        {
            _autostart.Enable(Environment.ProcessPath ?? Application.ExecutablePath);
            _startAtLoginItem.Checked = true;
        }
        catch (Exception e)
        {
            MessageBox.Show($"Could not enable Start at Login:\n{e.Message}",
                "AnyClip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenLogs()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_logFile}\"");
        }
        catch (Exception e)
        { RotatingLog.Shared.Warning($"open logs failed: {e.Message}"); }
    }

    public void Dispose()
    {
        Notify.Visible = false;
        Notify.Dispose();
    }
}
```

`Dialogs.cs`:
```csharp
using AnyClip.Core;

namespace AnyClip.App;

/// Onboarding + token dialogs (WinForms). Mirrors onboarding_win.py's
/// three-way flow and the macOS port's Token… Close/Enter/Reset flow.
public static class Dialogs
{
    /// env > config.json > onboarding dialog. null = user cancelled.
    public static string? ResolveToken()
    {
        var env = Environment.GetEnvironmentVariable("ANYCLIP_TOKEN");
        if (!string.IsNullOrEmpty(env)) return env;
        if (ConfigStore.Load() is { } stored) return stored;
        var token = ShowOnboarding();
        if (token is not null) TrySave(token);
        return token;
    }

    private static string? ShowOnboarding()
    {
        using var form = BuildChoiceForm(
            "Welcome to AnyClip",
            "Choose how to set the shared clipboard token.\n"
            + "Both devices must use the same value.",
            "Generate new token (first device)",
            "Enter existing token (second device)");
        return form.ShowDialog() switch
        {
            DialogResult.Yes => ConfigStore.GenerateToken(),
            DialogResult.No => PromptForToken(),
            _ => null,
        };
    }

    private static string? PromptForToken()
    {
        using var form = new Form
        {
            Text = "Enter shared token",
            Width = 420, Height = 150,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false, TopMost = true,
        };
        var field = new TextBox { Left = 12, Top = 12, Width = 380 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 226, Top = 50, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 312, Top = 50, Width = 80 };
        form.Controls.AddRange(new Control[] { field, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        if (form.ShowDialog() != DialogResult.OK) return null;
        var value = field.Text.Trim();
        return value.Length == 0 ? null : value;
    }

    /// Token… menu flow: show current + (Close default / Enter / Reset).
    public static void TokenFlow(Action quit)
    {
        var current = ConfigStore.Load() ?? "(no token configured)";
        using var form = BuildChoiceForm(
            "AnyClip token",
            $"Current token:\n{current}\n\nStored at: {ConfigStore.ConfigPath()}\n\n"
            + "Enter token… lets you paste the token from your other device.\n"
            + "Reset… generates a new random token.",
            "Enter token…", "Reset…", closeIsDefault: true);
        switch (form.ShowDialog())
        {
            case DialogResult.Yes: // Enter token…
                if (PromptForToken() is { } entered && TrySave(entered))
                {
                    MessageBox.Show(
                        "AnyClip will now quit. Relaunch to apply, then make "
                        + "sure your other device uses the same token.",
                        "Token saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    quit();
                }
                break;
            case DialogResult.No: // Reset…
                var confirm = MessageBox.Show(
                    "This will replace the current token. Your other device "
                    + "will stop syncing until you paste the new token there. Proceed?",
                    "Reset token?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;
                var fresh = ConfigStore.GenerateToken();
                if (TrySave(fresh))
                {
                    MessageBox.Show(
                        $"New token saved:\n{fresh}\n\nAnyClip will now quit. "
                        + "Relaunch to apply, then paste this token on your other device.",
                        "Token reset", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    quit();
                }
                break;
        }
    }

    private static bool TrySave(string token)
    {
        try { ConfigStore.Save(token); return true; }
        catch (Exception e)
        {
            MessageBox.Show(
                $"Saving to {ConfigStore.ConfigPath()} failed: {e.Message}",
                "Could not save token", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// Three-button chooser: [first]=Yes [second]=No [Close]=Cancel(default
    /// when closeIsDefault). WinForms has no 3-custom-button MessageBox, so
    /// a small fixed form keeps the flows native and thread-safe.
    private static Form BuildChoiceForm(
        string title, string body, string firstLabel, string secondLabel,
        bool closeIsDefault = false)
    {
        var form = new Form
        {
            Text = title, Width = 460, Height = 240,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false, TopMost = true,
        };
        var label = new Label { Left = 12, Top = 12, Width = 420, Height = 130, Text = body };
        var first = new Button { Text = firstLabel, DialogResult = DialogResult.Yes, Left = 12, Top = 155, Width = 200 };
        var second = new Button { Text = secondLabel, DialogResult = DialogResult.No, Left = 218, Top = 155, Width = 120 };
        var close = new Button { Text = closeIsDefault ? "Close" : "Cancel", DialogResult = DialogResult.Cancel, Left = 344, Top = 155, Width = 90 };
        form.Controls.AddRange(new Control[] { label, first, second, close });
        form.AcceptButton = closeIsDefault ? close : first;
        form.CancelButton = close;
        return form;
    }
}
```

`Program.cs`:
```csharp
using AnyClip.Core;

namespace AnyClip.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        string stateDir = ConfigStore.DefaultDir();
        string logFile = Path.Combine(stateDir, "anyclip.log");
        RotatingLog.Shared = new RotatingLog(logFile);

        string? token = Dialogs.ResolveToken();
        if (token is null)
        {
            Console.Error.WriteLine("anyclip: onboarding cancelled, exiting");
            return;
        }

        string appVersion =
            System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion.Split('+')[0]
            ?? "0.0.0-dev";

        using var quitCts = new CancellationTokenSource();
        Daemon? daemon = null;
        TrayIcon? tray = null;
        Task? daemonTask = null;

        void Quit()
        {
            quitCts.Cancel();
            // Give cleanup (mDNS deregister, pid release) up to 3 s, matching
            // the Python supervisor.stop(timeout=3) / macOS port behavior.
            daemonTask?.Wait(TimeSpan.FromSeconds(3));
            tray?.Dispose();
            Application.Exit();
        }

        tray = new TrayIcon(logFile, Quit);
        var notifier = new Notifier(tray.Notify);

        // STA invoker: a hidden UI-thread control all clipboard access is
        // marshalled through (daemon tasks call ApplyRemote off-thread).
        var staInvoker = new Control();
        _ = staInvoker.Handle; // force handle creation on this (UI) thread
        var winClipboard = new WinFormsClipboard();
        var clipboard = new ClipboardWatcher(
            winClipboard, Path.Combine(stateDir, "received"));
        winClipboard.Invoker = staInvoker;
        var mdns = new MdnsBeacon(() => daemon?.CurrentDirectory);
        daemon = new Daemon(
            new DaemonConfig(token, Wire.DefaultPort, Environment.MachineName),
            appVersion, stateDir,
            clipboard, mdns, new WindowsPidLock(),
            MdnsBeacon.PrimaryIPv4,
            notify: (title, body) => notifier.Notify(title, body),
            onFatal: message =>
            {
                MessageBox.Show(message, "AnyClip cannot start",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            });

        // Clipboard events come from the UI loop; forward to the watcher.
        using var listener = new ClipboardListenerWindow(
            clipboard.HandleClipboardUpdateAsync);

        daemonTask = Task.Run(() => daemon.RunForeverAsync(quitCts.Token));

        // Fold daemon events into tray state on the UI thread.
        var uiContext = SynchronizationContext.Current!;
        _ = Task.Run(async () =>
        {
            var state = PeerUiState.Initial;
            await foreach (var ev in daemon.Events.ReadAllAsync())
            {
                state = PeerStateReducer.Reduce(state, ev,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
                var snapshot = state;
                uiContext.Post(_ => tray.Apply(snapshot), null);
            }
        });

        Application.Run();
    }
}
```

- [ ] **Step 2: cross-build gate** — App project compiles clean on macOS;
  full Core suite still green.
- [ ] **Step 3: Commit** — `forwindows: tray + dialogs + composition root`

---

### Task 14: CI windows-native job + publish script + cross-publish check

**Files:**
- Modify: `.github/workflows/release.yml` (add job after `macos-swift`)
- Create: `forwindows/Scripts/publish-win.sh` (chmod +x)

- [ ] **Step 1: publish script** — `forwindows/Scripts/publish-win.sh`:

```bash
#!/bin/bash
# Cross-publish the Windows single-file exe + zip from any host.
# Version: env ANYCLIP_BUILD_VERSION (default 0.0.0-dev).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${ANYCLIP_BUILD_VERSION:-0.0.0-dev}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT="dist"

"$DOTNET" publish src/AnyClipApp -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableWindowsTargeting=true \
  -p:InformationalVersion="$VERSION" \
  -o "$OUT/publish"

rm -f "$OUT/AnyClip-v$VERSION-windows-x64-native.zip"
(cd "$OUT/publish" && zip -q -r "../AnyClip-v$VERSION-windows-x64-native.zip" .)
echo "Built $OUT/AnyClip-v$VERSION-windows-x64-native.zip"
```

- [ ] **Step 2: CI job** — insert into `.github/workflows/release.yml`
directly after the `macos-swift` job (same indentation level):

```yaml
  windows-native:
    name: Windows native (C#) build
    runs-on: windows-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"

      - name: Run tests (Core + App)
        run: |
          dotnet test forwindows/tests/AnyClipCore.Tests
          dotnet test forwindows/tests/AnyClipApp.Tests

      - name: Publish single-file exe
        shell: pwsh
        run: |
          $version = "${{ github.ref_name }}".TrimStart("v")
          dotnet publish forwindows/src/AnyClipApp -c Release -r win-x64 `
            --self-contained -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:InformationalVersion=$version `
            -o forwindows/dist/publish
          Compress-Archive -Force `
            -Path forwindows/dist/publish/* `
            -DestinationPath "forwindows/dist/AnyClip-${{ github.ref_name }}-windows-x64-native.zip"

      - name: Upload Windows native asset to Release
        uses: softprops/action-gh-release@v2
        with:
          files: forwindows/dist/AnyClip-${{ github.ref_name }}-windows-x64-native.zip
          draft: false
          prerelease: ${{ contains(github.ref_name, '-') }}
          generate_release_notes: true
```

NOTE: the Core interop test spawns `python3` — present on windows-latest
runners. If `python3` is missing there, alias it via
`shell: pwsh; run: New-Item -ItemType SymbolicLink ...` or guard the test
with an env check — adapt minimally and note it.

- [ ] **Step 3: verify locally**

1. `ruby -ryaml -e 'puts YAML.load_file(".github/workflows/release.yml")["jobs"].keys.join(", ")'`
   → `macos, windows, macos-swift, windows-native, homebrew, appcast`
2. `forwindows/Scripts/publish-win.sh` runs on macOS and produces the zip
   (cross-publish): expect `Built dist/AnyClip-v0.0.0-dev-windows-x64-native.zip`;
   `unzip -l` shows `AnyClip.exe`.

- [ ] **Step 4: Commit** — `forwindows: CI windows-native job + publish script`

---

### Task 15: READMEs + final verification + handoff

**Files:**
- Create: `forwindows/README.md`
- Modify: `README.md` (네이티브 구현 table: Windows row 개발 중 → 릴리스 상태로)

- [ ] **Step 1: forwindows/README.md**

```markdown
# AnyClip for Windows (native C#)

C#/.NET 8 port of the AnyClip client. Wire-compatible with the Python and
Swift implementations (protocol 1.0) and shares `~/.anyclip/` — an
existing token keeps working across all three.

## Build & test

Core (platform-neutral) builds and tests anywhere, including macOS:

```bash
dotnet test tests/AnyClipCore.Tests     # incl. fake_peer.py interop
Scripts/publish-win.sh                  # cross-publish win-x64 single exe
```

The Windows layer (`src/AnyClipApp`, `tests/AnyClipApp.Tests`) builds
anywhere (`EnableWindowsTargeting`) but only runs on Windows — CI runs
those tests on `windows-latest`.

## Layout

- `src/AnyClipCore` — wire codec, PeerLink, watchdogs, daemon assembly
  behind `IClipboardSync`/`IMdnsService`/`IPidLock`.
- `src/AnyClipApp` — WinForms shell: WM_CLIPBOARDUPDATE watcher, dnsapi
  mDNS, NotifyIcon tray, dialogs, HKCU Run autostart.

## Manual smoke checklist (real Windows hardware)

1. Unzip `AnyClip-vX.Y.Z-windows-x64-native.zip`, run `AnyClip.exe`
   (SmartScreen: 추가 정보 → 실행).
2. First launch onboarding: enter the shared token from the other device.
3. Tray icon appears red while searching; turns normal when linked.
4. Copy text/image/file both ways with the Mac peer — all three sync;
   balloon notifications appear.
5. Token… menu: Enter token… / Reset… flows; Start at Login toggles the
   HKCU Run entry; Open Logs reveals `~/.anyclip/anyclip.log`.
6. Quit removes `~/.anyclip/anyclip.pid`; relaunching takes over an
   existing Python daemon (PID lock).

## Not ported (deliberate)

WinSparkle auto-update, `--headless`, multi-file/folder sync, permission
probe (no Local Network concept on Windows).
```

- [ ] **Step 2: root README** — in the 네이티브 구현 table, change the
Windows row to:
```markdown
| Windows (C# · .NET 8) | `forwindows/` | 릴리스 자산 `AnyClip-vX.Y.Z-windows-x64-native.zip` |
```

- [ ] **Step 3: final verification**

1. `"$HOME/.dotnet/dotnet" test forwindows/tests/AnyClipCore.Tests` — all pass.
2. `"$HOME/.dotnet/dotnet" build forwindows/src/AnyClipApp && "$HOME/.dotnet/dotnet" build forwindows/tests/AnyClipApp.Tests` — clean.
3. `forwindows/Scripts/publish-win.sh` — zip produced.
4. `swift test --package-path formacOS` — 112 still green (fixtures untouched).
5. `.venv/bin/python -m pytest tests/ -q` — Python regression unchanged.

- [ ] **Step 4: Commit + handoff**

```bash
git add forwindows README.md
git commit -m "forwindows: README + native table update"
```

Report to the user: implementation status, what is verified on macOS vs
what awaits CI/manual testing, and offer the release step (tag push) that
will run the windows-native CI job and produce the installable zip for
the manual smoke test on their Windows machine.

---

## Self-Review Notes

- **Spec coverage:** wire/constants (T2, golden T3), reducer+tray spec
  (T3), pure logic incl. AuthGate regression (T4), framing (T5), PeerLink
  incl. bind-retry (T6), directory+watchdogs (T7), daemon assembly with
  injected surface + notification strings (T8), Python interop ON macOS
  (T9), pid/autostart (T10), clipboard events+seam (T11), dnsapi mDNS +
  contingency isolation (T12), tray/dialogs/composition (T13), CI+publish
  (T14), docs+verification (T15).
- **Known API risk points (executor adapts + notes):** DnsService P/Invoke
  marshaling (T12 — the spec's managed-mDNS contingency applies if CI/manual
  testing shows it broken); `SocketOptionName.TcpKeepAliveTime` on macOS
  (wrap in try/catch — already done); WinForms cross-targeting restore on
  macOS needs the windows targeting packs (first `dotnet build` downloads
  them — same TLS caveat as Task 1); `python3` availability on
  windows-latest (T14 note).
- **Port hygiene:** Core tests use 28601-28632; App tests use 58162; never
  24816. macOS Swift suite uses 2846x-2849x — no overlap.
- **Threading notes for the executor:** PeerLink's critical section uses
  SemaphoreSlim with no awaits held across registration; `PeerName`/
  `IsActive` are read without the lock for display/loop checks — the same
  relaxation as the Python/Swift ports. Clipboard STA: WinFormsClipboard
  marshals EVERY access through its `Invoker` control (Task 11 code), so
  daemon-task calls to ApplyRemote are safe; Program creates the invoker
  on the UI thread before the daemon starts (Task 13 code). Acceptance =
  App.Tests on CI + manual smoke.




