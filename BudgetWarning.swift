import Foundation

struct BudgetWarning: Equatable, Sendable {
    enum Scope {
        case weekly
        case session(WorkSession)
    }

    let identifier: String
    let expiresAt: Date
    let title: String
    let recommendation: String
    let detail: String

    var isWeekly: Bool { identifier.hasPrefix("weekly.") }

    init(scope: Scope, reset: Date, recommendation: String, detail: String, deadline: Date? = nil,
         title: String? = nil) {
        let resetTime = reset.timeIntervalSince1970
        switch scope {
        case .weekly:
            identifier = "weekly.\(resetTime)"
            expiresAt = min(reset, deadline ?? reset)
            self.title = title ?? "Weekly Codex budget at risk"
        case .session(let session):
            let day = WorkClock.calendar.startOfDay(for: session.start).timeIntervalSince1970
            let shift = switch session.template.id {
            case SessionTemplate.morningID: "morning"
            case SessionTemplate.afternoonID: "afternoon"
            default: session.template.id.uuidString
            }
            identifier = "session.\(resetTime).\(day).\(shift)"
            expiresAt = min(reset, session.end)
            self.title = title ?? "Codex session budget at risk"
        }
        self.recommendation = recommendation
        self.detail = detail
    }
}

func budgetNotification(for pace: Pace, quota: QuotaSample, now: Date) -> BudgetWarning? {
    guard quota.resetsAt > now, (0...300).contains(now.timeIntervalSince(quota.observedAt)),
          (0...100).contains(quota.usedPercent) else { return nil }

    let cutoff = now.addingTimeInterval(30 * 60)
    let weekly = pace.exhaustionDate.flatMap { $0 <= cutoff ? $0 : nil }
    let sessionTarget = pace.currentSession.flatMap { session -> (WorkSession, Date)? in
        guard now >= session.start, now < session.end, let room = pace.sessionRoomToTarget else { return nil }
        if room <= 0 { return (session, now) }
        guard let rate = pace.recentRate, rate > 0 else { return nil }
        let timeToTarget = room * 3600 / rate
        guard timeToTarget <= 30 * 60, timeToTarget <= session.end.timeIntervalSince(now) else { return nil }
        return (session, now.addingTimeInterval(timeToTarget))
    }

    if let weekly, weekly <= (sessionTarget?.1 ?? .distantFuture) {
        return imminentWarning(scope: .weekly, name: "weekly limit", date: weekly,
                               reset: quota.resetsAt, deadline: pace.planEndsAt, now: now)
    }
    if let (session, date) = sessionTarget {
        return imminentWarning(scope: .session(session), name: "session target", date: date,
                               reset: quota.resetsAt, deadline: session.end, now: now)
    }
    return nil
}

private func imminentWarning(scope: BudgetWarning.Scope, name: String, date: Date,
                             reset: Date, deadline: Date, now: Date) -> BudgetWarning {
    let remaining = date.timeIntervalSince(now)
    let reached = remaining <= 0
    let detail = reached
        ? "You’ve reached your \(name)."
        : "At this pace, you could reach your \(name) in about \(Int(ceil(remaining / 60))) min."
    return BudgetWarning(scope: scope, reset: reset,
                         recommendation: reached ? "Pause non-urgent turns." : "Use a lower effort or pause non-urgent turns.",
                         detail: detail, deadline: deadline,
                         title: reached ? "Codex \(name) reached" : "Slow down on Codex")
}
