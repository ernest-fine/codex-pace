import Foundation
import UserNotifications

@MainActor
final class BudgetNotifications: NSObject, UNUserNotificationCenterDelegate {
    private let center = UNUserNotificationCenter.current()
    private let defaults = UserDefaults.standard
    private let historyKey = "sentBudgetWarnings"
    private var requestedPermission: Task<Bool, Never>?
    private var pending = Set<String>()
    private var current: BudgetWarning?

    func start() {
        center.delegate = self
        Task { _ = await canDeliver() }
    }

    func update(_ warning: BudgetWarning?) {
        current = warning
        guard let warning,
              warning.expiresAt > Date(),
              deliveredWarnings()[warning.identifier] == nil,
              pending.insert(warning.identifier).inserted else { return }

        Task {
            defer { pending.remove(warning.identifier) }
            guard await canDeliver(),
                  let latest = current,
                  warning.identifier == latest.identifier,
                  latest.expiresAt > Date() else { return }

            do {
                try await submit(latest, identifier: latest.identifier, test: false)
                var sent = deliveredWarnings()
                // Keep the receipt until reset even if the hours for this shift change.
                sent[latest.identifier] = Date().addingTimeInterval(8 * 86400).timeIntervalSince1970
                defaults.set(sent, forKey: historyKey)
            } catch {
                // The next telemetry refresh can retry a request macOS did not accept.
            }
        }
    }

    func sendTest(_ warning: BudgetWarning) async throws -> Bool {
        guard await canDeliver() else {
            throw NotificationError.disabled
        }
        let identifier = "test-budget-warning.\(UUID().uuidString)"
        try await submit(warning, identifier: identifier, test: true)
        for _ in 0..<10 {
            if await center.deliveredNotifications().contains(where: { $0.request.identifier == identifier }) {
                return true
            }
            try await Task.sleep(for: .milliseconds(200))
        }
        return false
    }

    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping @Sendable (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list])
    }

    private func canDeliver() async -> Bool {
        let settings = await center.notificationSettings()
        switch settings.authorizationStatus {
        case .authorized, .provisional:
            return settings.alertSetting == .enabled || settings.notificationCenterSetting == .enabled
        case .notDetermined:
            if let requestedPermission { return await requestedPermission.value }
            let request = Task { (try? await center.requestAuthorization(options: [.alert])) ?? false }
            requestedPermission = request
            let allowed = await request.value
            requestedPermission = nil
            return allowed
        default:
            return false
        }
    }

    private func deliveredWarnings() -> [String: TimeInterval] {
        let saved = defaults.dictionary(forKey: historyKey) as? [String: TimeInterval] ?? [:]
        let now = Date().timeIntervalSince1970
        return saved.filter { $0.value > now }
    }

    private func submit(_ warning: BudgetWarning, identifier: String, test: Bool) async throws {
        let content = UNMutableNotificationContent()
        content.title = test ? "Test · \(warning.title)" : warning.title
        content.body = "\(warning.recommendation)\n\(warning.detail)"
        let request = UNNotificationRequest(identifier: identifier, content: content, trigger: nil)
        try await center.add(request)
    }

    private enum NotificationError: LocalizedError {
        case disabled

        var errorDescription: String? {
            "Allow Codex Ticker notifications in System Settings → Notifications, then try again."
        }
    }
}
