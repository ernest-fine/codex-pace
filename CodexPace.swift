import AppKit
import SwiftUI

@MainActor
final class TrackerStore: ObservableObject {
    @Published private(set) var data: DashboardData
    @Published private(set) var notificationTestStatus: String?
    @Published private(set) var workdays: Set<Workday> = Workday.standard
    @Published var schedule: WorkSchedule {
        didSet {
            if demo == nil, let encoded = try? JSONEncoder().encode(schedule) {
                defaults.set(encoded, forKey: "workSchedule")
            }
            updateDashboard()
        }
    }
    @Published var sessionTemplates: [SavedSessionTemplate] = [] {
        didSet {
            if demo == nil, let encoded = try? JSONEncoder().encode(sessionTemplates) {
                defaults.set(encoded, forKey: "sessionTemplates")
            }
        }
    }

    private let defaults = UserDefaults.standard
    private let notifications = BudgetNotifications()
    private let demo: String?
    private var timer: Timer?
    private var refreshing = false
    private var lastRefresh = Date.distantPast
    private var telemetry: Telemetry?
    private var latestObservedQuota: QuotaSample?
    private var history: [QuotaSample] = []
    private var resetTarget: ManualResetTarget?

    var manualResetTargetDate: Date? {
        guard let resetTarget, let quota = data.quota, data.pace != nil,
              ManualResetTarget.sameWindow(resetTarget.windowResetsAt, quota.resetsAt) else {
            return nil
        }
        return resetTarget.targetAt
    }

    init() {
        demo = ProcessInfo.processInfo.arguments.first { $0.hasPrefix("--demo") }
        let savedSchedule = defaults.data(forKey: "workSchedule").flatMap {
            try? JSONDecoder().decode(WorkSchedule.self, from: $0)
        }
        let initialSchedule = savedSchedule.flatMap { $0.isValid ? $0 : nil } ?? .standard
        schedule = initialSchedule
        if demo == nil, let stored = defaults.data(forKey: "sessionTemplates"),
           let templates = try? JSONDecoder().decode([SavedSessionTemplate].self, from: stored) {
            sessionTemplates = templates.filter(\.isValid)
        }
        if demo == nil, let stored = defaults.data(forKey: "quotaHistory") {
            history = (try? JSONDecoder().decode([QuotaSample].self, from: stored)) ?? []
        }
        if demo == nil, let stored = defaults.data(forKey: "manualResetTarget") {
            resetTarget = try? JSONDecoder().decode(ManualResetTarget.self, from: stored)
        }
        if demo == nil, let stored = defaults.data(forKey: "workdays"),
           let days = try? JSONDecoder().decode([Workday].self, from: stored) {
            workdays = Set(days)
        }
        data = dashboardData(telemetry: nil, history: [],
                             now: Date(), isRefreshing: false, isDemo: demo != nil, schedule: initialSchedule)
    }

