import Foundation

enum WorkClock {
    static var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .autoupdatingCurrent
        return calendar
    }

    static func boundary(on day: Date, at minute: Int, calendar: Calendar) -> Date? {
        guard let date = calendar.date(bySettingHour: minute / 60, minute: minute % 60, second: 0,
                                       of: day, matchingPolicy: .nextTime, repeatedTimePolicy: .first),
              calendar.isDate(date, inSameDayAs: day) else { return nil }
        return date
    }
}

enum Workday: Int, CaseIterable, Codable, Sendable {
    case monday = 2
    case tuesday = 3
    case wednesday = 4
    case thursday = 5
    case friday = 6
    case saturday = 7
    case sunday = 1

    static let standard: Set<Self> = [.monday, .tuesday, .wednesday, .thursday, .friday]

    var title: String {
        switch self {
        case .monday: "Monday"
        case .tuesday: "Tuesday"
        case .wednesday: "Wednesday"
        case .thursday: "Thursday"
        case .friday: "Friday"
        case .saturday: "Saturday"
        case .sunday: "Sunday"
        }
    }
}

struct SessionTemplate: Codable, Equatable, Sendable, Identifiable {
    let id: UUID
    var name: String
    var startMinute: Int
    var endMinute: Int

    static let morningID = UUID(uuidString: "00000000-0000-4000-8000-000000000001")!
    static let afternoonID = UUID(uuidString: "00000000-0000-4000-8000-000000000002")!
}

struct WorkSchedule: Codable, Equatable, Sendable {
    private let defaultSessions: [SessionTemplate]
    private var daySessions: [Workday: [SessionTemplate]] = [:]

    static let standard = Self(sessions: legacySessions(morningStart: 570, morningEnd: 750,
                                                       afternoonStart: 840, afternoonEnd: 1095))

    init(sessions: [SessionTemplate]) {
        defaultSessions = sessions
    }

    private enum CodingKeys: String, CodingKey {
        case sessions, daySessions, morningStart, morningEnd, afternoonStart, afternoonEnd
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        if let sessions = try values.decodeIfPresent([SessionTemplate].self, forKey: .sessions) {
            defaultSessions = sessions
        } else {
            defaultSessions = try Self.legacySessions(
                morningStart: values.decode(Int.self, forKey: .morningStart),
                morningEnd: values.decode(Int.self, forKey: .morningEnd),
                afternoonStart: values.decode(Int.self, forKey: .afternoonStart),
                afternoonEnd: values.decode(Int.self, forKey: .afternoonEnd)
            )
        }
        daySessions = try values.decodeIfPresent([Workday: [SessionTemplate]].self, forKey: .daySessions) ?? [:]
    }

    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(defaultSessions, forKey: .sessions)
        if !daySessions.isEmpty {
            try values.encode(daySessions, forKey: .daySessions)
        }
    }

    func sessions(on day: Workday) -> [SessionTemplate] {
        daySessions[day] ?? defaultSessions
    }

    func orderedSessions(on day: Workday) -> [SessionTemplate] {
        sessions(on: day).sorted { $0.startMinute < $1.startMinute }
    }

    mutating func setSessions(_ sessions: [SessionTemplate], on day: Workday) {
        daySessions[day] = sessions == defaultSessions ? nil : sessions
    }

    var invalidDays: [Workday] {
        Workday.allCases.filter { !Self.areValid(sessions(on: $0)) }
    }

    var isValid: Bool {
        Self.areValid(defaultSessions) && invalidDays.isEmpty
    }

    private static func areValid(_ sessions: [SessionTemplate]) -> Bool {
        let ordered = sessions.sorted { $0.startMinute < $1.startMinute }
        return Set(ordered.map(\.id)).count == ordered.count
            && ordered.allSatisfy {
                !$0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    && $0.startMinute >= 0 && $0.startMinute < $0.endMinute && $0.endMinute < 1440
            }
            && zip(ordered, ordered.dropFirst()).allSatisfy { $0.endMinute <= $1.startMinute }
    }

    private static func legacySessions(
        morningStart: Int, morningEnd: Int, afternoonStart: Int, afternoonEnd: Int
    ) -> [SessionTemplate] {
        [
            SessionTemplate(id: SessionTemplate.morningID, name: "Morning", startMinute: morningStart, endMinute: morningEnd),
            SessionTemplate(id: SessionTemplate.afternoonID, name: "Afternoon", startMinute: afternoonStart, endMinute: afternoonEnd)
        ]
    }
}

