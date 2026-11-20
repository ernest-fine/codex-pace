import Foundation

private let effortOrder = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"]
private let configuredFastTradeoffCostRatio = ProcessInfo.processInfo.environment["CODEX_PACE_FAST_TRADEOFF_COST_RATIO"]

func effortName(_ effort: String, model: String?) -> String {
    if effort == "low", model?.hasSuffix("-astra") == true { return "Light" }
    if effort == "xhigh" { return "Extra high" }
    return effort.capitalized
}

private func modelName(_ id: String, in models: [ModelOption]) -> String {
    models.first { $0.id == id }?.name ?? id
}

private func quotaRecommendation(
    quota: QuotaSample?, pace: Pace?, models: [ModelOption], now: Date, fastTradeoffCostRatio: String?
) -> (setting: String, detail: String)? {
    guard let quota, quota.resetsAt > now, (0..<100).contains(quota.usedPercent),
          (0...45 * 60).contains(now.timeIntervalSince(quota.observedAt)),
          let rate = pace?.safeRate, rate.isFinite, rate > 0
    else { return nil }

    let left = 100 - quota.usedPercent
    let (family, effort): (String, String) = switch (left, rate) {
    case (20..., 8...): ("-astra", "ultra")
    case (10..., 4...): ("-astra", "max")
    case (5..., 2...): ("-sol", "high")
    default: ("-luna", "low")
    }
    guard let model = models.first(where: { $0.id.hasSuffix(family) })
            ?? models.first(where: \.isDefault) ?? models.first else { return nil }

    let allowed = effortOrder.prefix(through: effortOrder.firstIndex(of: effort) ?? 0)
    let standardEffort = allowed.last(where: model.efforts.contains) ?? model.defaultEffort ?? model.efforts.first
    let supportsFast = model.supportsFastMode == true
    let lowerEffort = standardEffort.flatMap { standard in
        effortOrder.firstIndex(of: standard).flatMap { index in
            effortOrder.prefix(upTo: index).last(where: model.efforts.contains)
        }
    }
    // Compare lower effort at Fast with higher effort at Standard, normalized to 1.
    let configuredRatio = fastTradeoffCostRatio.flatMap { Double($0.trimmingCharacters(in: .whitespacesAndNewlines)) }
    let estimatedFastCost = configuredRatio.flatMap { $0.isFinite && $0 > 0 ? $0 : nil } ?? 1
    let useFast = supportsFast && left >= 30 && rate >= 12 && (lowerEffort == nil || estimatedFastCost < 1)
    let level = useFast ? lowerEffort ?? standardEffort : standardEffort
    let speed = supportsFast ? (useFast ? "Fast" : "Standard") : nil
    let settings = [model.name, level.map { effortName($0, model: model.id) }, speed]
        .compactMap { $0 }.joined(separator: " · ")
    let detail = "Based on \(DashboardFormat.percent(left)) of weekly quota left, or \(DashboardFormat.rate(rate)) before reset."
    return ("Use \(settings)", detail)
}

