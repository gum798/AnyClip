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
    Task StartAsync(string instanceName, int port,
        IReadOnlyList<(string Key, string Value)> txt);
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
