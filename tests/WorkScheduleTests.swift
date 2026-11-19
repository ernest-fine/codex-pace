import Foundation

@main
@MainActor
struct WorkScheduleTests {
    static func main() throws {
        try migratesSharedSessions()
        try keepsDayEditsIndependent()
        try keepsDayAndWeekDraftsSeparate()
        try restoresAndAppliesSessionTemplateSnapshots()
        usesDayHoursForQuotaTargets()
        carriesOverOverspendWithJustTheCurrentReading()
        carriesOverUnderspendAndStopsAtTheEndTarget()
        usesTheScheduleForTheTargetAndChecksStaleReadings()
        warnsWhenAPaceCanReachTheTargetSoon()
        warnsAtTheLimitAndSkipsStaleReadings()
        sessionChangeUsesTheLatestReadingWithinTheSession()
        sessionChangeSkipsMissingStaleAndResetReadings()
        try retainsManualResetTargetAcrossRefresh()
        print("13 pacing and schedule tests passed")
    }

    static func retainsManualResetTargetAcrossRefresh() throws {
        let now = Date(timeIntervalSince1970: 1_795_002_000)
        let reset = now.addingTimeInterval(2 * 86_400)
        let target = ManualResetTarget(targetAt: now.addingTimeInterval(3600), windowResetsAt: reset)
        let restored = try JSONDecoder().decode(ManualResetTarget.self, from: JSONEncoder().encode(target))

        for shift: TimeInterval in [-1, 0, 1, 5, 6, 7 * 86_400] {
            let reading = QuotaSample(observedAt: now, resetsAt: reset.addingTimeInterval(shift),
                                      windowMinutes: 10080, usedPercent: 28)
            let expected = abs(shift) <= 5 ? target.targetAt : reading.resetsAt
            let pace = calculatePace(quota: reading, history: [], now: now, resetTarget: restored)
            precondition(pace.planEndsAt == expected, "Unexpected target after a \(shift)s reset timestamp shift")
        }
    }

    static func migratesSharedSessions() throws {
        let legacy = Data("""
            {"morningStart":540,"morningEnd":720,"afternoonStart":780,"afternoonEnd":1020}
            """.utf8)
        let schedule = try JSONDecoder().decode(WorkSchedule.self, from: legacy)
        precondition(schedule.sessions(on: .monday).map(\.startMinute) == [540, 780])
        precondition(schedule.sessions(on: .tuesday) == schedule.sessions(on: .monday))

        let templates = WorkSchedule.standard.sessions(on: .monday)
        let encodedTemplates = try JSONEncoder().encode(templates)
        let shared = try JSONSerialization.data(withJSONObject: [
            "sessions": JSONSerialization.jsonObject(with: encodedTemplates),
        ])
        let restored = try JSONDecoder().decode(WorkSchedule.self, from: shared)
        precondition(restored.sessions(on: .friday) == WorkSchedule.standard.sessions(on: .friday))
    }

    static func keepsDayEditsIndependent() throws {
        var schedule = WorkSchedule.standard
        let inherited = schedule.sessions(on: .tuesday)
        schedule.setSessions([session("Late start", 660, 720)], on: .monday)
        schedule.setSessions([], on: .friday)
        let restored = try JSONDecoder().decode(WorkSchedule.self, from: JSONEncoder().encode(schedule))
        precondition(restored.sessions(on: .monday).map(\.name) == ["Late start"])
        precondition(restored.sessions(on: .tuesday) == inherited)
        precondition(restored.sessions(on: .friday).isEmpty)
        precondition(restored.isValid)

        schedule.setSessions([session("First", 540, 660), session("Overlap", 600, 720)], on: .sunday)
        precondition(schedule.invalidDays == [.sunday])
        precondition(!schedule.isValid)
    }

