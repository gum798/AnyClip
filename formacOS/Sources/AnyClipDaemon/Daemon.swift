import Foundation
import AppKit
import AnyClipCore

public struct DaemonConfig: Sendable {
    public var token: String
    public var port: UInt16
    public var name: String
    public var pollInterval: Double
    public var notify: Bool

    public init(
        token: String, port: UInt16 = Wire.defaultPort,
        name: String = ProcessInfo.processInfo.hostName,
        pollInterval: Double = 0.5, notify: Bool = true
    ) {
        self.token = token
        self.port = port
        self.name = name
        self.pollInterval = max(0.1, pollInterval)
        self.notify = notify
    }
}

/// Echo-suppression state shared by the inbound and outbound paths.
public actor SyncCoordinator {
    private var suppressor = EchoSuppressor()
    public init() {}
    public func markReceived(kind: String, hash: String) {
        suppressor.markReceived(kind: kind, payloadHash: hash)
    }
    public func shouldSend(kind: String, hash: String) -> Bool {
        suppressor.shouldSend(kind: kind, payloadHash: hash)
    }
}

/// Remove regular files (not subdirectories) from a directory.
/// Port of anyclip.clear_received_dir.
public func clearDirectoryFiles(_ dir: URL) {
    let fm = FileManager.default
    guard let entries = try? fm.contentsOfDirectory(
        at: dir, includingPropertiesForKeys: [.isDirectoryKey])
    else { return }
    for entry in entries {
        let isDir = (try? entry.resourceValues(forKeys: [.isDirectoryKey]))?
            .isDirectory ?? false
        if !isDir { try? fm.removeItem(at: entry) }
    }
}

/// Decide what to actually send given the peer's protocol minor. Minor >= 1
/// understands kind:"files" (pass through, dropped == 0). Minor 0 predates
/// multi-file sync: degrade a .files batch to its first file as legacy
/// kind:"file"; `dropped` counts the files left behind for the notification.
/// Returns a nil payload only for an empty .files batch (nothing to send).
public func downgradeForPeer(
    _ payload: ClipPayload, peerMinor: Int
) -> (payload: ClipPayload?, dropped: Int) {
    guard case .files(let fs) = payload, peerMinor < 1 else { return (payload, 0) }
    guard let first = fs.first else { return (nil, 0) }
    return (.file(name: first.name, data: first.data), fs.count - 1)
}

/// Assembles and supervises one daemon runtime: PeerLink + MdnsBeacon +
/// ClipboardWatcher + watchdogs, restarting with 1s -> 60s backoff on
/// errors (improvement over the Python GUI build, where watchdog-raised
/// restarts died in DaemonSupervisor).
public final class Daemon: @unchecked Sendable {
    public let events: AsyncStream<DaemonEvent>
    private let eventsCont: AsyncStream<DaemonEvent>.Continuation

    private let config: DaemonConfig
    private let appVersion: String
    private let stateDir: URL
    private let notifier: @Sendable (String, String) -> Void
    private let onFatal: @Sendable (String) -> Void