struct ManualResetTarget: Codable, Equatable, Sendable {
    let targetAt: Date
    let windowResetsAt: Date

    func activeDeadline(for quota: QuotaSample, now: Date) -> Date? {
        guard windowResetsAt == quota.resetsAt, now < targetAt, targetAt < quota.resetsAt else { return nil }
        return targetAt
    }
}

struct WorkSession: Identifiable {
    let start: Date
    let end: Date
    let targetPercent: Double
    let cumulativeTargetPercent: Double
    let template: SessionTemplate

    var id: Date { start }

    var menuLabel: String {
        if template.id == SessionTemplate.morningID && template.name == "Morning" { return "AM" }
        if template.id == SessionTemplate.afternoonID && template.name == "Afternoon" { return "PM" }
        return String(template.name.prefix(24))
    }
}

enum SessionPaceStatus {
    case onTrack
    case overPace
    case budgetUsed
}

enum PaceAction: Equatable {
    case checkUsage
    case awaitingReadings
    case pause
    case slowDown
    case keepPace
    case roomToSpeedUp
    case offHours

    var title: String {
        switch self {
        case .checkUsage: "Check usage"
        case .awaitingReadings: "Checking"
        case .pause: "Pause"
        case .slowDown: "Slow down"
        case .keepPace: "Keep this pace"
        case .roomToSpeedUp: "Room to speed up"
        case .offHours: "Off hours"
        }
    }

    static func weekly(pace: Pace?, quota: QuotaSample, now: Date) -> Self {
        guard quota.resetsAt > now, (0...45 * minute).contains(now.timeIntervalSince(quota.observedAt)) else {
            return .checkUsage
        }
        if quota.usedPercent >= 100 { return .pause }
        guard let pace, pace.remainingWorkHours > 0 else { return .offHours }
        guard let projected = pace.projectedPercent else { return .awaitingReadings }
        return projected >= 100 ? .slowDown : .keepPace
    }
}

struct SessionGuidance {
    let action: PaceAction
    let safeSessionPercentPerHour: Double?
    let weeklyRisk: Bool
}

struct Pace {
    let planEndsAt: Date
    let sessions: [WorkSession]
    let currentSession: WorkSession?
    let targetPercent: Double
    let remainingWorkHours: Double
    let safeRate: Double?
    let recentRate: Double?
    let projectedPercent: Double?
    let exhaustionDate: Date?
    let sessionRoomToTarget: Double?
    let sessionProjectedRoomToTarget: Double?
    let sessionTargetFraction: Double?
    let sessionUsageChange: Double?

    var sessionUsedFraction: Double? {
        guard let share = currentSession?.targetPercent, share > 0, let sessionRoomToTarget else { return nil }
        return (share - sessionRoomToTarget) / share
    }

    fileprivate var sessionDeviation: Double? {
        guard let share = currentSession?.targetPercent, let sessionUsedFraction, let sessionTargetFraction else { return nil }
        return (sessionUsedFraction - sessionTargetFraction) * share
    }

    var sessionStatus: SessionPaceStatus? {
        guard let sessionRoomToTarget, let sessionDeviation else { return nil }
        if sessionRoomToTarget <= 0 { return .budgetUsed }
        return sessionDeviation > 0.5 ? .overPace : .onTrack
    }
}

private let minute: TimeInterval = 60
private let hour: TimeInterval = 60 * minute

struct ShiftEndComparison {
    let actualUsed: Double
    let roomToTarget: Double
    let isStale: Bool