    func start() {
        guard timer == nil else { return }
        if demo == nil { notifications.start() }
        loadTelemetry(force: false)
        timer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refreshOnSchedule() }
        }
    }

    private func refreshOnSchedule() {
        let now = Date()
        let sessionActive = demo == nil && data.pace?.sessions.contains {
            $0.start <= now && now < $0.end
        } == true
        if sessionActive || now.timeIntervalSince(lastRefresh) >= 120 {
            loadTelemetry(force: false)
        }
    }

    func refresh() {
        loadTelemetry(force: true)
    }

    func changeWorkdays(_ days: Set<Workday>) {
        guard days != workdays else { return }
        workdays = days
        if demo == nil, let encoded = try? JSONEncoder().encode(Workday.allCases.filter(days.contains)) {
            defaults.set(encoded, forKey: "workdays")
        }
        let now = currentTime
        updateDashboard(now: now)
        updateBudgetWarning(now: now)
    }

    func changeResetTarget(_ date: Date?, for expectedReset: Date) -> Bool {
        let now = currentTime
        guard let quota = telemetry?.quota, quota.resetsAt > now,
              ManualResetTarget.sameWindow(expectedReset, quota.resetsAt) else {
            return false
        }
        if let date {
            let target = ManualResetTarget(targetAt: date, windowResetsAt: quota.resetsAt)
            guard target.activeDeadline(for: quota, now: now) != nil else { return false }
            saveResetTarget(target)
        } else {
            saveResetTarget(nil)
        }
        updateDashboard(now: now)
        updateBudgetWarning(now: now)
        return true
    }

    func sendTestNotification() {
        let example = Self.example(healthy: false)
        let preview = dashboardData(
            telemetry: example.telemetry, history: example.telemetry.history,
            now: example.now, isRefreshing: false, isDemo: true
        )
        guard let pace = preview.pace, let quota = preview.quota,
              let warning = budgetNotification(for: pace, quota: quota, now: example.now) else { return }
        notificationTestStatus = "Sending test notification…"
        Task {
            do {
                let delivered = try await notifications.sendTest(warning)
                notificationTestStatus = delivered
                    ? "Test delivered to Notification Center"
                    : "Test submitted to macOS"
            } catch {
                notificationTestStatus = nil
                let alert = NSAlert()
                alert.messageText = "Couldn’t send the test notification"
                alert.informativeText = error.localizedDescription
                alert.runModal()
            }
        }
    }

    private func loadTelemetry(force: Bool) {
        guard !refreshing else { return }
        lastRefresh = Date()
        if let demo {
            let example = Self.example(healthy: demo == "--demo=healthy")
            telemetry = example.telemetry
            history = example.telemetry.history
            updateDashboard(now: example.now)
            return
        }
        refreshing = true
        updateDashboard()
        Task {
            let result = await Task.detached(priority: .utility) {
                readTelemetry(now: Date(), force: force)
            }.value
            let now = Date()
            latestObservedQuota = result.source == "Codex account" ? result.quota : nil
            if let target = resetTarget, let observed = result.quota,
               !ManualResetTarget.sameWindow(target.windowResetsAt, observed.resetsAt) {
                saveResetTarget(nil)
            }
            let savedQuota = history.filter { $0.observedAt <= now }.max {
                $0.observedAt < $1.observedAt
            }
            let quota = result.quota ?? savedQuota
            telemetry = Telemetry(
                quota: quota, shortQuota: result.shortQuota, history: result.history,
                activity: result.activity, models: result.models,
                source: result.quota == nil && savedQuota != nil ? "Last saved account reading" : result.source,
                error: result.error
            )
            if let quota {
                let candidates = history + result.history + [quota]
                var byTime: [Date: QuotaSample] = [:]
                for sample in candidates where sample.resetsAt == quota.resetsAt &&
                    sample.windowMinutes == quota.windowMinutes &&
                    sample.observedAt <= now && (0...100).contains(sample.usedPercent) {
                    byTime[sample.observedAt] = sample
                }
                let ordered = byTime.values.sorted { $0.observedAt < $1.observedAt }
                history = Array(ordered.suffix(2400))
                if let encoded = try? JSONEncoder().encode(history) {
                    defaults.set(encoded, forKey: "quotaHistory")
                }
            }
            refreshing = false
            updateDashboard(now: now)
            updateBudgetWarning(now: now)
        }
    }

    private var currentTime: Date {
        demo.map { Self.example(healthy: $0 == "--demo=healthy").now } ?? Date()
    }

    private func saveResetTarget(_ target: ManualResetTarget?) {
        resetTarget = target
        guard demo == nil else { return }
        if let target, let encoded = try? JSONEncoder().encode(target) {
            defaults.set(encoded, forKey: "manualResetTarget")
        } else {
            defaults.removeObject(forKey: "manualResetTarget")
        }
    }

    private func updateBudgetWarning(now: Date) {
        guard demo == nil else { return }
        let warning = latestObservedQuota.flatMap { quota -> BudgetWarning? in
            guard quota == data.quota, let pace = data.pace else { return nil }
            return budgetNotification(for: pace, quota: quota, now: now)
        }
        notifications.update(warning)
    }

    private func updateDashboard(now: Date? = nil) {
        let time = now ?? currentTime
        data = dashboardData(telemetry: telemetry, history: history,
                             now: time, isRefreshing: refreshing, isDemo: demo != nil,
                             schedule: schedule, resetTarget: resetTarget, workdays: workdays)
    }

    private static func example(healthy: Bool) -> (telemetry: Telemetry, now: Date) {
        let calendar = WorkClock.calendar
        let now = calendar.date(from: DateComponents(year: 2026, month: 11, day: 18, hour: 15))!
        let reset = calendar.date(from: DateComponents(year: 2026, month: 11, day: 23, hour: 21, minute: 15))!
        let amounts: [Double] = healthy ? [35, 36, 38, 38, 39] : [49, 53, 57, 57, 60]
        let offsets: [TimeInterval] = [4.5, 3.5, 2.5, 0.25, 0]
        let samples = zip(amounts, offsets).map { used, hours in
            QuotaSample(observedAt: now.addingTimeInterval(-hours * 3600),
                        resetsAt: reset, windowMinutes: 10080, usedPercent: used)
        }
        let models = [
            ModelOption(id: "gpt-6.1-sol", name: "GPT-6.1 Sol",
                        efforts: ["low", "medium", "high", "xhigh", "max", "ultra"],
                        defaultEffort: "medium", isDefault: true, supportsFastMode: true),
            ModelOption(id: "gpt-6-luna", name: "GPT-6 Luna",
                        efforts: ["low", "medium", "high", "max"],
                        defaultEffort: "high", isDefault: false, supportsFastMode: true),
        ]
        let activity = Activity(observedAt: now, model: "gpt-6.1-sol", effort: "high",
                                turns: healthy ? 2 : 4, tokens: healthy ? 55_000 : 180_000)
        return (Telemetry(quota: samples.last, shortQuota: nil, history: samples, activity: activity,
                          models: models, source: "Example data", error: nil), now)
    }
}