    static func keepsDayAndWeekDraftsSeparate() throws {
        var schedule = WorkSchedule.standard
        let chosen = [session("Late shift", 660, 720)]
        schedule.setSessions(chosen, on: .tuesday)
        let draft = ScheduleDraft()
        draft.begin(schedule, workdays: [.tuesday, .friday])
        draft.day = .tuesday
        draft.setSessionField(\.endMinute, to: 780, id: chosen[0].id, in: .day(.tuesday))
        draft.selectScope(.entireWeek)
        precondition(draft.sessions.first?.endMinute == 780)
        draft.setSessionField(\.name, to: "Weekly shift", id: chosen[0].id, in: .week)
        draft.selectScope(.editedDays)
        precondition(draft.sessions.first?.name == "Late shift")
        precondition(draft.scheduleToSave.sessions(on: .friday) == schedule.sessions(on: .friday))
        draft.day = .friday
        draft.selectScope(.entireWeek)
        let restored = try JSONDecoder().decode(WorkSchedule.self, from: JSONEncoder().encode(draft.scheduleToSave))
        precondition(Workday.allCases.allSatisfy { restored.sessions(on: $0).map(\.name) == ["Weekly shift"] })

        draft.removeSession(chosen[0].id, from: .week)
        let cleared = draft.scheduleToSave
        precondition(cleared.isValid)
        precondition(Workday.allCases.allSatisfy { cleared.sessions(on: $0).isEmpty })
        draft.begin(schedule, workdays: [.tuesday])
        precondition(draft.applyScope == .editedDays)
        precondition(draft.sessions == chosen)
    }

    static func usesDayHoursForQuotaTargets() {
        let calendar = WorkClock.calendar
        let now = calendar.date(from: DateComponents(year: 2026, month: 11, day: 16, hour: 10))!
        let reset = calendar.date(from: DateComponents(year: 2026, month: 11, day: 23))!
        let quota = QuotaSample(observedAt: now, resetsAt: reset, windowMinutes: 10080, usedPercent: 10)
        var schedule = WorkSchedule.standard
        schedule.setSessions([session("Monday work", 540, 660)], on: .monday)
        schedule.setSessions([session("Tuesday work", 600, 780)], on: .tuesday)
        schedule.setSessions([], on: .wednesday)
        schedule.setSessions([session("Day off", 540, 720)], on: .thursday)

        let pace = calculatePace(quota: quota, history: [], now: now, schedule: schedule,
                                 workdays: [.monday, .tuesday, .wednesday])
        precondition(pace.sessions.map(\.template.name) == ["Monday work", "Tuesday work"])
        precondition(pace.sessions.map(\.cumulativeTargetPercent) == [40, 100])
        precondition(pace.currentSession?.template.name == "Monday work")
        precondition(pace.targetPercent == 20)
        precondition(pace.remainingWorkHours == 4)
    }

    static func restoresAndAppliesSessionTemplateSnapshots() throws {
        let template = SavedSessionTemplate(name: "  Late shift  ", sessions: [session("Focus", 660, 780)])!
        let restored = try JSONDecoder().decode(SavedSessionTemplate.self, from: JSONEncoder().encode(template))
        precondition(restored == template && restored.name == "Late shift")
        precondition(SavedSessionTemplate(name: " ", sessions: template.sessions) == nil)
        precondition(SavedSessionTemplate(name: "Empty", sessions: []) == nil)
        precondition(SavedSessionTemplate(
            name: "Overlap", sessions: template.sessions + [session("Overlap", 700, 800)]
        ) == nil)

        let draft = ScheduleDraft()
        let original = WorkSchedule.standard
        draft.begin(original, workdays: [.tuesday])
        draft.applyTemplate(restored)
        precondition(draft.scheduleToSave.sessions(on: .tuesday) == restored.sessions)
        precondition(draft.scheduleToSave.sessions(on: .monday) == original.sessions(on: .monday))

        draft.setSessionField(\.name, to: "Edited", id: restored.sessions[0].id, in: draft.sessionList)
        draft.selectScope(.entireWeek)
        draft.applyTemplate(restored)
        precondition(Workday.allCases.allSatisfy { draft.scheduleToSave.sessions(on: $0) == restored.sessions })
        draft.selectScope(.editedDays)
        precondition(draft.sessions.map(\.name) == ["Edited"])
        precondition(restored.sessions.map(\.name) == ["Focus"])
        draft.begin(original, workdays: [.tuesday])
        precondition(draft.sessions == original.sessions(on: .tuesday))
    }