    var actualLeft: Double { 100 - actualUsed }
}

struct QuotaProgress {
    enum Level {
        case unknown, withinTarget, slightlyOverTarget, overTarget
    }

    let used: Double
    let target: Double?
    let showsRemaining: Bool

    var displayedAmount: Double { oriented(used) }
    var displayedTarget: Double? { target.map(oriented) }
    var barFraction: Double { min(1, displayedAmount) }
    var markerFraction: Double? { displayedTarget.map { min(1, $0) } }

    var level: Level {
        guard let target else { return .unknown }
        if used <= target { return .withinTarget }
        if used <= target * 1.10 { return .slightlyOverTarget }
        return .overTarget
    }

    private func oriented(_ fraction: Double) -> Double {
        max(0, showsRemaining ? 1 - fraction : fraction)
    }
}

func shiftEndComparison(for session: WorkSession, quota: QuotaSample?, now: Date) -> ShiftEndComparison? {
    guard let quota, session.start <= now, now < session.end, now < quota.resetsAt,
          quota.observedAt <= now, (0...100).contains(quota.usedPercent) else { return nil }

    return ShiftEndComparison(
        actualUsed: quota.usedPercent,
        roomToTarget: session.cumulativeTargetPercent - quota.usedPercent,
        isStale: now.timeIntervalSince(quota.observedAt) > 45 * minute
    )
}

func sessionGuidance(for pace: Pace, quota: QuotaSample?, now: Date) -> SessionGuidance? {
    guard let session = pace.currentSession, session.start <= now, now < session.end else { return nil }
    guard let quota, quota.resetsAt > now,
          (0...45 * minute).contains(now.timeIntervalSince(quota.observedAt))
    else {
        return SessionGuidance(action: .checkUsage, safeSessionPercentPerHour: nil, weeklyRisk: false)
    }

    let weeklyExhausted = quota.usedPercent >= 100
    let weeklyRisk = weeklyExhausted || (pace.projectedPercent ?? 0) >= 100
    let sessionStatus = pace.sessionStatus
    let projectedOverBudget = (pace.sessionProjectedRoomToTarget ?? 0) < -0.75

    let action: PaceAction
    if weeklyExhausted || sessionStatus == .budgetUsed {
        action = .pause
    } else if weeklyRisk || projectedOverBudget || sessionStatus == .overPace {
        action = .slowDown
    } else if let deviation = pace.sessionDeviation, -deviation >= max(0.5, session.targetPercent * 0.10) {
        action = .roomToSpeedUp
    } else {
        action = sessionStatus == .onTrack ? .keepPace : .awaitingReadings
    }

    let safeSessionPercentPerHour: Double?
    if action != .pause, session.targetPercent > 0, let room = pace.sessionRoomToTarget, room > 0 {
        let remainingHours = session.end.timeIntervalSince(now) / hour
        let sessionRate = room / remainingHours
        safeSessionPercentPerHour = min(sessionRate, pace.safeRate ?? sessionRate) * 100 / session.targetPercent
    } else {
        safeSessionPercentPerHour = nil
    }

    return SessionGuidance(action: action, safeSessionPercentPerHour: safeSessionPercentPerHour, weeklyRisk: weeklyRisk)
}

