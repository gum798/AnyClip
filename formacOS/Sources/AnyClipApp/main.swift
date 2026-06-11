import AppKit

let app = NSApplication.shared
app.setActivationPolicy(.accessory) // menu bar only; also LSUIElement in Info.plist
let delegate = AppDelegate()
app.delegate = delegate
app.run()
