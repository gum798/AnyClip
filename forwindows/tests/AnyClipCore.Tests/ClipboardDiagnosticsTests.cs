// forwindows/tests/AnyClipCore.Tests/ClipboardDiagnosticsTests.cs
using System.Runtime.InteropServices;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class ClipboardDiagnosticsTests
{
    [Fact]
    public void DescribeIncludesKindExceptionTypeAndMessage()
    {
        var s = ClipboardDiagnostics.DescribeWriteFailure(
            "text", new InvalidOperationException("boom"), ownerProcess: null);
        Assert.Contains("clipboard write (text) failed", s);
        Assert.Contains("InvalidOperationException", s);
        Assert.Contains("boom", s);
        Assert.DoesNotContain("hr=", s);
        Assert.DoesNotContain("held by", s);
    }

    [Fact]
    public void DescribeRendersHresultForExternalException()
    {
        // CLIPBRD_E_CANT_OPEN — the classic "another process holds the
        // clipboard open" failure this diagnostic exists for.
        var e = new ExternalException("cant open", unchecked((int)0x800401D0));
        var s = ClipboardDiagnostics.DescribeWriteFailure("text", e, null);
        Assert.Contains("hr=0x800401D0", s);
    }

    [Fact]
    public void OnlyClipboardCantOpenIsRetryable()
    {
        Assert.True(ClipboardDiagnostics.IsRetryable(
            new ExternalException("busy", ClipboardDiagnostics.ClipboardCantOpenHr)));
        // Same exception type, different HRESULT — deterministic, no retry.
        Assert.False(ClipboardDiagnostics.IsRetryable(
            new ExternalException("other", unchecked((int)0x80004005))));
        // Non-COM failures (bad payload, non-STA thread) — no retry.
        Assert.False(ClipboardDiagnostics.IsRetryable(
            new InvalidOperationException("cleared after write")));
    }

    [Fact]
    public void DescribeNamesTheClipboardOwnerWhenKnown()
    {
        var s = ClipboardDiagnostics.DescribeWriteFailure(
            "image", new ExternalException("cant open"),
            ownerProcess: "acme-dlp (pid 1234)");
        Assert.Contains("clipboard write (image) failed", s);
        Assert.Contains("held by 'acme-dlp (pid 1234)'", s);
    }
}
