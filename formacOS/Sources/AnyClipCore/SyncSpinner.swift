/// Pure model behind the menu-bar sync spinner — a Claude-style braille spinner
/// shown on the menu-bar glyph while clipboard syncs happen. The AppKit Timer
/// and glyph rendering live in StatusItemController; this type owns the testable
/// logic: which frame to show next, and whether to keep spinning.
public struct SyncSpinner: Sendable {
    /// The same 10-frame braille spinner Claude Code shows in the terminal.
    public static let frames: [String] = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"]
    /// Seconds between frames (~12.5 fps reads as a smooth spin).
    public static let frameInterval: Double = 0.08
    /// How long a single sync keeps the spinner going. Re-triggers extend it, so
    /// a burst of copies spins continuously — like Claude spins while it works.
    public static let spinWindow: Double = 0.9

    private var index = 0
    private var deadline: Double = 0

    public init() {}

    /// A sync happened at `now`: (re)start or extend the spin window.
    public mutating func trigger(now: Double) {
        deadline = now + Self.spinWindow
    }

    /// Should the spinner keep animating at `now`?
    public func isSpinning(now: Double) -> Bool {
        now < deadline
    }

    /// The glyph to show now; advances to the next frame, wrapping at the end.
    public mutating func nextFrame() -> String {
        let glyph = Self.frames[index]
        index = (index + 1) % Self.frames.count
        return glyph
    }
}
