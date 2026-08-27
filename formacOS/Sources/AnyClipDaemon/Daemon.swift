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
/// understands kind:"files" (pass through, dropped == 0) — including each
/// entry's optional path, which a peer below minor 3 simply ignores and writes
/// flat. Minor 0 predates multi-file sync: degrade to the first LOOSE file as
/// legacy kind:"file". Folder-derived entries are EXCLUDED from that fallback
/// (a tree cannot be expressed in one kind:"file" frame), so a folder-only
/// clip sends NOTHING on a minor-0 link — logged, never toasted. `dropped`
/// counts the entries left behind for the notification.
/// Returns a nil payload for an empty .files batch and for a folder-only clip
/// to a minor-0 peer. Keep in lockstep with anyclip.downgrade_for_peer.
public func downgradeForPeer(
    _ payload: ClipPayload, peerMinor: Int
) -> (payload: ClipPayload?, dropped: Int) {
    guard case .files(let fs) = payload, peerMinor < 1 else { return (payload, 0) }
    guard let first = fs.first(where: { $0.relPath == nil }) else { return (nil, 0) }
    return (.file(name: first.name, data: first.data), fs.count - 1)
}

/// One aggregated toast for the peers a clip was too large for — their protocol
/// is < 1.2, so they still enforce the legacy 16 MiB receive cap and the
/// fan-out skipped them (their links stayed up). nil when nothing was skipped;
/// at most ONE per clip. Keep in lockstep with anyclip.size_skip_message.
public func sizeSkipMessage(_ names: [String]) -> String? {
    guard !names.isEmpty else { return nil }
    if names.count == 1 {
        return "clip not sent to \(names[0]) (too large for its AnyClip version)"
    }
    return "clip not sent to \(names.count) peer(s) (too large for their AnyClip version)"
}

/// True when a received files clip ended up as exactly ONE placed top-level
/// item and that item is a LOOSE file rather than a folder — the only case in
/// which the watcher would re-detect it as a single-file copy (kind "file").
/// A placed FOLDER re-surfaces as kind:"files" and needs no extra seeding.
/// Pure so the decision itself has a unit test, exactly like
/// anyclip.placed_single_loose_file.
public func placedSingleLooseFile(_ placed: PlacedFiles) -> Bool {
    placed.topLevelItems.count == 1 && placed.folderTops.isEmpty
}

