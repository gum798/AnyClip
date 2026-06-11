import Foundation
import AnyClipCore

/// Single-instance lock shared with the Python implementation
/// (~/.anyclip/anyclip.pid, "<pid> <port>\n"). Port of
/// anyclip.prepare_pid_lock / release_pid_lock and helpers.
public enum PidLock {
    public static func prepare(port: UInt16, dir: URL) throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let pidFile = dir.appendingPathComponent("anyclip.pid")

        // 1) PID file from a previous run (Python or Swift).
        if let content = try? String(contentsOf: pidFile, encoding: .utf8),
           let first = content.split(separator: " ").first,
           let oldPid = Int32(first.trimmingCharacters(in: .whitespacesAndNewlines)),
           oldPid > 0, oldPid != getpid(), processAlive(oldPid) {
            AnyLog.shared.info("another anyclip detected (pid \(oldPid) via PID file); terminating")
            guard terminate(oldPid) else {
                throw FatalStartupError(
                    "could not terminate previous anyclip (pid \(oldPid)); "
                    + "please run: kill -9 \(oldPid)")
            }
            AnyLog.shared.info("previous anyclip (pid \(oldPid)) terminated")
        }

        // 2) Stale state: port held without a matching PID file.
        if let listenerPid = findListeningPid(port: port), listenerPid != getpid() {
            if isAnyclipPid(listenerPid) {
                AnyLog.shared.info("anyclip listening on tcp/\(port) (pid \(listenerPid)); terminating")
                guard terminate(listenerPid) else {
                    throw FatalStartupError(
                        "could not terminate anyclip on tcp/\(port) (pid \(listenerPid)); "
                        + "please run: kill -9 \(listenerPid)")
                }
                usleep(300_000) // let the OS release the socket
            } else {
                throw FatalStartupError(
                    "tcp/\(port) is held by a non-anyclip process (pid \(listenerPid)); "
                    + "stop that process or quit it first")
            }
        }

        // 3) Record our pid (and chosen port for diagnostics).
        try? "\(getpid()) \(port)\n".write(to: pidFile, atomically: true, encoding: .utf8)
    }

    /// Remove our PID file, but only if it still points at us.
    public static func release(dir: URL) {
        let pidFile = dir.appendingPathComponent("anyclip.pid")
        guard let content = try? String(contentsOf: pidFile, encoding: .utf8),
              let first = content.split(separator: " ").first,
              Int32(first.trimmingCharacters(in: .whitespacesAndNewlines)) == getpid()
        else { return }
        try? FileManager.default.removeItem(at: pidFile)
    }

    static func processAlive(_ pid: Int32) -> Bool {
        guard pid > 0 else { return false }
        if kill(pid, 0) == 0 { return true }
        return errno == EPERM // exists, owned by another user
    }

    /// Pure matcher, exposed for tests. Case-insensitive so it recognises
    /// both `anyclip.py` and `AnyClip.app` command lines.
    static func argsLookLikeAnyclip(_ args: String) -> Bool {
        args.lowercased().contains("anyclip")
    }

    static func isAnyclipPid(_ pid: Int32) -> Bool {
        guard let out = runCommand("/bin/ps", ["-p", "\(pid)", "-o", "args="]) else {
            return false
        }
        return argsLookLikeAnyclip(out)
    }

    static func findListeningPid(port: UInt16) -> Int32? {
        guard let out = runCommand(
            "/usr/sbin/lsof", ["-nP", "-iTCP:\(port)", "-sTCP:LISTEN", "-t"])
        else { return nil }
        for line in out.split(separator: "\n") {
            if let pid = Int32(line.trimmingCharacters(in: .whitespaces)) { return pid }
        }
        return nil
    }

    /// SIGTERM, wait up to 2 s, then SIGKILL. True if the pid is gone.
    static func terminate(_ pid: Int32) -> Bool {
        if kill(pid, SIGTERM) != 0, !processAlive(pid) { return true }
        for _ in 0..<20 {
            usleep(100_000)
            if !processAlive(pid) { return true }
        }
        kill(pid, SIGKILL)
        for _ in 0..<10 {
            usleep(100_000)
            if !processAlive(pid) { return true }
        }
        return !processAlive(pid)
    }

    private static func runCommand(_ path: String, _ args: [String]) -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = args
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return String(data: data, encoding: .utf8)
    }
}