    static func session(_ name: String, _ start: Int, _ end: Int) -> SessionTemplate {
        SessionTemplate(id: UUID(), name: name, startMinute: start, endMinute: end)
    }

    static func carriesOverOverspendWithJustTheCurrentReading() {
        let quota = sample(hour: 10, used: 25)
        let pace = carryoverPace(quota)
        precondition(pace.currentSession?.targetPercent == 20)
        precondition(pace.sessionRoomToTarget == 15)
        precondition(pace.sessionUsedFraction == 0.25)
        precondition(pace.sessionTargetFraction == 0)
        precondition(pace.sessionStatus == .overPace)

        let withHistory = carryoverPace(quota, history: [sample(hour: 9, minute: 58, used: 24)])
        precondition(withHistory.sessionUsedFraction == pace.sessionUsedFraction)
        precondition(withHistory.sessionTargetFraction == pace.sessionTargetFraction)
    }

    static func carriesOverUnderspendAndStopsAtTheEndTarget() {
        let under = carryoverPace(sample(hour: 10, used: 15))
        precondition(under.sessionRoomToTarget == 25)
        precondition(under.sessionUsedFraction == -0.25)

        let quota = sample(hour: 10, minute: 30, used: 45)
        let over = carryoverPace(quota)
        precondition(over.sessionRoomToTarget == -5)
        precondition(over.sessionUsedFraction == 1.25)
        precondition(sessionGuidance(for: over, quota: quota, now: quota.observedAt)?.action == .pause)
    }

    static func usesTheScheduleForTheTargetAndChecksStaleReadings() {
        let now = sample(hour: 10, minute: 30, used: 30).observedAt
        let fresh = sample(hour: 10, minute: 30, used: 30)
        let freshPace = carryoverPace(fresh, now: now)
        precondition(freshPace.sessionUsedFraction == 0.5)
        precondition(freshPace.sessionTargetFraction == 0.5)
        precondition(sessionGuidance(for: freshPace, quota: fresh, now: now)?.action == .keepPace)

        let stale = sample(hour: 9, minute: 30, used: 30)
        let stalePace = carryoverPace(stale, now: now)
        precondition(stalePace.sessionUsedFraction == freshPace.sessionUsedFraction)
        precondition(sessionGuidance(for: stalePace, quota: stale, now: now)?.action == .checkUsage)
    }

    static func warnsWhenAPaceCanReachTheTargetSoon() {
        let quota = sample(hour: 10, minute: 30, used: 37)
        let history = [sample(hour: 10, used: 34)]
        let pace = carryoverPace(quota, history: history)
        let warning = budgetNotification(for: pace, quota: quota, now: quota.observedAt)
        precondition(warning?.identifier.hasPrefix("session.") == true)
        precondition(warning?.detail.contains("30 min") == true)

        let noTrend = carryoverPace(quota)
        precondition(budgetNotification(for: noTrend, quota: quota, now: quota.observedAt) == nil)

        let slower = carryoverPace(quota, history: [sample(hour: 10, used: 36)])
        precondition(budgetNotification(for: slower, quota: quota, now: quota.observedAt) == nil)

        let late = sample(hour: 10, minute: 50, used: 38)
        let nearingEnd = carryoverPace(late, history: [sample(hour: 10, minute: 20, used: 35)])
        precondition(budgetNotification(for: nearingEnd, quota: late, now: late.observedAt) == nil)
    }

