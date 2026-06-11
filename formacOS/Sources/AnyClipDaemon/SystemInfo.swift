import Foundation

/// Best-effort primary IPv4 of this host: the source IP for the default
/// route, discovered by "connecting" a UDP socket to 8.8.8.8:80 (no packet
/// is sent). Port of anyclip.get_local_ipv4().
public func primaryIPv4() -> String? {
    let fd = socket(AF_INET, SOCK_DGRAM, 0)
    guard fd >= 0 else { return nil }
    defer { close(fd) }
    var addr = sockaddr_in()
    addr.sin_family = sa_family_t(AF_INET)
    addr.sin_port = in_port_t(80).bigEndian
    guard inet_pton(AF_INET, "8.8.8.8", &addr.sin_addr) == 1 else { return nil }
    let rc = withUnsafePointer(to: &addr) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
            connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
        }
    }
    guard rc == 0 else { return nil }
    var local = sockaddr_in()
    var len = socklen_t(MemoryLayout<sockaddr_in>.size)
    let rc2 = withUnsafeMutablePointer(to: &local) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
            getsockname(fd, $0, &len)
        }
    }
    guard rc2 == 0 else { return nil }
    var buf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
    var sin = local.sin_addr
    guard inet_ntop(AF_INET, &sin, &buf, socklen_t(INET_ADDRSTRLEN)) != nil else { return nil }
    return String(cString: buf)
}

/// Monotonic seconds (never goes backwards on clock changes).
public func monotonicNow() -> Double {
    Double(DispatchTime.now().uptimeNanoseconds) / 1_000_000_000
}

/// Raised when the daemon cannot start and retrying will not help.
/// The in-process supervisor recognises this and stops instead of looping.
public struct FatalStartupError: Error, CustomStringConvertible {
    public let message: String
    public var description: String { message }
    public init(_ message: String) { self.message = message }
}
