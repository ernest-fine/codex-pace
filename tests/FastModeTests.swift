import Foundation

@main
@MainActor
struct FastModeTests {
    static func main() {
        readsFastCapabilityWithoutGuessingFromModelNames()
        liveCapabilityTakesPrecedenceOverTheCatalog()
        recommendsFastOnlyWithProvenHeadroom()
        recommendsStandardForPressureAndHeavyWork()
        omitsAdviceWhenCapabilityOrReadingsAreMissing()
        followsTheModelActuallySuggested()
        print("6 Fast mode checks passed")
    }

    static func readsFastCapabilityWithoutGuessingFromModelNames() {
        precondition(ModelOption.fastModeSupport(serviceTierIDs: ["priority"], additionalSpeedTiers: nil) == true)
        precondition(ModelOption.fastModeSupport(serviceTierIDs: ["fast"], additionalSpeedTiers: nil) == true)
        precondition(ModelOption.fastModeSupport(serviceTierIDs: nil, additionalSpeedTiers: ["fast"]) == true)
        precondition(ModelOption.fastModeSupport(serviceTierIDs: ["flex"], additionalSpeedTiers: []) == false)
        precondition(ModelOption.fastModeSupport(serviceTierIDs: [], additionalSpeedTiers: []) == false)
        precondition(ModelOption.fastModeSupport(serviceTierIDs: nil, additionalSpeedTiers: nil) == nil)
    }

    static func liveCapabilityTakesPrecedenceOverTheCatalog() {
        let catalog = [model(supported: true)]
        let omitted = ModelOption.enrichFastSupport([model(supported: nil)], from: catalog)
        precondition(omitted.first?.supportsFastMode == true)
        let explicitNo = ModelOption.enrichFastSupport([model(supported: false)], from: catalog)
        precondition(explicitNo.first?.supportsFastMode == false)
        let other = ModelOption(id: "another-model", name: "Other", efforts: [], defaultEffort: nil, isDefault: false)
        precondition(ModelOption.enrichFastSupport([other], from: catalog).first?.supportsFastMode == nil)
    }

    static func recommendsFastOnlyWithProvenHeadroom() {
        let now = sample(hour: 10, minute: 30, used: 25).observedAt
        let data = dashboard(now: now, previous: sample(hour: 10, used: 24))
        precondition(data.budgetWarning == nil)
        precondition(data.fastModeRecommendation == "Fast")
        precondition(data.recommendation == "Use GPT-6.1 Sol · High")
        precondition(data.showsRecommendation)
        let unproven = dashboard(now: now)
        precondition(unproven.fastModeRecommendation == "Standard")
        precondition(unproven.recommendation == "Use GPT-6.1 Sol · High")
    }

    static func recommendsStandardForPressureAndHeavyWork() {
        let now = sample(hour: 10, minute: 30, used: 25).observedAt
        let warning = dashboard(now: now, previous: sample(hour: 10, used: 10))
        precondition(warning.budgetWarning != nil)
        precondition(warning.fastModeRecommendation == "Standard")
        let demanding = dashboard(now: now, previous: sample(hour: 10, used: 24), turns: 6, tokens: 600_000)
        precondition(demanding.fastModeRecommendation == "Standard")
    }

    static func omitsAdviceWhenCapabilityOrReadingsAreMissing() {
        let now = sample(hour: 10, minute: 30, used: 25).observedAt
        let previous = sample(hour: 10, used: 24)
        precondition(dashboard(now: now, previous: previous, supported: false).fastModeRecommendation == nil)
        precondition(dashboard(now: now, previous: previous, supported: nil).fastModeRecommendation == nil)
        let current = sample(hour: 10, minute: 30, used: 25)
        let stale = now.addingTimeInterval(46 * 60)
        precondition(recommendedSpeed(model: model(supported: true), activity: activity(at: stale),
                                      quota: current, pace: nil, now: stale, hasBudgetWarning: false) == nil)
        precondition(recommendedSpeed(model: model(supported: true), activity: nil,
                                      quota: current, pace: nil, now: now, hasBudgetWarning: false) == nil)
    }

    static func followsTheModelActuallySuggested() {
        let now = sample(hour: 10, minute: 30, used: 25).observedAt
        let data = dashboard(now: now, previous: sample(hour: 10, used: 10), turns: 2, tokens: 50_000, effort: "low")
        precondition(data.budgetWarning != nil)
        precondition(data.recommendation.contains("Luna"))
        precondition(data.fastModeRecommendation == nil)
    }

    static func dashboard(
        now: Date, previous: QuotaSample? = nil, supported: Bool? = true,
        turns: Int = 3, tokens: Int = 200_000, effort: String = "high"
    ) -> DashboardData {
        let quota = sample(hour: 10, minute: 30, used: 25)
        let models = [
            model(supported: supported),
            ModelOption(id: "gpt-6-luna", name: "GPT-6 Luna", efforts: ["low", "medium", "high"],
                        defaultEffort: "medium", isDefault: false, supportsFastMode: false),
        ]
        let history = previous.map { [$0, quota] } ?? [quota]
        let telemetry = Telemetry(quota: quota, shortQuota: nil, history: history,
                                  activity: activity(at: now, turns: turns, tokens: tokens, effort: effort),
                                  models: models, source: "Codex account", error: nil)
        let sessions = (9..<14).map { hour in
            SessionTemplate(id: UUID(), name: "Work \(hour)", startMinute: hour * 60, endMinute: (hour + 1) * 60)
        }
        return dashboardData(telemetry: telemetry, history: history, now: now, isRefreshing: false, isDemo: true,
                             schedule: WorkSchedule(sessions: sessions), workdays: [.monday])
    }

    static func model(supported: Bool?) -> ModelOption {
        ModelOption(id: "gpt-6.1-sol", name: "GPT-6.1 Sol", efforts: ["low", "medium", "high"],
                    defaultEffort: "medium", isDefault: true, supportsFastMode: supported)
    }

    static func activity(at date: Date, turns: Int = 3, tokens: Int = 200_000, effort: String = "high") -> Activity {
        Activity(observedAt: date, model: "gpt-6.1-sol", effort: effort, turns: turns, tokens: tokens)
    }

    static func sample(hour: Int, minute: Int = 0, used: Double) -> QuotaSample {
        let calendar = WorkClock.calendar
        let observed = calendar.date(from: DateComponents(year: 2026, month: 11, day: 16, hour: hour, minute: minute))!
        let reset = calendar.date(from: DateComponents(year: 2026, month: 11, day: 23))!
        return QuotaSample(observedAt: observed, resetsAt: reset, windowMinutes: 10080, usedPercent: used)
    }
}
