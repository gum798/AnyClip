import Foundation

public enum UpdateStatus: Equatable, Sendable {
    case upToDate(current: String)
    case available(latest: String, url: String)
    case failed(reason: String)
}

/// Pure update detection. Network IO is injected via `fetch`, so the
/// parse/compare logic is unit-testable without hitting GitHub.
public enum UpdateChecker {
    public static let releasesApiURL =
        "https://api.github.com/repos/gum798/AnyClip/releases/latest"
    public static let releasesPageURL =
        "https://github.com/gum798/AnyClip/releases/latest"

    /// `tag_name` from GitHub releases JSON, leading "v" stripped. nil if absent/malformed.
    public static func parseLatestTag(_ json: String) -> String? {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = obj["tag_name"] as? String else { return nil }
        let v = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        return v.isEmpty ? nil : v
    }

    /// Semver-ish: numeric components dominate; a pre-release ("-" suffix)
    /// ranks below the same core; non-numeric components sort low.
    public static func compareVersions(_ a: String, _ b: String) -> ComparisonResult {
        let pa = parse(a), pb = parse(b)
        let n = max(pa.nums.count, pb.nums.count)
        for i in 0..<n {
            let x = i < pa.nums.count ? pa.nums[i] : 0
            let y = i < pb.nums.count ? pb.nums[i] : 0
            if x != y { return x < y ? .orderedAscending : .orderedDescending }
        }
        if pa.isPre != pb.isPre { return pa.isPre ? .orderedAscending : .orderedDescending }
        return .orderedSame
    }

    private static func parse(_ v: String) -> (nums: [Int], isPre: Bool) {
        let core = v.split(separator: "-", maxSplits: 1).first.map(String.init) ?? v
        let nums = core.split(separator: ".").map { Int($0) ?? -1 }
        return (nums, v.contains("-"))
    }

    public static func checkForUpdate(
        current: String, fetch: () async throws -> String
    ) async -> UpdateStatus {
        let body: String
        do { body = try await fetch() }
        catch { return .failed(reason: "\(error)") }
        guard let latest = parseLatestTag(body) else {
            return .failed(reason: "could not parse latest release")
        }
        return compareVersions(current, latest) == .orderedAscending
            ? .available(latest: latest, url: releasesPageURL)
            : .upToDate(current: current)
    }
}
