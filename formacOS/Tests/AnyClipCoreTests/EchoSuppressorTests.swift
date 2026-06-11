import Testing
@testable import AnyClipCore

@Test func sendsWhenNothingReceived() {
    let s = EchoSuppressor()
    #expect(s.shouldSend(kind: "text", payloadHash: "h1"))
}

@Test func suppressesEchoOfJustReceived() {
    var s = EchoSuppressor()
    s.markReceived(kind: "text", payloadHash: "h1")
    #expect(!s.shouldSend(kind: "text", payloadHash: "h1"))
    #expect(s.shouldSend(kind: "text", payloadHash: "h2"))
}

@Test func kindsAreTrackedIndependently() {
    var s = EchoSuppressor()
    s.markReceived(kind: "text", payloadHash: "h1")
    #expect(s.shouldSend(kind: "image", payloadHash: "h1"))
}