func calculatePace(
    quota: QuotaSample, history: [QuotaSample], now: Date,
    schedule: WorkSchedule = .standard, resetTarget: ManualResetTarget? = nil,
    workdays: Set<Workday> = Workday.standard
) -> Pace {
    let windowStart = quota.resetsAt.addingTimeInterval(-Double(quota.windowMinutes) * minute)
    let planEndsAt = resetTarget?.activeDeadline(for: quota, now: now) ?? quota.resetsAt
    let activeSchedule = schedule.isValid ? schedule : .standard
    let sessions = makeWorkSessions(
        from: windowStart, until: planEndsAt, schedule: activeSchedule, workdays: workdays
    )
    let elapsed = workSeconds(sessions, from: windowStart, until: now)
    let remaining = workSeconds(sessions, from: now, until: planEndsAt)
    let total = elapsed + remaining
    let target = total > 0 ? 100 * elapsed / total : 0
    let used = quota.usedPercent
    let remainingHours = remaining / hour
    let safeRate = remainingHours > 0 ? max(0, 100 - used) / remainingHours : nil
    let samples = currentQuotaSamples(history: history, quota: quota, windowStart: windowStart, now: now)
    let recentRate = recentWorkRate(samples: samples, sessions: sessions, now: now)
    let projected = remainingHours > 0 ? recentRate.map { used + $0 * remainingHours } : nil
    let currentSession = sessions.first { $0.start <= now && now < $0.end }
    let sessionRoomToTarget = currentSession.map { $0.cumulativeTargetPercent - used }
    let projectedAdditionalUsage: Double? = if let currentSession, let recentRate {
        recentRate * currentSession.end.timeIntervalSince(now) / hour
    } else {
        nil
    }
    let sessionProjectedRoomToTarget = sessionRoomToTarget.flatMap { room in
        projectedAdditionalUsage.map { room - $0 }
    }

    let sessionTargetFraction = currentSession.map { session in
        now.timeIntervalSince(session.start) / session.end.timeIntervalSince(session.start)
    }

    return Pace(
        planEndsAt: planEndsAt,
        sessions: sessions,
        currentSession: currentSession,
        targetPercent: target,
        remainingWorkHours: remainingHours,
        safeRate: safeRate,
        recentRate: recentRate,
        projectedPercent: projected,
        exhaustionDate: estimatedExhaustion(sessions: sessions, now: now, used: used, recentRate: recentRate),
        sessionRoomToTarget: sessionRoomToTarget,
        sessionProjectedRoomToTarget: sessionProjectedRoomToTarget,
        sessionTargetFraction: sessionTargetFraction,
        sessionUsageChange: currentSession.flatMap {
            sessionUsageChange(for: $0, quota: quota, samples: samples, now: now)
        }
    )
}

private func sessionUsageChange(for session: WorkSession, quota: QuotaSample, samples: [QuotaSample], now: Date) -> Double? {
    guard session.targetPercent.isFinite, session.targetPercent > 0,
          quota.resetsAt > now, (0...100).contains(quota.usedPercent),
          (0...45 * minute).contains(now.timeIntervalSince(quota.observedAt)),
          let latest = samples.last, latest.observedAt == quota.observedAt,
          let previous = samples.dropLast().last(where: { $0.observedAt >= session.start }),
          (0...45 * minute).contains(now.timeIntervalSince(previous.observedAt))
    else { return nil }
    return (latest.usedPercent - previous.usedPercent) * 100 / session.targetPercent
}

private func makeWorkSessions(
    from start: Date, until end: Date, schedule: WorkSchedule, workdays: Set<Workday>
) -> [WorkSession] {
    guard start < end, !workdays.isEmpty else { return [] }
    let calendar = WorkClock.calendar
    var day = calendar.startOfDay(for: start)
    var intervals: [(interval: DateInterval, template: SessionTemplate)] = []
    while day < end {
        if let weekday = Workday(rawValue: calendar.component(.weekday, from: day)), workdays.contains(weekday) {
            for template in schedule.orderedSessions(on: weekday) {
                guard let opening = WorkClock.boundary(on: day, at: template.startMinute, calendar: calendar),
                      let closing = WorkClock.boundary(on: day, at: template.endMinute, calendar: calendar) else { continue }
                let sessionStart = max(start, opening)
                let sessionEnd = min(end, closing)
                if sessionStart < sessionEnd {
                    intervals.append((DateInterval(start: sessionStart, end: sessionEnd), template))
                }
            }
        }
        guard let nextDay = calendar.date(byAdding: .day, value: 1, to: day) else { break }
        day = calendar.startOfDay(for: nextDay)
    }

    let duration = intervals.reduce(0) { $0 + $1.interval.duration }
    var scheduledThroughEnd: TimeInterval = 0
    return intervals.map { interval, template in
        scheduledThroughEnd += interval.duration
        return WorkSession(
            start: interval.start,
            end: interval.end,
            targetPercent: 100 * interval.duration / duration,
            cumulativeTargetPercent: 100 * scheduledThroughEnd / duration,
            template: template
        )
    }
}

