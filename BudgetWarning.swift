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

    init(scope: Scope, reset: Date, recommendation: String, detail: String, deadline: Date? = nil) {
        let resetTime = reset.timeIntervalSince1970
        switch scope {
        case .weekly:
            identifier = "weekly.\(resetTime)"
            expiresAt = min(reset, deadline ?? reset)
            title = "Weekly Codex budget at risk"
        case .session(let session):
            let day = WorkClock.calendar.startOfDay(for: session.start).timeIntervalSince1970
            let shift = switch session.template.id {
            case SessionTemplate.morningID: "morning"
            case SessionTemplate.afternoonID: "afternoon"
            default: session.template.id.uuidString
            }
            identifier = "session.\(resetTime).\(day).\(shift)"
            expiresAt = min(reset, session.end)
            title = "Codex session budget at risk"
        }
        self.recommendation = recommendation
        self.detail = detail
    }
}