/// Toast body for an inbound kind:"files" batch. A folder-only clip names the
/// folder ("<top> (N files)"); anything else keeps today's "N files".
/// Keep in lockstep with anyclip.received_clip_message.
public func receivedFilesBody(_ placed: PlacedFiles) -> String {
    if placed.folderTops.count == 1, placed.topLevelItems.count == 1 {
        return "\(placed.folderTops[0]) (\(placed.files.count) files)"
    }
    return "\(placed.files.count) files"
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

        let manager = LinkManager(
            config: LinkManager.LinkConfig(
                token: config.token, port: config.port,
                name: config.name, appVersion: appVersion),
            nodeID: nodeID)

        // Holder breaks the watcher <-> apply callback cycle.
        let watcherBox = Locked<ClipboardWatcher?>(nil)

        // Serialized inbound applies: every link delivers here; a single drain
        // task applies them in FIFO order so markReceived + the clipboard write
        // stay ordered across ALL peers. That ordering (apply order == mark
        // order) is what keeps the single-slot-per-kind suppressor sufficient
        // under N peers.
        let (inboundStream, inboundCont) =
            AsyncStream.makeStream(of: (ClipPayload, String).self)

        let applyClip: @Sendable (ClipPayload, String) async -> Void = { payload, peer in
            // Mark BEFORE writing local clipboard so the outbound poller sees the
            // suppression flag in time.
            await coordinator.markReceived(kind: payload.kind, hash: payload.payloadHash)
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
                // updateLocalFile(s) is async now: it does its disk work off the
                // main actor, so it is awaited directly rather than wrapped in
                // MainActor.run. ClipboardWatcher is @MainActor (and therefore
                // Sendable), so the box hand-off is safe from here.
                var ok = false
                if let watcher = watcherBox.get() {
                    ok = await watcher.updateLocalFile(name: name, data: data)
                }
                AnyLog.shared.info(
                    "<- received file \(name) \(data.count) bytes from \(peer) "
                    + "(\(ok ? "written to clipboard" : "WRITE FAILED"))")
                notify("AnyClip ← \(peer)", "file: \(name) (\(data.count / 1024) KB)")
            case .files(let fs):
                var placed = PlacedFiles()
                if let watcher = watcherBox.get() {
                    placed = await watcher.updateLocalFiles(fs)
                }
                // If a lone LOOSE file landed, the watcher re-detects it as a
                // single-file copy (kind "file"), so also suppress that hash.
                if placedSingleLooseFile(placed), let only = placed.files.first {
                    await coordinator.markReceived(
                        kind: "file", hash: sha256Hex(only.data))
                }
                AnyLog.shared.info(
                    "<- received \(fs.count) files from \(peer) "
                    + "(\(placed.files.count) written, \(placed.folderTops.count) folder(s))")
                notify("AnyClip ← \(peer)", receivedFilesBody(placed))
            }
        }

        await manager.setHandlers(
            onClip: { payload, peer in inboundCont.yield((payload, peer)) },
            emit: emit)

        // Outbound sends run on their OWN task, fed by a non-blocking queue, so
        // the clipboard poll loop can NEVER be frozen by a stalled send.
        let outbound = OutboundQueue()

        let sendOutbound: @Sendable (ClipPayload) async -> Void = { rawPayload in
            // Global echo-suppression, evaluated ONCE per local copy: a
            // just-received clip must not be rebroadcast to any peer. This is
            // what makes the full mesh relay-free.
            guard await coordinator.shouldSend(
                kind: rawPayload.kind, hash: rawPayload.payloadHash)
            else {
                AnyLog.shared.debug("skip echo of just-received \(rawPayload.kind)")
                return
            }
            let result = await manager.broadcast(rawPayload)
            // Per-peer `-> sent` LOG lines stay (logs are fine per peer), but the
            // toast is aggregated to ONE per local copy: an 8-peer mesh otherwise
            // fired 8 toasts for a single copy. Parity with the C# tray (one toast
            // whose title lists the delivered peer names, sorted + comma-joined).
            for d in result.delivered {
                switch d.payload {
                case .text(let text):
                    AnyLog.shared.info("-> sent text \(text.count) chars to \(d.peerName)")
                case .image(let png):
                    AnyLog.shared.info("-> sent image \(png.count) bytes to \(d.peerName)")
                case .file(let name, let data):
                    AnyLog.shared.info("-> sent file \(name) \(data.count) bytes to \(d.peerName)")
                case .files(let fs):
                    let total = fs.reduce(0) { $0 + $1.data.count }
                    AnyLog.shared.info("-> sent \(fs.count) files \(total) bytes to \(d.peerName)")
                }
            }
            if !result.delivered.isEmpty {
                let peers = result.delivered.map { $0.peerName }.sorted().joined(separator: ", ")
                // Body keyed off the local payload kind (one copy -> one toast).
                switch rawPayload {
                case .text(let text):
                    notify("AnyClip → \(peers)", preview(text))
                case .image(let png):
                    notify("AnyClip → \(peers)", "image (\(png.count / 1024) KB)")
                case .file(let name, let data):
                    notify("AnyClip → \(peers)", "file: \(name) (\(data.count / 1024) KB)")
                case .files(let fs):
                    notify("AnyClip → \(peers)", "\(fs.count) files")
                }
            }
            // Old-peer fallback aggregated into ONE toast across all peers (same
            // principle as the folder-skip aggregation, commit d8894a0).
            if result.maxDropped > 0 {
                notify("AnyClip",
                    "\(result.maxDropped) file(s) not synced — update the peer to receive multiple files")
            }
            // Same aggregation for the peers the legacy 16 MiB size gate
            // skipped: ONE toast per local copy, never one per peer.
            if let message = sizeSkipMessage(result.sizeSkipped) {
                notify("AnyClip", message)
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
            onPeer: { [weak manager] endpoint, label, peerID in
                guard let manager else { return }
                // Skip peers we already mesh with (keyed by the mDNS TXT id) so a
                // re-discovery never triggers a spurious reconnect/replacement.
                if await manager.hasLink(nodeID: peerID) { return }
                _ = await manager.tryConnect(to: endpoint, label: label)
            })

        let txtData = TXTCodec.encode([
            ("id", nodeID),
            ("version", "\(Wire.legacyVersion)"),
            ("app_version", appVersion),
            ("protocol_major", "\(Wire.protocolMajor)"),
            ("protocol_minor", "\(Wire.protocolMinor)"),
        ])
        await manager.configureAdvertising(
            instanceName: "\(config.name)-\(nodeID.prefix(8))", txtData: txtData)
        await beacon.start()
        AnyLog.shared.info(
            "AnyClip starting (node \(nodeID.prefix(8)), name=\(config.name))")

        do {
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask { try await manager.serve() }
                group.addTask { try await watcher.run() }
                group.addTask { await outbound.run(send: sendOutbound) }
                // Serialized inbound applies (ONE drain across all links).
                group.addTask {
                    for await (payload, peer) in inboundStream {
                        if Task.isCancelled { break }
                        await applyClip(payload, peer)
                    }
                }
                group.addTask { try await mdnsReconnectLoop(beacon: beacon, manager: manager) }
                group.addTask { try await networkWatchdog(beacon: beacon) }
                group.addTask { try await idleLinkWatchdog(beacon: beacon, manager: manager) }
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
                for try await _ in group {}
            }
        } catch {
            inboundCont.finish()
            await cleanup(manager: manager, beacon: beacon, receivedDir: receivedDir)
            throw error
        }
        inboundCont.finish()
        await cleanup(manager: manager, beacon: beacon, receivedDir: receivedDir)
    }

    private func cleanup(manager: LinkManager, beacon: MdnsBeacon, receivedDir: URL) async {
        await manager.shutdown()
        await beacon.stop()
        PidLock.release(dir: stateDir)
        clearDirectoryFiles(receivedDir)
    }
}
