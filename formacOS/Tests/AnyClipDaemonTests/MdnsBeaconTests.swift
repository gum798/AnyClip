import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

private func makeBeacon(nodeID: String = "self-node") -> MdnsBeacon {
    MdnsBeacon(nodeID: nodeID, emit: { _ in }, onPeer: { _, _ in })
}

@Test func selfAdvertisementIsIgnored() async {
    let beacon = makeBeacon(nodeID: "self-node")
    await beacon.ingest(txt: ["id": "self-node"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "x")
    #expect(await beacon.eventsSeen == 0)
    #expect(await beacon.peersSnapshot().isEmpty)
}

@Test func nonSelfPeerIsRecordedAndCountsAsEvidence() async {
    let beacon = makeBeacon()
    await beacon.ingest(txt: ["id": "other-node"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "peer-1")
    #expect(await beacon.eventsSeen == 1)
    let peers = await beacon.peersSnapshot()
    #expect(peers.count == 1)
    #expect(peers[0].label == "peer-1")
}

@Test func missingTXTIdIsIgnored() async {
    let beacon = makeBeacon()
    await beacon.ingest(txt: [:], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "x")
    #expect(await beacon.peersSnapshot().isEmpty)
}

@Test func freshDiscoveryClearsFailureCount() async {
    let beacon = makeBeacon()
    await beacon.ingest(txt: ["id": "p"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    _ = await beacon.recordFail(label: "addr")
    _ = await beacon.recordFail(label: "addr")
    await beacon.ingest(txt: ["id": "p"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    #expect(await beacon.recordFail(label: "addr") == 1) // counter was reset
}

@Test func pruneRemovesAllNodeIdsForAddress() async {
    let beacon = makeBeacon()
    // Same address seen under two node ids (peer restarted, new uuid).
    await beacon.ingest(txt: ["id": "p1"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    await beacon.ingest(txt: ["id": "p2"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    #expect(await beacon.peersSnapshot().count == 1) // deduped by label
    await beacon.pruneAddress(label: "addr")
    #expect(await beacon.peersSnapshot().isEmpty)
}

@Test func snapshotDedupsByAddressLabel() async {
    let beacon = makeBeacon()
    await beacon.ingest(txt: ["id": "p1"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    await beacon.ingest(txt: ["id": "p2"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    await beacon.ingest(txt: ["id": "p3"], endpoint: .hostPort(host: "5.6.7.8", port: 24816), label: "addr2")
    #expect(await beacon.peersSnapshot().count == 2)
}
