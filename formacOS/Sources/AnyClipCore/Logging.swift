import Foundation

/// Rotating file logger writing the same line shape as Python's logging
/// ("YYYY-MM-DD HH:MM:SS,mmm LEVEL message"), into the same
/// ~/.anyclip/anyclip.log, 5 MB × 3 backups. File level is always DEBUG;
/// console (stderr) level respects `verbose`. Thread-safe via a serial queue.
public final class AnyLog: @unchecked Sendable {
    public static let shared = AnyLog()

    public enum Level: Int, Sendable {
        case debug = 10, info = 20, warning = 30, error = 40
        var label: String {
            switch self {
            case .debug: return "DEBUG"
            case .info: return "INFO"
            case .warning: return "WARNING"
            case .error: return "ERROR"
            }
        }
    }

    private let queue = DispatchQueue(label: "anyclip.log")
    private var fileURL: URL?
    private var handle: FileHandle?
    private var consoleLevel: Level = .info
    private var maxBytes = 5 * 1024 * 1024
    private var backupCount = 3
    private let formatter: DateFormatter

    public init() {
        formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss,SSS"
        formatter.locale = Locale(identifier: "en_US_POSIX")
    }

    public func configure(
        fileURL: URL, verbose: Bool,
        maxBytes: Int = 5 * 1024 * 1024, backupCount: Int = 3
    ) {
        queue.sync {
            self.consoleLevel = verbose ? .debug : .info
            self.maxBytes = maxBytes
            self.backupCount = backupCount
            self.fileURL = fileURL
            openHandle()
        }
    }

    public func debug(_ message: String) { write(.debug, message) }
    public func info(_ message: String) { write(.info, message) }
    public func warning(_ message: String) { write(.warning, message) }
    public func error(_ message: String) { write(.error, message) }

    /// Drains the queue so tests can read the file deterministically.
    public func flushForTesting() { queue.sync {} }

    private func write(_ level: Level, _ message: String) {
        queue.async { [self] in
            let line = "\(self.formatter.string(from: Date())) \(level.label) \(message)\n"
            let data = Data(line.utf8)
            if level.rawValue >= self.consoleLevel.rawValue {
                FileHandle.standardError.write(data)
            }
            guard let handle = self.handle else { return }
            handle.write(data)
            self.rotateIfNeeded()
        }
    }

    private func openHandle() {
        guard let fileURL else { return }
        try? FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: fileURL.path) {
            FileManager.default.createFile(atPath: fileURL.path, contents: nil)
        }
        handle = try? FileHandle(forWritingTo: fileURL)
        _ = try? handle?.seekToEnd()
    }

    private func rotateIfNeeded() {
        guard let fileURL = self.fileURL, let handle = self.handle,
              let offset = try? handle.offset(), offset > UInt64(maxBytes)
        else { return }
        try? handle.close()
        self.handle = nil
        let fm = FileManager.default
        let base = fileURL.path
        try? fm.removeItem(atPath: "\(base).\(backupCount)")
        if backupCount >= 2 {
            for i in stride(from: backupCount - 1, through: 1, by: -1) {
                if fm.fileExists(atPath: "\(base).\(i)") {
                    try? fm.moveItem(atPath: "\(base).\(i)", toPath: "\(base).\(i + 1)")
                }
            }
        }
        try? fm.moveItem(atPath: base, toPath: "\(base).1")
        openHandle()
    }
}
