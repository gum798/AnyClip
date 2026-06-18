/// Pure builders for the detached update helper invocation (kept out of the
/// runtime so the exact command is unit-testable without spawning anything).
public enum UpdateCommand {
    /// `/bin/sh -c` script: wait for `pid` to exit, run the cask upgrade,
    /// relaunch the app; on upgrade failure open the releases page instead.
    public static func macHelperScript(
        pid: Int32, brewPath: String, appName: String, releasesURL: String
    ) -> String {
        """
        while kill -0 \(pid) 2>/dev/null; do sleep 0.3; done
        if \(brewPath) upgrade --cask anyclip; then /usr/bin/open -a "\(appName)"; else /usr/bin/open "\(releasesURL)"; fi
        """
    }
}