private func workSeconds(_ sessions: [WorkSession], from start: Date, until end: Date) -> TimeInterval {
    sessions.reduce(0) { total, session in
        total + max(0, min(end, session.end).timeIntervalSince(max(start, session.start)))
    }
}

private func currentQuotaSamples(
    history: [QuotaSample], quota: QuotaSample, windowStart: Date, now: Date
) -> [QuotaSample] {
    var samplesByDate: [Date: QuotaSample] = [:]
    for sample in history + [quota] where
        sample.resetsAt == quota.resetsAt
        && sample.windowMinutes == quota.windowMinutes
        && sample.observedAt >= windowStart
        && sample.observedAt <= min(quota.observedAt, now)
        && (0...100).contains(sample.usedPercent)
    {
        samplesByDate[sample.observedAt] = sample
    }
    let samples = samplesByDate.values.sorted { $0.observedAt < $1.observedAt }
    guard let first = samples.first, let latest = samples.last else { return [] }

    // A one-point reversal can be rounding or a difference between reporting sources.
    // Reset the series only when usage moves materially below its peak.
    var startIndex = samples.startIndex
    var peak = first.usedPercent
    for index in samples.indices.dropFirst() {
        let used = samples[index].usedPercent
        if peak - used > 1 {
            startIndex = index
            peak = used
        } else {
            peak = max(peak, used)
        }
    }

    // Avoid counting a rebound as spend, while retaining the latest account value exactly.
    var normalized = 0.0
    return samples[startIndex...].map { sample in
        normalized = max(normalized, min(sample.usedPercent, latest.usedPercent))
        return QuotaSample(
            observedAt: sample.observedAt, resetsAt: sample.resetsAt,
            windowMinutes: sample.windowMinutes, usedPercent: normalized
        )
    }
}

private func recentWorkRate(samples: [QuotaSample], sessions: [WorkSession], now: Date) -> Double? {
    guard let latest = samples.last,
          workSeconds(sessions, from: latest.observedAt, until: now) <= 30 * minute
    else { return nil }

    var secondsObserved: TimeInterval = 0
    var usageObserved = 0.0
    for (earlier, later) in zip(samples, samples.dropFirst()).reversed() {
        let secondsSince = workSeconds(sessions, from: later.observedAt, until: now)
        guard secondsSince < 4 * hour else { break }
        let duration = later.observedAt.timeIntervalSince(earlier.observedAt)
        let scheduled = workSeconds(sessions, from: earlier.observedAt, until: later.observedAt)
        // A quota increase across an off-hours gap cannot be timed to a work session.
        let increase = later.usedPercent - earlier.usedPercent
        guard increase == 0 || duration - scheduled < 1 else { continue }
        secondsObserved += min(scheduled, 4 * hour - secondsSince)
        if scheduled > 0 {
            usageObserved += increase * min(1, (4 * hour - secondsSince) / scheduled)
        }
    }
    guard secondsObserved >= 30 * minute else { return nil }
    return usageObserved * hour / secondsObserved
}

private func estimatedExhaustion(
    sessions: [WorkSession], now: Date, used: Double, recentRate: Double?
) -> Date? {
    if used >= 100 { return now }
    guard let recentRate, recentRate > 0 else { return nil }
    var secondsToExhaustion = (100 - used) * hour / recentRate
    for session in sessions where session.end > now {
        let start = max(now, session.start)
        let duration = session.end.timeIntervalSince(start)
        if secondsToExhaustion <= duration {
            return start.addingTimeInterval(secondsToExhaustion)
        }
        secondsToExhaustion -= duration
    }
    return nil
}