    public init(
        config: DaemonConfig, appVersion: String,
        stateDir: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".anyclip", isDirectory: true),
        notifier: @escaping @Sendable (String, String) -> Void,
        onFatal: @escaping @Sendable (String) -> Void
    ) {
        self.config = config
        self.appVersion = appVersion
        self.stateDir = stateDir
        self.notifier = notifier
        self.onFatal = onFatal
        (events, eventsCont) = AsyncStream.makeStream(of: DaemonEvent.self)
    }

    public func runForever() async {
        var backoff: Double = 1
        while !Task.isCancelled {
            do {
                try await runOnce()
                return
            } catch is CancellationError {
                return
            } catch let fatal as FatalStartupError {
                AnyLog.shared.error("fatal: \(fatal.message)")
                onFatal(fatal.message)
                return
            } catch {
                if Task.isCancelled { return }
                AnyLog.shared.error("daemon crashed: \(error); restarting in \(Int(backoff))s")
                try? await sleepSeconds(backoff)
                backoff = min(backoff * 2, 60)
            }
        }
    }

    private func runOnce() async throws {
        try PidLock.prepare(port: config.port, dir: stateDir)
        let receivedDir = stateDir.appendingPathComponent("received")
        clearDirectoryFiles(receivedDir)

        let nodeID = UUID().uuidString.lowercased()
        let coordinator = SyncCoordinator()
        let emit: @Sendable (DaemonEvent) -> Void = { [eventsCont] event in
            eventsCont.yield(event)
        }
        // Only invoke the notifier when notifications are enabled.
        let notifyEnabled = config.notify
        let capturedNotifier = notifier
        let notify: @Sendable (String, String) -> Void = { title, body in
            if notifyEnabled { capturedNotifier(title, body) }
        }

        let link = PeerLink(
            config: PeerLink.LinkConfig(
                token: config.token, port: config.port,
                name: config.name, appVersion: appVersion),
            nodeID: nodeID)

        // Holder breaks the watcher <-> link callback cycle (Python uses a
        // forward-declared closure variable for the same reason).
        let watcherBox = Locked<ClipboardWatcher?>(nil)

        // [weak link]: the closure is stored BY link itself; a strong
        // capture would leak one PeerLink per supervisor restart.
        await link.setHandlers(
            onClip: { [coordinator, weak link] payload in
                // Mark BEFORE writing local clipboard so the outbound
                // poller sees the suppression flag in time.
                await coordinator.markReceived(kind: payload.kind, hash: payload.payloadHash)
                let peer = await link?.peerName ?? "peer"
                switch payload {
                case .text(let text):
                    await MainActor.run { watcherBox.get()?.updateLocalText(text) }
                    AnyLog.shared.info("<- received text \(text.count) chars from \(peer)")
                    notify("AnyClip ← \(peer)", preview(text))
                case .image(let png):
                    let ok = await MainActor.run {
                        watcherBox.get()?.updateLocalImage(png) ?? false
                    }
                    AnyLog.shared.info(
                        "<- received image \(png.count) bytes from \(peer) "
                        + "(\(ok ? "written to clipboard" : "WRITE FAILED"))")
                    notify("AnyClip ← \(peer)", "image (\(png.count / 1024) KB)")
                case .file(let name, let data):
                    let ok = await MainActor.run {
                        watcherBox.get()?.updateLocalFile(name: name, data: data) ?? false
                    }
                    AnyLog.shared.info(
                        "<- received file \(name) \(data.count) bytes from \(peer) "
                        + "(\(ok ? "written to clipboard" : "WRITE FAILED"))")
                    notify("AnyClip ← \(peer)", "file: \(name) (\(data.count / 1024) KB)")
                case .files(let fs):
                    let placed = await MainActor.run {
                        watcherBox.get()?.updateLocalFiles(fs) ?? []
                    }
                    // markReceived("files", aggregate) already ran at the top of
                    // this handler. If exactly one file landed, the watcher will
                    // re-detect it as a single-file copy (kind "file"), so also
                    // suppress that hash.
                    if placed.count == 1 {
                        await coordinator.markReceived(
                            kind: "file", hash: sha256Hex(placed[0].data))
                    }
                    AnyLog.shared.info(
                        "<- received \(fs.count) files from \(peer) "
                        + "(\(placed.count) written to clipboard)")
                    notify("AnyClip ← \(peer)", "\(placed.count) files")
                }
            },
            emit: emit)

        // Outbound sends run on their OWN task, fed by a non-blocking queue, so
        // the single clipboard poll loop can NEVER be frozen by a send that
        // stalls, errors, times out, or leaks its continuation. (Root cause of
        // the v1.1.10 outbound-silence wedge: onChange awaited the send inline,
        // so one parked send killed all future polling.) sendOutbound is the
        // former onLocalChange body, now driven by the drain task below.
        let outbound = OutboundQueue()

        let sendOutbound: @Sendable (ClipPayload) async -> Void = { [coordinator, weak link] rawPayload in
            guard let link else { return }
            guard await link.isActive else { return }

            // Old-peer fallback: a peer that predates protocol 1.1 cannot decode
            // kind:"files". Degrade a batch to its first file and notify.
            let (maybePayload, dropped) = downgradeForPeer(
                rawPayload, peerMinor: await link.peerProtocolMinor)
            guard let payload = maybePayload else { return }
            if dropped > 0 {
                notify("AnyClip",
                    "\(dropped) file(s) not synced — update the peer to receive multiple files")
            }

            guard await coordinator.shouldSend(
                kind: payload.kind, hash: payload.payloadHash)
            else {
                AnyLog.shared.debug("skip echo of just-received \(payload.kind)")
                return
            }
            await link.sendClip(payload)
            let peer = await link.peerName ?? "peer"
            switch payload {
            case .text(let text):
                AnyLog.shared.info("-> sent text \(text.count) chars to \(peer)")
                notify("AnyClip → \(peer)", preview(text))
            case .image(let png):
                AnyLog.shared.info("-> sent image \(png.count) bytes to \(peer)")
                notify("AnyClip → \(peer)", "image (\(png.count / 1024) KB)")
            case .file(let name, let data):
                AnyLog.shared.info("-> sent file \(name) \(data.count) bytes to \(peer)")
                notify("AnyClip → \(peer)", "file: \(name) (\(data.count / 1024) KB)")
            case .files(let fs):
                let total = fs.reduce(0) { $0 + $1.data.count }
                AnyLog.shared.info("-> sent \(fs.count) files \(total) bytes to \(peer)")
                notify("AnyClip → \(peer)", "\(fs.count) files")
            }
        }

        let pollInterval = config.pollInterval
        let watcher = await MainActor.run {
            ClipboardWatcher(
                pollInterval: pollInterval, receivedDir: receivedDir,
                callbacks: ClipboardWatcher.Callbacks(
                    onChange: { payload in outbound.enqueue(payload) },
                    onFileSkipped: { message in notify("AnyClip", message) }))
        }
        watcherBox.set(watcher)

        let beacon = MdnsBeacon(
            nodeID: nodeID, emit: emit,
            onPeer: { [weak link] endpoint, label in
                await link?.tryConnect(to: endpoint, label: label)
            })

        let txtData = TXTCodec.encode([
            ("id", nodeID),
            ("version", "\(Wire.legacyVersion)"),
            ("app_version", appVersion),
            ("protocol_major", "\(Wire.protocolMajor)"),
            ("protocol_minor", "\(Wire.protocolMinor)"),
        ])
        await link.configureAdvertising(
            instanceName: "\(config.name)-\(nodeID.prefix(8))", txtData: txtData)
        await beacon.start()
        AnyLog.shared.info(
            "AnyClip starting (node \(nodeID.prefix(8)), name=\(config.name))")

        do {
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask { try await link.serve() }
                group.addTask { try await watcher.run() }
                // Drain outbound clips on a dedicated task: a stuck send stalls
                // only here, never the watcher's poll loop.
                group.addTask { await outbound.run(send: sendOutbound) }
                group.addTask { try await mdnsReconnectLoop(beacon: beacon, link: link) }
                group.addTask { try await networkWatchdog(beacon: beacon) }
                group.addTask { try await idleLinkWatchdog(beacon: beacon, link: link) }
                group.addTask { try await linkPingLoop(link: link) }
                group.addTask { [emit] in
                    let result = try await runProbe(
                        eventsSeen: { await beacon.eventsSeen },
                        hasNetwork: { primaryIPv4() != nil })
                    switch result {
                    case .blockedLocalNetwork:
                        AnyLog.shared.warning(
                            "permission probe: no mDNS activity in 30s -- "
                            + "Local Network permission likely blocked")
                        emit(.permissionMissing(kind: "local_network"))
                    case .noNetwork:
                        AnyLog.shared.warning("permission probe: no active network interface")
                        emit(.permissionMissing(kind: "no_network"))
                    case .ok:
                        AnyLog.shared.debug("permission probe: ok")
                    }
                }
                // Wait for ALL tasks (= asyncio.gather): the first throw
                // cancels the rest and the for-loop rethrows.
                for try await _ in group {}
            }
        } catch {
            // Cleanup runs even on CancellationError — file ops and
            // shutdown() do not check task cancellation, so they execute
            // cleanly in a cancelled task context. Rethrow afterward so
            // runForever sees CancellationError and exits.
            await cleanup(link: link, beacon: beacon, receivedDir: receivedDir)
            throw error
        }
        await cleanup(link: link, beacon: beacon, receivedDir: receivedDir)
    }

    private func cleanup(link: PeerLink, beacon: MdnsBeacon, receivedDir: URL) async {
        await link.shutdown()
        await beacon.stop()
        PidLock.release(dir: stateDir)
        clearDirectoryFiles(receivedDir)
    }
}
