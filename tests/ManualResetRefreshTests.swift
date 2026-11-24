import Foundation
@testable import CodexPace

@main
@MainActor
struct ManualResetRefreshTests {
    struct Failure: LocalizedError {
        let errorDescription: String?
        init(_ message: String) { errorDescription = message }
    }

    static func main() async {
        do {
            try await run()
        } catch {
            FileHandle.standardError.write(Data("\(error.localizedDescription)\n".utf8))
            exit(1)
        }
    }

    static func run() async throws {
        let args = CommandLine.arguments
        guard args.count == 4, let start = TimeInterval(args[3]),
              FileManager.default.homeDirectoryForCurrentUser.resolvingSymlinksInPath().path ==
                URL(fileURLWithPath: args[2]).resolvingSymlinksInPath().path,
              Bundle.main.bundleIdentifier?.hasPrefix("local.codex-pace.test-") == true else {
            throw Failure("Run tests/manual-reset-refresh.sh to create isolated preferences and quota readings.")
        }
        let fixture = URL(fileURLWithPath: args[2]).appendingPathComponent(".codex/archived_sessions")
        let now = Date(timeIntervalSince1970: start)
        let day: TimeInterval = 86_400
        let target = now.addingTimeInterval(4 * day)
        let original = now.addingTimeInterval(5 * day)
        let next = now.addingTimeInterval(7 * day)
        let earlier = now.addingTimeInterval(2 * day)
        let store = TrackerStore()

        switch args[1] {
        case "save":
            try await refresh(store, fixture: fixture, observed: now, reset: original)
            guard store.changeResetTarget(target), store.data.pace?.planEndsAt == target,
                  store.data.usesManualResetTarget else {
                throw Failure("Could not set the manual target on the initial quota.")
            }
            try await refresh(store, fixture: fixture, observed: now.addingTimeInterval(1), reset: next)
            guard store.manualResetTargetDate == target, store.data.pace?.planEndsAt == target,
                  store.data.usesManualResetTarget else {
                throw Failure("Refresh discarded a future target that still fits the new quota window.")
            }
            try await refresh(store, fixture: fixture, observed: now.addingTimeInterval(2), reset: earlier)
            guard store.manualResetTargetDate == target, store.data.pace?.planEndsAt == earlier,
                  !store.data.usesManualResetTarget else {
                throw Failure("An earlier account reset should suspend pacing to the target and keep it visible.")
            }
            try await refresh(store, fixture: fixture, observed: now.addingTimeInterval(3), reset: target)
            guard store.manualResetTargetDate == target, store.data.pace?.planEndsAt == target,
                  !store.data.usesManualResetTarget else {
                throw Failure("A saved target equal to the account reset should stay visible and inactive.")
            }
            guard UserDefaults.standard.data(forKey: "manualResetTarget") != nil else {
                throw Failure("An earlier account reset erased the saved target.")
            }
            guard UserDefaults.standard.synchronize() else { throw Failure("Could not flush the test preferences.") }
        case "relaunch":
            try await refresh(store, fixture: fixture, observed: now.addingTimeInterval(4), reset: next)
            guard store.manualResetTargetDate == target, store.data.pace?.planEndsAt == target,
                  store.data.usesManualResetTarget else {
                throw Failure("Relaunch did not restore the saved target when it became usable again.")
            }
            guard store.changeResetTarget(nil) else { throw Failure("Could not clear the target.") }
            try await refresh(store, fixture: fixture, observed: now.addingTimeInterval(5), reset: next)
            guard store.manualResetTargetDate == nil, store.data.pace?.planEndsAt == next,
                  !store.data.usesManualResetTarget,
                  UserDefaults.standard.data(forKey: "manualResetTarget") == nil,
                  UserDefaults.standard.synchronize() else {
                throw Failure("The cleared target returned after refresh.")
            }
        case "after-clear":
            try await refresh(store, fixture: fixture, observed: now.addingTimeInterval(6), reset: next)
            guard store.manualResetTargetDate == nil, store.data.pace?.planEndsAt == next,
                  !store.data.usesManualResetTarget else {
                throw Failure("The cleared target returned after relaunch.")
            }
        default:
            throw Failure("Unknown test phase.")
        }
    }

    static func refresh(_ store: TrackerStore, fixture: URL, observed: Date, reset: Date) async throws {
        // Write the input consumed by the local-history fallback on both automatic and manual refresh.
        let limits: [String: Any] = ["limit_id": "codex", "secondary": [
            "used_percent": 12, "window_minutes": 10080, "resets_at": reset.timeIntervalSince1970
        ]]
        let record: [String: Any] = [
            "type": "event_msg", "timestamp": ISO8601DateFormatter().string(from: observed),
            "payload": ["type": "token_count", "rate_limits": limits]
        ]
        let data = try JSONSerialization.data(withJSONObject: record)
        try data.write(to: fixture.appendingPathComponent("\(Int(observed.timeIntervalSince1970)).jsonl"))
        store.refresh()
        let deadline = ContinuousClock.now + .seconds(10)
        while store.data.isRefreshing && ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        guard !store.data.isRefreshing, store.data.quota?.observedAt == observed, store.data.quota?.resetsAt == reset,
              store.data.source == "Local Codex history" else {
            throw Failure("The store did not finish refreshing from the fixture.")
        }
    }
}
