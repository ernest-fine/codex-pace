import Foundation

@main
@MainActor
struct FastModeTests {
    static func main() {
        readsFastCapabilityWithoutGuessingFromModelNames()
        liveCapabilityTakesPrecedenceOverTheCatalog()
        usesQuotaThroughTheResetTarget()
        ignoresRecentModelEffortAndWorkload()
        requiresBothKindsOfHeadroomForFast()
        comparesTheCostOfEffortAndSpeed()
        keepsFastWhenThereIsNoLowerEffortToCompare()
        respectsAvailableModelSettings()
        skipsUnavailableOrExhaustedQuota()
        print("9 model and speed recommendation checks passed")
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

    static func usesQuotaThroughTheResetTarget() {
        let manual = dashboard(used: 51.7, deadlineHour: 13, previousUsed: 51.2)
        precondition(abs((manual.pace?.sessionUsedFraction ?? 0) - 1.068) < 0.0001)
        precondition(manual.budgetWarning?.isWeekly == false)
        precondition(manual.recommendation == "Use Astra · Ultra · Standard", manual.recommendation)
        precondition(manual.hasModelRecommendation && manual.showsRecommendation)

        let scheduled = dashboard(used: 51.7, previousUsed: 51.2)
        precondition(scheduled.recommendation == "Use Sol · High · Standard", scheduled.recommendation)
    }

    static func ignoresRecentModelEffortAndWorkload() {
        let heavy = Activity(observedAt: now, model: "gpt-6.2-sol", effort: "max", turns: 8, tokens: 800_000)
        let light = Activity(observedAt: now, model: "gpt-6-luna", effort: "low", turns: 1, tokens: 3_000)
        let expected = "Use Astra · Ultra · Standard"
        precondition(dashboard(used: 51.7, deadlineHour: 13, activity: heavy).recommendation == expected)
        precondition(dashboard(used: 51.7, deadlineHour: 13, activity: light).recommendation == expected)
        precondition(dashboard(used: 51.7, deadlineHour: 13).recommendation == expected)
    }

    static func requiresBothKindsOfHeadroomForFast() {
        precondition(dashboard(used: 70, deadlineHour: 13, fastCostRatio: "0.8").recommendation == "Use Astra · Max · Fast")
        precondition(dashboard(used: 71, deadlineHour: 12, fastCostRatio: "0.8").recommendation == "Use Astra · Ultra · Standard")
        precondition(dashboard(used: 60, deadlineHour: 14, fastCostRatio: "0.8").recommendation == "Use Astra · Ultra · Standard")
        precondition(dashboard(used: 75, deadlineHour: 14).recommendation == "Use Astra · Max · Standard")
        precondition(dashboard(used: 98, deadlineHour: 13).recommendation == "Use Luna · Low · Standard")
    }

    static func comparesTheCostOfEffortAndSpeed() {
        let higherEffort = "Use Astra · Ultra · Standard"
        for ratio in ["1", "1.2", "", "NaN", "0", "-0.8", "0,8"] {
            let result = dashboard(used: 70, deadlineHour: 13, fastCostRatio: ratio)
            precondition(result.recommendation == higherEffort, result.recommendation)
        }

        let limited = ModelOption(id: "gpt-6.2-astra", name: "Astra", efforts: ["ultra", "low", "high"],
                                  defaultEffort: "low", isDefault: true, supportsFastMode: true)
        let cheaperFast = dashboard(used: 70, deadlineHour: 13, available: [limited], fastCostRatio: " 0.8 ")
        precondition(cheaperFast.recommendation == "Use Astra · High · Fast", cheaperFast.recommendation)
    }

    static func keepsFastWhenThereIsNoLowerEffortToCompare() {
        let onlyUltra = ModelOption(id: "gpt-6.2-astra", name: "Astra", efforts: ["ultra"],
                                    defaultEffort: "ultra", isDefault: true, supportsFastMode: true)
        let result = dashboard(used: 70, deadlineHour: 13, available: [onlyUltra], fastCostRatio: "1.2")
        precondition(result.recommendation == "Use Astra · Ultra · Fast", result.recommendation)
    }

    static func respectsAvailableModelSettings() {
        let supported = models[0]
        let limited = ModelOption(id: supported.id, name: supported.name, efforts: ["low", "max"],
                                  defaultEffort: "low", isDefault: false, supportsFastMode: false)
        let result = dashboard(used: 50, deadlineHour: 13, available: [limited])
        precondition(result.recommendation == "Use Astra · Max")
        precondition(result.hasModelRecommendation)
    }

    static func skipsUnavailableOrExhaustedQuota() {
        let stale = dashboard(used: 25, deadlineHour: 13, observedAt: now.addingTimeInterval(-46 * 60))
        precondition(!stale.hasModelRecommendation)
        precondition(!dashboard(used: 100, deadlineHour: 13).hasModelRecommendation)
        precondition(!dashboard(used: 25, deadlineHour: 13, available: []).hasModelRecommendation)
        precondition(!dashboard(used: 25, deadlineHour: 13, workdays: []).hasModelRecommendation)
    }

    private static var now: Date { time(10, 30) }

    private static var models: [ModelOption] {
        [
            ModelOption(id: "gpt-6.2-astra", name: "Astra", efforts: ["low", "medium", "high", "max", "ultra"],
                        defaultEffort: "low", isDefault: false, supportsFastMode: true),
            model(supported: true),
            ModelOption(id: "gpt-6-luna", name: "Luna", efforts: ["low", "medium", "high"],
                        defaultEffort: "medium", isDefault: false, supportsFastMode: true),
        ]
    }

    private static func dashboard(
        used: Double, deadlineHour: Int? = nil, previousUsed: Double? = nil, observedAt: Date? = nil,
        activity: Activity? = nil, available: [ModelOption]? = nil, workdays: Set<Workday> = Workday.standard,
        fastCostRatio: String = ""
    ) -> DashboardData {
        let reset = WorkClock.calendar.date(byAdding: .day, value: 7, to: time(0))!
        let target = deadlineHour.map { ManualResetTarget(targetAt: time($0), windowResetsAt: reset) }
        let quota = QuotaSample(observedAt: observedAt ?? now, resetsAt: reset, windowMinutes: 10080, usedPercent: used)
        let history = previousUsed.map { used in
            [QuotaSample(observedAt: time(10), resetsAt: reset, windowMinutes: 10080, usedPercent: used), quota]
        } ?? [quota]
        let telemetry = Telemetry(quota: quota, shortQuota: nil, history: history, activity: activity,
                                  models: available ?? models, source: "Codex account", error: nil)
        let sessions = (9..<14).map { hour in
            SessionTemplate(id: UUID(), name: "Work \(hour)", startMinute: hour * 60, endMinute: (hour + 1) * 60)
        }
        return dashboardData(telemetry: telemetry, history: history, now: now, isRefreshing: false, isDemo: true,
                             schedule: WorkSchedule(sessions: sessions), resetTarget: target, workdays: workdays,
                             fastTradeoffCostRatio: fastCostRatio)
    }

    static func model(supported: Bool?) -> ModelOption {
        ModelOption(id: "gpt-6.2-sol", name: "Sol", efforts: ["low", "medium", "high", "max"],
                    defaultEffort: "medium", isDefault: true, supportsFastMode: supported)
    }

    private static func time(_ hour: Int, _ minute: Int = 0) -> Date {
        WorkClock.calendar.date(from: DateComponents(year: 2026, month: 11, day: 16, hour: hour, minute: minute))!
    }
}