@main
struct CodexPaceApp: App {
    @StateObject private var store = TrackerStore()
    @Environment(\.openWindow) private var openWindow
    @AppStorage("weeklyTargetDisplay") private var targetDisplay: WeeklyTargetDisplay = .left
    @AppStorage("heroFocus") private var heroFocus: HeroFocus = .session

    var body: some Scene {
        let menuBarSummary = MenuBarSummary.make(
            data: store.data, now: Date(), focus: heroFocus, display: targetDisplay
        )
        Window("Codex Pace", id: "dashboard") {
            Dashboard(
                data: store.data, schedule: $store.schedule, sessionTemplates: $store.sessionTemplates,
                workdays: store.workdays, changeWorkdays: store.changeWorkdays,
                manualResetTarget: store.manualResetTargetDate, changeResetTarget: store.changeResetTarget,
                refresh: store.refresh, targetDisplay: $targetDisplay, heroFocus: $heroFocus
            )
                .task { store.start() }
        }
        .defaultSize(width: 1020, height: 830)
        .commands {
            CommandMenu("Notifications") {
                Button("Send Test Notification", action: store.sendTestNotification)
                if let status = store.notificationTestStatus {
                    Text(status)
                }
            }
        }

        MenuBarExtra {
            Text(menuBarSummary.title)
            Divider()
            Text(store.data.status)
            Text(store.data.recommendation)
            Divider()
            Button("Open dashboard") {
                openWindow(id: "dashboard")
                NSApp.activate()
                store.start()
            }
            Button("Refresh usage", action: store.refresh)
                .disabled(store.data.isRefreshing)
            Divider()
            Button("Quit Codex Pace") { NSApp.terminate(nil) }
                .keyboardShortcut("q")
        } label: {
            MenuBarStatusLabel(summary: menuBarSummary)
        }
    }
}