func dashboardData(
    telemetry: Telemetry?,
    history: [QuotaSample],
    now: Date,
    isRefreshing: Bool,
    isDemo: Bool,
    schedule: WorkSchedule = .standard,
    resetTarget: ManualResetTarget? = nil,
    workdays: Set<Workday> = Workday.standard,
    fastTradeoffCostRatio: String? = configuredFastTradeoffCostRatio
) -> DashboardData {
    let models = telemetry?.models ?? []
    let activity = telemetry?.activity
    let recent = activity.flatMap { item in
        (0...3600).contains(now.timeIntervalSince(item.observedAt)) ? item : nil
    }
    let activityText: String
    if let recent {
        let current = recent.model.map { modelName($0, in: models) } ?? "Model unavailable"
        let effort = recent.effort.map { " · \(effortName($0, model: recent.model))" } ?? ""
        let turns = recent.turns == 1 ? "1 turn" : "\(recent.turns) turns"
        activityText = "\(current)\(effort) · \(turns) in the last hour"
    } else {
        activityText = "No recent local Codex activity"
    }

    let quota = telemetry?.quota
    let pace = quota.flatMap {
        $0.resetsAt > now
            ? calculatePace(
                quota: $0, history: history, now: now, schedule: schedule, resetTarget: resetTarget, workdays: workdays
            )
            : nil
    }
    let hasManualDeadline = quota.flatMap { resetTarget?.activeDeadline(for: $0, now: now) } != nil
    let quotaAge = quota.map { now.timeIntervalSince($0.observedAt) }
    let isStale = (quotaAge ?? 0) > 45 * 60
    let shortQuota = telemetry?.shortQuota.flatMap { $0.resetsAt > now ? $0 : nil }
    let status: String
    let recommendation: String
    let detail: String
    let tone: RecommendationTone
    var warning: BudgetWarning?

    if let quota, quota.resetsAt <= now {
        (status, recommendation, detail, tone) = (
            "Waiting for reset data",
            "Refresh after the reset",
            "The last confirmed weekly reading belongs to the previous window.",
            .neutral
        )
    } else if quota == nil || isStale {
        (status, recommendation, detail, tone) = (
            quota == nil ? "Waiting for quota" : "Quota reading is stale",
            quota == nil ? "Waiting for Codex usage" : "Refresh for a current recommendation",
            quota == nil
                ? "Once Codex reports your weekly allowance, your schedule and pace will appear here."
                : "Open or continue a Codex task as usual if a live account reading is unavailable.",
            .neutral
        )
    } else if let quota, quota.usedPercent >= 100 {
        (status, recommendation, detail, tone) = (
            "Weekly limit reached",
            hasManualDeadline ? "Use your manual reset in Codex" : "Wait for the weekly reset",
            "The latest account reading shows the weekly allowance at 100%.",
            .critical
        )
    } else if let quota, let pace, let projection = pace.projectedPercent,
              projection >= 100, let safeRate = pace.safeRate {
        let withinSession = pace.exhaustionDate.map { date in
            pace.currentSession.map { date <= $0.end } ?? false
        } ?? false
        let rate = DashboardFormat.percent(safeRate)
        let deadline = hasManualDeadline ? "your manual reset target" : "the scheduled reset"
        (status, recommendation, detail, tone) = (
            "Slow down",
            "Use a lower effort or pause non-urgent turns",
            "At this pace you could reach the weekly limit before \(deadline). Aim for \(rate) of weekly quota per work hour or less.",
            withinSession || quota.usedPercent >= 90 ? .critical : .caution
        )
        warning = BudgetWarning(
            scope: .weekly, reset: quota.resetsAt, recommendation: recommendation,
            detail: detail, deadline: pace.planEndsAt
        )
    } else if let quota, let pace, let session = pace.currentSession,
              let room = pace.sessionRoomToTarget, let projectedRoom = pace.sessionProjectedRoomToTarget,
              projectedRoom < -0.75, (pace.recentRate ?? 0) > 0 {
        let remaining = max(0, room)
        let sessionRate = remaining * 3600 / session.end.timeIntervalSince(now)
        let rate = DashboardFormat.percent(sessionRate)
        let action = remaining > 0
            ? "To stay within the cumulative shift-end target, aim for \(rate) of weekly quota per hour."
            : "The cumulative shift-end target has been reached. Pause non-urgent turns until your next session."
        (status, recommendation, detail, tone) = (
            "Session budget at risk",
            "Use a lower effort or pause non-urgent turns",
            action,
            .caution
        )
        warning = BudgetWarning(
            scope: .session(session), reset: quota.resetsAt, recommendation: recommendation, detail: detail
        )
    } else if let pace, pace.remainingWorkHours == 0 {
        (status, recommendation, detail, tone) = (
            "Workweek complete",
            hasManualDeadline ? "No more work scheduled before your target" : "No scheduled work before reset",
            "Any extra Codex activity will use the allowance shown above.",
            .neutral
        )
    } else if let pace, pace.recentRate != nil {
        let ahead = (quota?.usedPercent ?? 0) > pace.targetPercent + 2
        (status, recommendation, detail, tone) = (
            ahead ? "Pace is sustainable" : "On pace",
            "Keep this pace",
            ahead
                ? "Usage is above the schedule target, and your recent pace fits the remaining allowance."
                : "Your recent pace fits the remaining weekly allowance.",
            .healthy
        )
    } else {
        (status, recommendation, detail, tone) = (
            "Learning your pace",
            "Waiting for more usage readings",
            "The schedule target updates throughout the day. Forecasts need at least 30 minutes of observed work time.",
            .neutral
        )
    }

    let modelAdvice = quotaRecommendation(
        quota: quota, pace: pace, models: models, now: now, fastTradeoffCostRatio: fastTradeoffCostRatio
    )

    return DashboardData(
        quota: quota, pace: pace,
        guidance: pace.flatMap { sessionGuidance(for: $0, quota: quota, now: now) },
        shortQuota: shortQuota, status: status,
        recommendation: modelAdvice?.setting ?? recommendation,
        recommendationDetail: modelAdvice?.detail ?? detail, recommendationTone: tone,
        recentActivity: activityText,
        source: telemetry?.source ?? "Connecting to Codex",
        isRefreshing: isRefreshing, isDemo: isDemo, error: telemetry?.error, budgetWarning: warning,
        hasModelRecommendation: modelAdvice != nil
    )
}
