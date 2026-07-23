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
    /// App-layer heartbeat while linked. Two jobs:
    ///  1. Ping every interval, so an actively broken socket surfaces as a
    ///     send failure + EOF.
    ///  2. Enforce a liveness deadline. A half-open socket — the peer slept or
    ///     vanished without RST/FIN — accepts our pings silently and never
    ///     delivers EOF, so the parked receive idles forever and the link is a
    ///     permanent zombie. Detection can't rely on send failures; we require
    ///     inbound traffic (the peer pongs our pings). If nothing arrives for
    ///     interval * deadFactor, the link is dead — drop it so the reconnect
    ///     loop runs. (Field bug: a Mac held a dead link for ~50 min after its
    ///     peer slept, which made the peer idle-bounce forever.)
    public static async Task LinkPingLoopAsync(
        PeerLink link, double intervalSeconds, CancellationToken ct, double deadFactor = 3)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            if (!link.IsActive) continue;
            await link.SendPingAsync();
            var idle = link.SecondsSinceInbound();
            if (idle is double s && s > intervalSeconds * deadFactor)
                link.DropStaleLink(s);
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
        IMdnsService mdns, LinkManager manager,
        double idleThresholdSeconds, int refreshAttempts, CancellationToken ct)
    {
        int consecutiveIdle = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(idleThresholdSeconds), ct);
            // Global escalator: only when the WHOLE mesh is down (zero links).
            if (manager.ActiveLinkCount > 0) { consecutiveIdle = 0; continue; }
            consecutiveIdle++;
            if (consecutiveIdle <= refreshAttempts)
            {
                RotatingLog.Shared.Info(
                    $"no active links for {(int)(idleThresholdSeconds * consecutiveIdle)}s; "
                    + $"refreshing mDNS (attempt {consecutiveIdle}/{refreshAttempts})");
                mdns.Refresh();
            }
            else
            {
                throw new DaemonRestartException(
                    $"no active links with no recovery after {refreshAttempts} mDNS "
                    + "refresh attempts; bouncing daemon");
            }
        }
    }

    public static async Task MdnsReconnectLoopAsync(
        PeerDirectory directory, LinkManager manager, CancellationToken ct)
    {
        double backoff = 1;
        while (true)
        {
            var peers = directory.PeersSnapshot();
            bool attempted = false;
            foreach (var (host, port, label) in peers)
            {
                if (ct.IsCancellationRequested) return;
                if (manager.AtCap) break;                    // no slots: stop dialing this cycle
                if (manager.HasLinkToHost(host)) continue;   // already meshed with this peer
                attempted = true;
                await manager.TryConnectAsync(host, port, label, ct);
                if (manager.HasLinkToHost(host))
                {
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
            if (attempted)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(backoff, 60)), ct);
                backoff = Math.Min(backoff * 2, 60);
            }
            else { backoff = 1; await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        }
    }
}
