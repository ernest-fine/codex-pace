import AppKit
import Foundation

@main
@MainActor
struct RecommendationTests {
    static func main() {
        readsFastCapabilityWithoutGuessingFromModelNames()
        liveCapabilityTakesPrecedenceOverTheCatalog()
        slowsDownWithPacingAdvice()
        reservesQuotaForUrgentWork()
        pacingAdviceIsIndependentOfTheModel()
        pausesWhenTheSessionBudgetIsUsed()
        keepsPaceWhenOnTrack()
        skipsUnavailableOrExhaustedQuota()
        presentsCarryoverCreditAndReturnsToUsage()
        usesACompactReadingAndKeepsTheExactTitle()
        matchesWeeklyBarColorsInBothAppearances()
        print("11 pacing and quota display checks passed")
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

    static func slowsDownWithPacingAdvice() {
        let result = dashboard(used: 34, deadlineHour: 14, previousUsed: 25)
        precondition(result.guidance?.action == .slowDown && result.guidance?.weeklyRisk == false)
        precondition(result.recommendation == "Run fewer turns", result.recommendation)
        precondition(result.showsRecommendation)
        let used = DashboardFormat.percent(70)
        let target = DashboardFormat.percent(50)
        precondition(result.recommendationDetail.contains("\(used) of your session budget used. Target by now is \(target)."))
        precondition(result.budgetWarning?.recommendation == result.recommendation)
    }

    static func reservesQuotaForUrgentWork() {
        let result = dashboard(used: 34, deadlineHour: 14, previousUsed: 20)
        precondition(result.guidance?.action == .slowDown && result.guidance?.weeklyRisk == true)
        precondition(result.recommendation == "Use Codex for urgent work only", result.recommendation)
        precondition(result.budgetWarning?.isWeekly == true)
        precondition(result.recommendationDetail.contains("Target by now is \(DashboardFormat.percent(50))."))
    }

    static func pacingAdviceIsIndependentOfTheModel() {
        let sol = Activity(observedAt: now, model: "gpt-6.2-sol", effort: "low", turns: 1, tokens: 3_000)
        let routine = dashboard(used: 34, deadlineHour: 14, previousUsed: 25, activity: sol)
        let noCatalog = dashboard(used: 34, deadlineHour: 14, previousUsed: 25, available: [])
        precondition(routine.recommendation == noCatalog.recommendation)
        precondition(routine.recentActivity.contains("Sol · Low"))

        let luna = Activity(observedAt: now, model: "gpt-6-luna", effort: "low", turns: 1, tokens: 3_000)
        let urgent = dashboard(used: 34, deadlineHour: 14, previousUsed: 20, activity: luna)
        precondition(urgent.recommendation == "Use Codex for urgent work only", urgent.recommendation)
    }

    static func pausesWhenTheSessionBudgetIsUsed() {
        let manual = dashboard(used: 51.7, deadlineHour: 13, previousUsed: 25)
        precondition(manual.guidance?.action == .pause)
        precondition(manual.recommendation == "Pause non-urgent turns until your next session", manual.recommendation)
        precondition(manual.showsRecommendation)
        precondition(manual.budgetWarning?.isWeekly == false)
    }

    static func keepsPaceWhenOnTrack() {
        let result = dashboard(used: 29, deadlineHour: 14, previousUsed: 25)
        precondition(result.guidance?.action == .keepPace || result.guidance?.action == .roomToSpeedUp)
        precondition(result.recommendation == "Keep this pace", result.recommendation)
        precondition(result.showsRecommendation)
    }

    static func skipsUnavailableOrExhaustedQuota() {
        let stale = dashboard(used: 25, deadlineHour: 13, observedAt: now.addingTimeInterval(-46 * 60))
        precondition(!stale.showsRecommendation)
        let spent = dashboard(used: 100, deadlineHour: 13)
        precondition(spent.showsRecommendation)
        precondition(spent.recommendation.hasPrefix("Weekly quota resets "))
        precondition(!dashboard(used: 25, deadlineHour: 13, workdays: []).showsRecommendation)
    }

    static func presentsCarryoverCreditAndReturnsToUsage() {
        let data = dashboard(used: 15, deadlineHour: 13)
        let used = MenuBarSummary.make(data: data, now: now, focus: .session, display: .used)
        let credit = DashboardFormat.percent(40)
        let netUsed = DashboardFormat.percent(-40)
        let target = DashboardFormat.percent(50)
        precondition(used.label == "+\(credit)", used.title)
        precondition(used.title.contains("\(netUsed) net used"), used.title)

        let left = MenuBarSummary.make(data: data, now: now, focus: .session, display: .left)
        precondition(left.label == compactPair(140, 50))
        precondition(left.title.contains("Target \(target) left by now"))

        let zero = dashboard(used: 25, deadlineHour: 13)
        let zeroUsed = MenuBarSummary.make(data: zero, now: now, focus: .session, display: .used)
        precondition(zeroUsed.label == compactPair(0, 50))
        let active = dashboard(used: 30, deadlineHour: 13)
        let activeUsed = MenuBarSummary.make(data: active, now: now, focus: .session, display: .used)
        precondition(activeUsed.label == compactPair(20, 50))

        let nearZero = MenuBarSummary(period: "Session", progress: QuotaProgress(
            used: -0.00049, target: 0.5, showsRemaining: false, allowsNegativeUsed: true
        ))
        precondition(nearZero.label == zeroUsed.label)
    }

    static func usesACompactReadingAndKeepsTheExactTitle() {
        let summary = MenuBarSummary(period: "Afternoon", progress: QuotaProgress(
            used: 0.237, target: 0.281, showsRemaining: false
        ))
        precondition(summary.label == compactPair(24, 28), summary.label ?? summary.title)
        precondition(summary.title.contains("\(DashboardFormat.percent(23.7)) used"))
        precondition(summary.title.contains("Target \(DashboardFormat.percent(28.1)) used by now"))
        precondition(summary.image(dark: false).size.width == 42)

        let exhausted = MenuBarSummary(period: "Afternoon", progress: QuotaProgress(
            used: 2.7, target: 0.19, showsRemaining: false
        ))
        precondition(exhausted.label == compactPair(100, 19, overBudget: true))
        precondition(exhausted.image(dark: false).size.width == 42)
        precondition(exhausted.title.contains("\(DashboardFormat.percent(270)) used"))

        let left = MenuBarSummary(period: "Afternoon", progress: QuotaProgress(
            used: 0.237, target: 0.281, showsRemaining: true
        ))
        precondition(left.label == compactPair(76, 72))
        precondition(left.title.contains("Target \(DashboardFormat.percent(71.9)) left by now"))

        let withoutTarget = MenuBarSummary(period: "Weekly quota", progress: QuotaProgress(
            used: 0.237, target: nil, showsRemaining: false
        ))
        precondition(withoutTarget.label == DashboardFormat.percent(24))

        let offHours = MenuBarSummary(period: "Off hours", progress: nil)
        precondition(offHours.label == nil)
        precondition(offHours.image(dark: false).size.width < 42)
        precondition(offHours.title == "Off hours")
    }

    private static func compactPair(_ reading: Int, _ target: Int, overBudget: Bool = false) -> String {
        let suffix = overBudget ? "+" : ""
        return "\(reading.formatted())\(suffix)/\(target.formatted())"
    }

    static func matchesWeeklyBarColorsInBothAppearances() {
        let sustainable = dashboard(used: 34, deadlineHour: 14, previousUsed: 25)
        let weekly = MenuBarSummary.make(data: sustainable, now: now, focus: .weekly, display: .used)
        let session = MenuBarSummary.make(data: sustainable, now: now, focus: .session, display: .used)
        precondition(weekly.progress?.level == .overTarget, "The fixture must be above the plotted target")
        precondition(weekly.weeklyAction == .keepPace)
        precondition(imageContains(weekly.image(dark: false), rgb: 0x468464))
        precondition(imageContains(weekly.image(dark: true), rgb: 0xa0d5af))
        precondition(imageContains(session.image(dark: false), rgb: 0xa93846))
        precondition(imageContains(session.image(dark: true), rgb: 0xefa5a9))

        let slightlyOver = MenuBarSummary(period: "Afternoon", progress: QuotaProgress(
            used: 0.38, target: 0.35, showsRemaining: false
        ))
        precondition(imageContains(slightlyOver.image(dark: false), rgb: 0x946217))
        precondition(imageContains(slightlyOver.image(dark: true), rgb: 0xe2ba73))

        let credit = MenuBarSummary(period: "Afternoon", progress: QuotaProgress(
            used: -0.25, target: 0.28, showsRemaining: false, allowsNegativeUsed: true
        ))
        precondition(imageContains(credit.image(dark: false), rgb: 0x4e7ca9))
        precondition(imageContains(credit.image(dark: true), rgb: 0xa3c9ed))

        let stale = dashboard(used: 34, deadlineHour: 14, observedAt: now.addingTimeInterval(-46 * 60))
        let lastReading = MenuBarSummary.make(data: stale, now: now, focus: .weekly, display: .used)
        precondition(lastReading.title.contains("Last reading"))
        precondition(imageContains(lastReading.image(dark: false), rgb: 0x5d645f))
        precondition(imageContains(lastReading.image(dark: true), rgb: 0xbac2bd))
    }

    private static func imageContains(_ image: NSImage, rgb: UInt32) -> Bool {
        guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let sRGB = CGColorSpace(name: CGColorSpace.sRGB) else { return false }
        let width = cgImage.width
        let height = cgImage.height
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        return pixels.withUnsafeMutableBytes { buffer in
            guard let context = CGContext(
                data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                bytesPerRow: width * 4, space: sRGB, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ) else { return false }
            context.draw(cgImage, in: CGRect(x: 0, y: 0, width: width, height: height))
            return stride(from: 0, to: buffer.count, by: 4).contains { offset in
                let pixel = (UInt32(buffer[offset]) << 16) | (UInt32(buffer[offset + 1]) << 8) | UInt32(buffer[offset + 2])
                return buffer[offset + 3] == 255 && pixel == rgb
            }
        }
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
        activity: Activity? = nil, available: [ModelOption]? = nil, workdays: Set<Workday> = Workday.standard
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
        return dashboardData(telemetry: telemetry, history: history, now: now, isRefreshing: false, isDemo: false,
                             schedule: WorkSchedule(sessions: sessions), resetTarget: target, workdays: workdays)
    }

    static func model(supported: Bool?) -> ModelOption {
        ModelOption(id: "gpt-6.2-sol", name: "Sol", efforts: ["low", "medium", "high", "max"],
                    defaultEffort: "medium", isDefault: true, supportsFastMode: supported)
    }

    private static func time(_ hour: Int, _ minute: Int = 0) -> Date {
        WorkClock.calendar.date(from: DateComponents(year: 2026, month: 11, day: 16, hour: hour, minute: minute))!
    }
}
