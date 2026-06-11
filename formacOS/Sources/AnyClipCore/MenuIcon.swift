/// Pure mapping from UI state to the menu-bar glyph. The menu bar shows
/// "@" when linked; when NOT linked the glyph is highlighted (rendered in
/// red by the AppKit shell) so a broken link is eye-catching, and errors
/// add "!".
public struct MenuIconSpec: Equatable, Sendable {
    public let text: String
    /// true = render attention color (not linked)
    public let highlighted: Bool
    public init(text: String, highlighted: Bool) {
        self.text = text
        self.highlighted = highlighted
    }
}

public func menuIconSpec(for state: PeerUIState) -> MenuIconSpec {
    switch state.kind {
    case .linked: return MenuIconSpec(text: "@", highlighted: false)
    case .error: return MenuIconSpec(text: "@!", highlighted: true)
    case .idle, .searching: return MenuIconSpec(text: "@", highlighted: true)
    }
}
