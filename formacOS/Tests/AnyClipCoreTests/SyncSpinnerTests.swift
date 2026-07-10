import Testing
@testable import AnyClipCore

// Pure model behind the menu-bar sync spinner (Claude-style braille spinner).
// The AppKit Timer/glyph rendering lives in StatusItemController; this type
// owns the testable logic: which frame to show, and whether to keep spinning.

@Test func freshSpinnerIsNotSpinning() {
    let s = SyncSpinner()
    #expect(s.isSpinning(now: 0) == false)
    #expect(s.isSpinning(now: 100) == false)
}

@Test func triggerSpinsForTheSpinWindowThenStops() {
    var s = SyncSpinner()
    s.trigger(now: 10)
    #expect(s.isSpinning(now: 10))                                    // just triggered
    #expect(s.isSpinning(now: 10 + SyncSpinner.spinWindow - 0.01))    // within window
    #expect(s.isSpinning(now: 10 + SyncSpinner.spinWindow) == false)  // window elapsed
}

@Test func retriggerExtendsTheSpinWindow() {
    var s = SyncSpinner()
    s.trigger(now: 0)
    s.trigger(now: 0.5)                              // copied again mid-spin
    // Past the original 0.9 deadline, but within the extended 0.5 + 0.9 = 1.4.
    #expect(s.isSpinning(now: 1.0))
    #expect(s.isSpinning(now: 1.4) == false)
}

@Test func nextFrameCyclesThroughAllFramesThenWraps() {
    var s = SyncSpinner()
    var seen: [String] = []
    for _ in 0..<SyncSpinner.frames.count { seen.append(s.nextFrame()) }
    #expect(seen == SyncSpinner.frames)               // one full cycle, in order
    #expect(s.nextFrame() == SyncSpinner.frames[0])   // then wraps to the start
}

@Test func framesAreTheClaudeBrailleSpinner() {
    #expect(SyncSpinner.frames == ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"])
}