    static func warnsAtTheLimitAndSkipsStaleReadings() {
        let reached = sample(hour: 10, minute: 30, used: 40)
        let pace = carryoverPace(reached)
        precondition(budgetNotification(for: pace, quota: reached, now: reached.observedAt)?.title == "Codex session target reached")
        let stale = reached.observedAt.addingTimeInterval(301)
        precondition(budgetNotification(for: pace, quota: reached, now: stale) == nil)

        let nearWeekly = sample(hour: 13, minute: 30, used: 95)
        let nearWeeklyPace = carryoverPace(nearWeekly, history: [sample(hour: 13, used: 85)])
        let warning = budgetNotification(for: nearWeeklyPace, quota: nearWeekly, now: nearWeekly.observedAt)
        precondition(warning?.identifier.hasPrefix("weekly.") == true)
        precondition(warning?.detail.contains("15 min") == true)
    }

    static func carryoverPace(_ quota: QuotaSample, history: [QuotaSample] = [], now: Date? = nil) -> Pace {
        let sessions = (9..<14).map { hour in
            session("Session \(hour - 8)", hour * 60, (hour + 1) * 60)
        }
        return calculatePace(quota: quota, history: history, now: now ?? quota.observedAt,
                             schedule: WorkSchedule(sessions: sessions), workdays: [.monday])
    }

    static func sessionChangeUsesTheLatestReadingWithinTheSession() {
        let opening = sample(hour: 10, used: 25)
        let previous = sample(hour: 10, minute: 10, used: 26)
        let latest = sample(hour: 10, minute: 30, used: 28)
        let pace = carryoverPace(latest, history: [opening, previous, latest])
        precondition(pace.sessionUsageChange == 10)

        let exactStart = carryoverPace(latest, history: [opening])
        precondition(exactStart.sessionUsageChange == 15)
        let unchanged = sample(hour: 10, minute: 35, used: 28)
        precondition(carryoverPace(unchanged, history: [latest]).sessionUsageChange == 0)
    }

    static func sessionChangeSkipsMissingStaleAndResetReadings() {
        let latest = sample(hour: 10, minute: 30, used: 28)
        precondition(carryoverPace(latest).sessionUsageChange == nil)
        precondition(carryoverPace(latest, history: [latest]).sessionUsageChange == nil)
        precondition(carryoverPace(latest, history: [sample(hour: 9, minute: 59, used: 25)]).sessionUsageChange == nil)

        let oldWindow = QuotaSample(observedAt: sample(hour: 10, used: 25).observedAt,
                                    resetsAt: latest.resetsAt.addingTimeInterval(-7 * 24 * 3600),
                                    windowMinutes: latest.windowMinutes, usedPercent: 25)
        precondition(carryoverPace(latest, history: [oldWindow]).sessionUsageChange == nil)
        precondition(carryoverPace(latest, history: [sample(hour: 10, used: 35)]).sessionUsageChange == nil)

        let staleNow = sample(hour: 10, minute: 55, used: 28).observedAt
        let staleQuota = sample(hour: 10, minute: 5, used: 28)
        precondition(carryoverPace(staleQuota, history: [sample(hour: 10, used: 25)], now: staleNow).sessionUsageChange == nil)
        let freshQuota = sample(hour: 10, minute: 55, used: 28)
        precondition(carryoverPace(freshQuota, history: [sample(hour: 10, used: 25)]).sessionUsageChange == nil)
    }

    static func sample(hour: Int, minute: Int = 0, used: Double) -> QuotaSample {
        let calendar = WorkClock.calendar
        let observed = calendar.date(from: DateComponents(year: 2026, month: 11, day: 16, hour: hour, minute: minute))!
        let reset = calendar.date(from: DateComponents(year: 2026, month: 11, day: 23))!
        return QuotaSample(observedAt: observed, resetsAt: reset, windowMinutes: 10080, usedPercent: used)
    }
}
