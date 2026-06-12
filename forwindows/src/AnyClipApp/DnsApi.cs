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
