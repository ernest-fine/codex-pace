import Foundation

func effortName(_ effort: String, model: String?) -> String {
    if effort == "low", model?.hasSuffix("-astra") == true { return "Light" }
    if effort == "xhigh" { return "Extra high" }
    return effort.capitalized
}

private func modelName(_ id: String, in models: [ModelOption]) -> String {
    models.first { $0.id == id }?.name ?? id
}

private func sessionUsageDetail(_ pace: Pace) -> String? {
    guard let used = pace.sessionUsedFraction, let target = pace.sessionTargetFraction else { return nil }
    return "\(DashboardFormat.percent(used * 100)) of your session budget used. " +
        "Target by now is \(DashboardFormat.percent(target * 100))."
}

func dashboardData(
    telemetry: Telemetry?,
    history: [QuotaSample],
    now: Date,
    isRefreshing: Bool,
    isDemo: Bool,
    schedule: WorkSchedule = .standard,
    resetTarget: ManualResetTarget? = nil,
    workdays: Set<Workday> = Workday.standard
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
    let isStale = quotaAge.map { !(0...45 * 60).contains($0) } ?? false
    let guidance = pace.flatMap { sessionGuidance(for: $0, quota: quota, now: now) }
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
            quota == nil ? "Waiting for Codex usage" : "Refresh to check your pace",
            quota == nil
                ? "Once Codex reports your weekly allowance, your schedule and pace will appear here."
                : "Open or continue a Codex task as usual if a live account reading is unavailable.",
            .neutral
        )
    } else if let quota, quota.usedPercent >= 100 {
        (status, recommendation, detail, tone) = (
            "Weekly limit reached",
            "Weekly quota resets \(DashboardFormat.dateTime(quota.resetsAt))",
            "The latest account reading shows the weekly allowance at 100%.",
            .critical
        )
    } else if let quota, let session = pace?.currentSession, guidance?.action == .pause {
        (status, recommendation, detail, tone) = (
            "Session budget used",
            "Pause non-urgent turns until your next session",
            "You’ve reached your session target.",
            .critical
        )
        warning = BudgetWarning(scope: .session(session), reset: quota.resetsAt, recommendation: recommendation, detail: detail)
    } else if let quota, let pace, (pace.projectedPercent ?? 0) >= 100 {
        let withinSession = pace.exhaustionDate.map { date in
            pace.currentSession.map { date <= $0.end } ?? false
        } ?? false
        let deadline = hasManualDeadline ? "your reset target" : "reset"
        let forecast = "At this pace, your weekly quota could run out before \(deadline)."
        (status, recommendation, detail, tone) = (
            "Slow down",
            "Use Codex for urgent work only",
            sessionUsageDetail(pace) ?? forecast,
            withinSession || quota.usedPercent >= 90 ? .critical : .caution
        )
        warning = BudgetWarning(
            scope: .weekly, reset: quota.resetsAt, recommendation: recommendation,
            detail: detail, deadline: pace.planEndsAt
        )
    } else if let quota, let pace, let session = pace.currentSession, guidance?.action == .slowDown {
        let forecast = (pace.sessionProjectedRoomToTarget ?? 0) < -0.75
            ? "At this pace, you could reach your session target before the session ends." : nil
        (status, recommendation, detail, tone) = (
            "Slow down",
            "Run fewer turns",
            [sessionUsageDetail(pace), forecast].compactMap { $0 }.joined(separator: " "),
            .caution
        )
        warning = BudgetWarning(scope: .session(session), reset: quota.resetsAt, recommendation: recommendation, detail: detail)
    } else if let pace, pace.remainingWorkHours == 0 {
        (status, recommendation, detail, tone) = (
            "Workweek complete",
            hasManualDeadline ? "No more work scheduled before your target" : "No scheduled work before reset",
            "Any extra Codex activity will use the allowance shown above.",
            .neutral
        )
    } else if let pace, pace.recentRate != nil || guidance?.action == .keepPace || guidance?.action == .roomToSpeedUp {
        let ahead = (quota?.usedPercent ?? 0) > pace.targetPercent + 2
        (status, recommendation, detail, tone) = (
            ahead ? "Pace is sustainable" : "On pace",
            "Keep this pace",
            ahead
                ? "Usage is above the schedule target, and your recent pace fits the remaining allowance."
                : "Usage fits your current pacing target.",
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

    return DashboardData(
        quota: quota, pace: pace, usesManualResetTarget: hasManualDeadline,
        guidance: guidance,
        shortQuota: shortQuota, status: status,
        recommendation: recommendation,
        recommendationDetail: detail, recommendationTone: tone,
        recentActivity: activityText,
        source: telemetry?.source ?? "Connecting to Codex",
        isRefreshing: isRefreshing, isDemo: isDemo, error: telemetry?.error, budgetWarning: warning
    )
}
