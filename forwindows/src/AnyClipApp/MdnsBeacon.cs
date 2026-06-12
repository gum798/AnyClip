using System.Net;
using System.Runtime.InteropServices;
using AnyClip.Core;

namespace AnyClip.App;

/// IMdnsService over the built-in Windows mDNS (dnsapi). Advertises our
/// instance and browses for peers, ingesting resolutions into the
/// daemon's current PeerDirectory. GC-rooted delegates are kept as
/// readonly fields initialized once in the constructor and NEVER
/// reassigned — collecting (or replacing) a delegate while native code
/// can still invoke its thunk (including the final ERROR_CANCELLED
/// callback after a cancel/deregister) is the classic P/Invoke crash.
public sealed class MdnsBeacon : IMdnsService
{
    private readonly Func<PeerDirectory?> _directory;
    private DnsApi.DNS_SERVICE_REGISTER_REQUEST _registerRequest;
    private DnsApi.DNS_SERVICE_CANCEL _registerCancel;
    private DnsApi.DNS_SERVICE_BROWSE_REQUEST _browseRequest;
    private DnsApi.DNS_SERVICE_CANCEL _browseCancel;
    private IntPtr _instance = IntPtr.Zero;
    private bool _registered;
    private bool _browsing;
    private string? _instanceName;
    private string? _fullName; // read by the register callback for logging
    private int _port;
    private (string[] Keys, string[] Values)? _txt;

    // DnsServiceDeRegister completes asynchronously through the register
    // callback (its goodbye packets still reference the instance); freeing
    // the instance before that callback lands is a native use-after-free.
    private readonly ManualResetEventSlim _deregDone = new(false);
    private volatile bool _deregistering;

    // Rooted delegates (see class doc) — never reassigned.
    private readonly DnsApi.DnsServiceRegisterCallback _registerCb;
    private readonly DnsApi.DnsServiceBrowseCallback _browseCb;
    private readonly List<DnsApi.DnsServiceResolveCallback> _resolveCbs = new();

    public string? AdvertisedIp { get; private set; }

    public MdnsBeacon(Func<PeerDirectory?> directory)
    {
        _directory = directory;
        _registerCb = (status, _, pInstance) =>
        {
            // The callback owns this instance copy and must free it
            // (DNS_SERVICE_REGISTER_COMPLETE contract).
            if (pInstance != IntPtr.Zero) DnsApi.DnsServiceFreeInstance(pInstance);
            if (_deregistering) { _deregDone.Set(); return; }
            if (status != DnsApi.ERROR_SUCCESS)
                RotatingLog.Shared.Warning($"mDNS register completed with status {status}");
            else
                RotatingLog.Shared.Info($"mDNS advertised as {_fullName}");
        };
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
    }

    public Task StartAsync(string instanceName, int port,
        IReadOnlyList<(string Key, string Value)> txt)
    {
        _instanceName = instanceName;
        _port = port;
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
        _deregistering = false;
        string host = $"{Environment.MachineName}.local";
        _fullName = $"{_instanceName}.{Wire.ServiceType}.local";
        _instance = DnsApi.DnsServiceConstructInstance(
            _fullName, host, IntPtr.Zero, IntPtr.Zero,
            (ushort)_port, 0, 0,
            (uint)_txt.Value.Keys.Length, _txt.Value.Keys, _txt.Value.Values);
        if (_instance == IntPtr.Zero)
        {
            RotatingLog.Shared.Warning("DnsServiceConstructInstance failed");
            return;
        }
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
        // Only rebuild the request struct; _browseCb is the single rooted,
        // never-reassigned delegate (see class doc), so the documented
        // final ERROR_CANCELLED invocation after DnsServiceBrowseCancel
        // always lands on a live thunk (harmless: the callback returns
        // immediately on status != ERROR_SUCCESS).
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
        // NOTE: queryContext must NOT be named `_` here — `_ = dir.IngestAsync(...)`
        // below would assign to it (CS0029) instead of discarding.
        resolveCb = (status, _ctx, pInstance) =>
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
                var dir = _directory();
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
        // Re-announce FIRST, then re-issue the browse — Python's
        // beacon.refresh() order and the spec's "re-register on refresh()"
        // mandate (the idle-link watchdog relies on the re-announce so the
        // REMOTE peer can rediscover us after stale multicast state).
        // Register() is called unconditionally so the watchdog also retries
        // an advertisement whose initial registration failed. FakeMdns
        // counts Refresh() calls unchanged, so Task 7 tests are unaffected;
        // the re-announce is verified by the manual Windows acceptance pass
        // (watch for BOTH log lines below).
        DeregisterAndFreeInstance();
        Register();
        RotatingLog.Shared.Debug("mDNS: re-announced service");
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
        DeregisterAndFreeInstance();
    }

    /// Deregister, wait for the async completion callback, and only then
    /// free the instance: DnsServiceDeRegister returns DNS_REQUEST_PENDING
    /// (9506) and its goodbye packets reference pServiceInstance, so
    /// freeing early is a native use-after-free that can corrupt the heap
    /// inside dnsapi.dll. Blocking here is fine — callers run on
    /// daemon/threadpool continuations (Daemon.RunOnceAsync finally,
    /// watchdog Refresh), never the STA UI thread, and the dereg callback
    /// arrives on a dnsapi worker thread. This path is hit on every
    /// watchdog-induced daemon restart, after which the next Register()
    /// starts from a clean state.
    private void DeregisterAndFreeInstance()
    {
        if (_registered)
        {
            _deregDone.Reset();
            _deregistering = true;
            int rc = DnsApi.DnsServiceDeRegister(ref _registerRequest, IntPtr.Zero);
            bool done = rc != 9506 /* DNS_REQUEST_PENDING */
                || _deregDone.Wait(TimeSpan.FromSeconds(2));
            _deregistering = false;
            _registered = false;
            if (!done)
            {
                // Deliberate leak: never free an instance a pending
                // deregistration may still reference.
                RotatingLog.Shared.Warning(
                    "mDNS deregister still pending after 2s; leaking instance");
                _instance = IntPtr.Zero;
                return;
            }
        }
        if (_instance != IntPtr.Zero)
        {
            DnsApi.DnsServiceFreeInstance(_instance);
            _instance = IntPtr.Zero;
        }
    }
}
