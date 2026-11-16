import SwiftUI

private enum Graphite {
    static let surface = adaptive(0xeeefed, 0x151716)
    static let panel = adaptive(0xfafbf9, 0x1d201e)
    static let ink = adaptive(0x252826, 0xf3f4f1)
    static let muted = adaptive(0x5d645f, 0xbac2bd)
    static let line = adaptive(0xdce1dc, 0x3b443e)
    static let selection = adaptive(0xe6ebe6, 0x353e37)
    static let hero = adaptive(0xfdfefc, 0x222723)
    static let inset = adaptive(0xedf0eb, 0x2e3630)
    static let track = adaptive(0xdde3dd, 0x3e4841)
    static let onPace = adaptive(0x287653, 0x8ac99e)
    static let caution = adaptive(0x946217, 0xe2ba73)
    static let risk = adaptive(0xa93846, 0xefa5a9)

    private static func adaptive(_ light: UInt32, _ dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let rgb = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: CGFloat((rgb >> 16) & 255) / 255,
                           green: CGFloat((rgb >> 8) & 255) / 255,
                           blue: CGFloat(rgb & 255) / 255, alpha: 1)
        })
    }
}

enum RecommendationTone {
    case neutral, healthy, caution, critical

    var color: Color {
        switch self {
        case .neutral: .secondary
        case .healthy: .green
        case .caution: .orange
        case .critical: .red
        }
    }
}

struct DashboardData {
    var quota: QuotaSample?
    var pace: Pace?
    var guidance: SessionGuidance?
    var shortQuota: QuotaSample?
    var status: String
    var recommendation: String
    var recommendationDetail: String
    var recommendationTone: RecommendationTone
    var recentActivity: String
    var source: String
    var isRefreshing: Bool
    var isDemo: Bool
    var error: String?
    var budgetWarning: BudgetWarning? = nil

    var showsRecommendation: Bool {
        budgetWarning != nil || recommendationTone == .critical
    }
}

struct Dashboard: View {
    let data: DashboardData
    @Binding var schedule: WorkSchedule
    let workdays: Set<Workday>
    let changeWorkdays: (Set<Workday>) -> Void
    let manualResetTarget: Date?
    let changeResetTarget: (Date?, Date) -> Bool
    let refresh: () -> Void
    @Binding var targetDisplay: WeeklyTargetDisplay
    @StateObject private var sessionsDraft = ScheduleDraft()
    @StateObject private var workdaysDraft = WorkdaysDraft()

    var body: some View {
        let now = data.isDemo ? data.quota?.observedAt ?? Date.now : Date.now
        GeometryReader { geometry in
            let width = min(1024, max(0, geometry.size.width - 44))
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    header
                    CurrentSessionPanel(data: data, now: now, width: width, targetDisplay: targetDisplay)
                    if let error = data.error {
                        Text(error)
                            .font(.callout)
                            .foregroundStyle(Graphite.muted)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    quotaAndPlan(width: width, now: now)
                    RecommendationPanel(data: data, showsAdvice: !hasActiveSession(at: now))
                    if !data.source.isEmpty {
                        Text(data.source)
                            .font(.caption)
                            .foregroundStyle(Graphite.muted)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .frame(width: width, alignment: .leading)
                .padding(.vertical, 22)
                .frame(maxWidth: .infinity)
            }
        }
        .background(Graphite.surface)
        .foregroundStyle(Graphite.ink)
        .tint(Graphite.ink)
        .frame(minWidth: 580, minHeight: 480)
        .sheet(isPresented: $sessionsDraft.isPresented) {
            ScheduleEditor(draft: sessionsDraft) { schedule = $0 }
        }
        .sheet(isPresented: $workdaysDraft.isPresented) {
            WorkdaysEditor(draft: workdaysDraft, save: changeWorkdays)
        }
    }

    private var header: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 16) {
                identity.fixedSize(horizontal: true, vertical: false)
                Spacer(minLength: 0)
                quotaView
                actions
            }
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 12) {
                    identity
                    Spacer(minLength: 0)
                    quotaView
                }
                actions
            }
        }
        .padding(.bottom, 4)
    }

    private var identity: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Codex Pace")
                .font(.system(size: 28, weight: .bold))
                .tracking(-0.9)
                .accessibilityAddTraits(.isHeader)
            Text(data.isDemo ? "Demo data" : quotaTimestamp)
                .font(.caption)
                .foregroundStyle(Graphite.muted)
        }
    }

    private var quotaView: some View {
        HStack(spacing: 8) {
            Text("Quota view")
                .font(.caption.weight(.medium))
                .foregroundStyle(Graphite.muted)
            Picker("Quota view", selection: $targetDisplay) {
                Text("Used").tag(WeeklyTargetDisplay.used)
                Text("Left").tag(WeeklyTargetDisplay.left)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .accessibilityLabel("Quota view")
            .help("Changes Current session, Weekly quota and Work sessions")
        }
        .fixedSize(horizontal: true, vertical: false)
    }

    private var actions: some View {
        HStack(spacing: 8) {
            Button("Edit sessions") {
                sessionsDraft.schedule = schedule
                sessionsDraft.isPresented = true
            }
            .help("Add, remove, name, or change the times of your work sessions")

            Button("Work days") {
                workdaysDraft.days = workdays
                workdaysDraft.isPresented = true
            }
            .help("Choose your recurring work days")

            Button(data.isRefreshing ? "Refreshing…" : "Refresh", action: refresh)
                .disabled(data.isRefreshing)
                .keyboardShortcut("r", modifiers: .command)
                .help("Check for the latest Codex usage")
        }
        .controlSize(.regular)
        .fixedSize(horizontal: true, vertical: false)
    }

    @ViewBuilder
    private func quotaAndPlan(width: CGFloat, now: Date) -> some View {
        if let quota = data.quota, let pace = data.pace, width >= 788 {
            HStack(alignment: .top, spacing: 14) {
                weeklyPanel(quota).frame(width: floor((width - 14) * 0.365))
                workPlan(pace, now: now)
            }
        } else {
            if let quota = data.quota {
                weeklyPanel(quota)
            } else if let shortQuota = data.shortQuota {
                Panel {
                    ShortQuotaSummary(quota: shortQuota)
                }
            }
            if let pace = data.pace {
                workPlan(pace, now: now)
            }
        }
    }

    private func weeklyPanel(_ quota: QuotaSample) -> some View {
        WeeklyPanel(quota: quota, pace: data.pace, shortQuota: data.shortQuota,
                    targetDisplay: targetDisplay, manualResetTarget: manualResetTarget,
                    demoNow: data.isDemo ? quota.observedAt : nil, changeResetTarget: changeResetTarget)
    }

    private func workPlan(_ pace: Pace, now: Date) -> some View {
        WorkPlanPanel(pace: pace, schedule: schedule, quota: data.quota, now: now, targetDisplay: targetDisplay)
    }

    private func hasActiveSession(at now: Date) -> Bool {
        guard let session = data.pace?.currentSession else { return false }
        return session.start <= now && now < session.end
    }

    private var quotaTimestamp: String {
        guard let observedAt = data.quota?.observedAt else { return "Weekly usage tracker" }
        return "Updated \(DashboardFormat.dateTime(observedAt))"
    }
}

private struct CurrentSessionPanel: View {
    let data: DashboardData
    let now: Date
    let width: CGFloat
    let targetDisplay: WeeklyTargetDisplay

    private var quota: QuotaSample? { data.quota }

    var body: some View {
        Panel(surface: Graphite.hero, padding: 22, cornerRadius: 16) {
            VStack(alignment: .leading, spacing: 10) {
                if let pace = data.pace, let session = pace.currentSession, session.start <= now, now < session.end {
                    if width >= 680 {
                        HStack(alignment: .center, spacing: 24) {
                            sessionReadings(pace, session: session)
                            sessionAdvice(pace)
                                .frame(width: floor((width - 68) * 0.38))
                        }
                    } else {
                        sessionReadings(pace, session: session)
                        sessionAdvice(pace)
                    }
                } else if let pace = data.pace {
                    SectionTitle(text: "Current session")
                    outsideSession(pace.sessions)
                } else {
                    SectionTitle(text: "Current session")
                    Text("Checking quota")
                        .font(.callout)
                        .foregroundStyle(Graphite.muted)
                }
            }
        }
    }

    private func sessionReadings(_ pace: Pace, session: WorkSession) -> some View {
        let usedFraction = pace.sessionUsedFraction
        let shownPercent = DashboardFormat.sessionPercent(pace, display: targetDisplay)
        let targetFraction = pace.sessionTargetFraction
        let targetPercent = targetFraction.map { DashboardFormat.percent(targetDisplay.percent(forUsed: 100 * $0)) }
        let comparison = shiftEndComparison(for: session, quota: quota, now: now)

        return VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 10) {
                VStack(alignment: .leading, spacing: 3) {
                    SectionTitle(text: DashboardFormat.sessionName(session))
                    Text(DashboardFormat.timeRange(session))
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(Graphite.muted)
                }
                Spacer(minLength: 0)
                Text("\(DashboardFormat.timeRemaining(until: session.end, now: now)) left")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(Graphite.muted)
                    .fixedSize()
            }

            HStack(alignment: .bottom, spacing: 16) {
                if let shownPercent {
                    QuotaReading(label: "Session budget \(targetDisplay.rawValue)", percent: shownPercent, size: 52)
                } else {
                    Text(pace.sessionBudget == 0 ? "No session allowance" : "Session use pending")
                        .font(.system(size: 24, weight: .semibold))
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
                if let targetPercent {
                    QuotaReading(label: "Target \(targetDisplay.rawValue) by now", percent: targetPercent, size: 34, alignment: .trailing)
                }
            }

            if let usedFraction, let shownPercent {
                let kind = targetDisplay.rawValue
                let displayedFraction = targetDisplay.percent(forUsed: usedFraction * 100) / 100
                QuotaUsageBar(used: usedFraction, target: targetFraction,
                              showsRemaining: targetDisplay == .left, thickness: 10)
                    .accessibilityRepresentation {
                        ProgressView(value: min(1, max(0, displayedFraction)), total: 1)
                            .accessibilityLabel("Session allowance \(kind)")
                            .accessibilityValue(targetPercent.map { "\(shownPercent) \(kind), \($0) target by now" }
                                ?? "\(shownPercent) \(kind)")
                    }
            }

            VStack(alignment: .leading, spacing: 5) {
                if let complement = targetDisplay == .used
                    ? DashboardFormat.sessionRemainingPercent(pace)
                    : DashboardFormat.sessionPercent(pace, display: .used) {
                    let prefix = pace.sessionStartInferred ? "Estimated " : ""
                    Text("\(prefix)\(complement) \(targetDisplay.complement.rawValue) in this session")
                }
                if let comparison {
                    let kind = targetDisplay.rawValue
                    let actual = DashboardFormat.percent(targetDisplay.percent(for: comparison))
                    let target = DashboardFormat.percent(targetDisplay.percent(for: session))
                    Text("\(actual) weekly quota \(kind) · \(target) target \(kind) at \(DashboardFormat.time(session.end))")
                }
                if data.guidance?.action != .checkUsage, let projected = pace.sessionProjectedRoomToTarget {
                    Text(projectedRoomText(projected))
                }
            }
            .font(.caption)
            .foregroundStyle(Graphite.muted)
            .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func sessionAdvice(_ pace: Pace) -> some View {
        let guidance = data.guidance
        let action = guidance?.action ?? .awaitingReadings

        return VStack(alignment: .leading, spacing: 9) {
            Text(action.title)
                .font(.system(size: 25, weight: .bold))
                .tracking(-0.7)
                .accessibilityAddTraits(.isHeader)
            guidanceDetail(pace, action: action, guidance: guidance)
            if data.showsRecommendation {
                GraphiteDivider()
                VStack(alignment: .leading, spacing: 4) {
                    if data.budgetWarning != nil {
                        Text("Suggested for the next turn")
                            .font(.caption)
                            .foregroundStyle(Graphite.muted)
                    }
                    Text(data.recommendation)
                        .font(.callout.weight(.semibold))
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Graphite.inset, in: RoundedRectangle(cornerRadius: 12))
    }

    private func guidanceDetail(_ pace: Pace, action: SessionAction, guidance: SessionGuidance?) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            if action == .checkUsage {
                Text("Refresh to check your current room to target.")
            } else if action == .pause {
                if (quota?.usedPercent ?? 0) >= 100 {
                    Text("Your weekly quota is used up. You’ve reached the cumulative session target.")
                } else if let room = pace.sessionRoomToTarget, room < 0 {
                    Text("You’ve used \(DashboardFormat.percent(abs(room))) of weekly quota beyond the session target.")
                } else {
                    Text("You’ve reached the cumulative session target.")
                }
            } else if action == .awaitingReadings {
                Text("The tracker needs another quota reading to estimate your pace.")
            }

            if pace.sessionUsed != nil, let rate = guidance?.safeSessionPercentPerHour {
                Text("Aim for about \(DashboardFormat.percent(rate)) of this session’s budget per hour or less.")
            }
            if action != .pause, guidance?.weeklyRisk == true {
                Text("At this pace, your weekly quota could run out before reset.")
            }
            if pace.sessionBudget == nil {
                Text("Session percentages need a quota reading around the session start.")
            }
        }
        .font(.callout)
        .foregroundStyle(Graphite.muted)
        .fixedSize(horizontal: false, vertical: true)
    }

    private func projectedRoomText(_ projectedRoom: Double) -> String {
        let rounded = (projectedRoom * 10).rounded() / 10
        guard rounded != 0 else { return "Projected to finish at the session target" }
        let amount = DashboardFormat.percent(abs(rounded))
        return rounded > 0
            ? "Projected to finish with \(amount) of weekly quota to spare against the target"
            : "Projected to exceed the session target by \(amount) of weekly quota"
    }

    private func outsideSession(_ sessions: [WorkSession]) -> some View {
        let next = sessions.first { $0.start > now }
        let calendar = DashboardFormat.calendar
        let alreadyWorkedToday = sessions.contains { $0.end <= now && calendar.isDate($0.end, inSameDayAs: now) }
        let isBetween = alreadyWorkedToday && (next.map { calendar.isDate($0.start, inSameDayAs: now) } ?? false)

        return VStack(alignment: .leading, spacing: 5) {
            Text(isBetween ? "Between sessions" : "Outside work hours")
                .font(.title2.weight(.semibold))
            if let next {
                Text("Next \(DashboardFormat.sessionName(next)) · \(DashboardFormat.day(next.start)) · \(DashboardFormat.timeRange(next))")
                    .font(.callout)
                    .foregroundStyle(Graphite.muted)
            }
        }
    }
}

private struct QuotaUsageBar: View {
    let used: Double
    let target: Double?
    var showsRemaining = false
    var thickness: CGFloat = 8

    var fill: Color {
        guard let target else { return Graphite.muted }
        if used <= target { return Graphite.onPace }
        if used <= target * 1.10 { return Graphite.caution }
        return Graphite.risk
    }

    var body: some View {
        GeometryReader { geometry in
            let width = geometry.size.width
            let amount = showsRemaining ? 1 - used : used
            ZStack(alignment: .leading) {
                Capsule()
                    .fill(Graphite.track)
                    .frame(height: thickness)
                Capsule()
                    .fill(fill)
                    .frame(width: width * min(1, max(0, amount)), height: thickness)
                if let target {
                    let marker = width * min(1, max(0, showsRemaining ? 1 - target : target))
                    RoundedRectangle(cornerRadius: 1.5)
                        .fill(Graphite.ink)
                        .frame(width: 3, height: thickness + 8)
                        .offset(x: max(0, min(width - 3, marker - 1.5)))
                }
            }
            .frame(height: thickness + 8)
        }
        .frame(height: thickness + 8)
    }
}

private final class ScheduleDraft: ObservableObject {
    @Published var isPresented = false
    @Published var schedule: WorkSchedule = .standard

    func addSession() {
        let start = suggestedStart
        schedule.sessions.append(SessionTemplate(
            id: UUID(), name: "Session \(schedule.sessions.count + 1)", startMinute: start,
            endMinute: min(start + 60, lastMinute, nextSessionStart(after: start) ?? lastMinute)
        ))
    }

    private let lastMinute = 24 * 60 - 1

    private var suggestedStart: Int {
        guard !schedule.sessions.isEmpty else { return 9 * 60 + 30 }
        var next = 0
        var lastGap: Int?
        for session in schedule.sessions.sorted(by: { $0.startMinute < $1.startMinute }) {
            if next < session.startMinute { lastGap = next }
            next = max(next, session.endMinute)
        }
        return next < lastMinute ? next : lastGap ?? 9 * 60 + 30
    }

    private func nextSessionStart(after start: Int) -> Int? {
        schedule.sessions.map(\.startMinute).filter { $0 > start }.min()
    }
}

private struct ScheduleEditor: View {
    @ObservedObject var draft: ScheduleDraft
    let save: (WorkSchedule) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Edit sessions")
                    .font(.title2.weight(.semibold))
                    .accessibilityAddTraits(.isHeader)
            }

            if draft.schedule.sessions.isEmpty {
                Text("No sessions scheduled")
                    .foregroundStyle(Graphite.muted)
            } else {
                ScrollViewReader { scroll in
                    ScrollView {
                        VStack(spacing: 0) {
                            sessionFields
                            Color.clear.frame(height: 1)
                                .id("session-editor-end")
                                .accessibilityHidden(true)
                        }
                    }
                    .frame(maxHeight: 256)
                    .onChange(of: draft.schedule.sessions.count) { before, after in
                        if after > before { scroll.scrollTo("session-editor-end", anchor: .bottom) }
                    }
                }
            }

            if !draft.schedule.isValid {
                Text("Give each session a name and an end time after its start. Sessions cannot overlap.")
                    .font(.callout)
                    .foregroundStyle(Graphite.risk)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 8) {
                Button("Add session", action: draft.addSession)
                Spacer()
                Button("Cancel") { draft.isPresented = false }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    guard draft.schedule.isValid else { return }
                    save(draft.schedule)
                    draft.isPresented = false
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!draft.schedule.isValid)
            }
        }
        .padding(24)
        .frame(width: 540)
        .background(Graphite.panel)
        .foregroundStyle(Graphite.ink)
    }

    private var sessionFields: some View {
        Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 10) {
            GridRow {
                Text("Name").font(.caption).foregroundStyle(Graphite.muted)
                Text("Start").font(.caption).foregroundStyle(Graphite.muted)
                Text("End").font(.caption).foregroundStyle(Graphite.muted)
            }
            ForEach(draft.schedule.sessions) { session in
                GridRow {
                    TextField("Session name", text: field(session, \.name))
                        .textFieldStyle(.roundedBorder)
                        .frame(maxWidth: .infinity)
                    timePicker("Start time", minutes: field(session, \.startMinute))
                    timePicker("End time", minutes: field(session, \.endMinute))
                    Button("Remove") {
                        draft.schedule.sessions.removeAll { $0.id == session.id }
                    }
                    .accessibilityLabel("Remove \(session.name)")
                }
            }
        }
    }

    private func field<Value>(_ session: SessionTemplate, _ keyPath: WritableKeyPath<SessionTemplate, Value>) -> Binding<Value> {
        Binding(
            get: {
                draft.schedule.sessions.first { $0.id == session.id }?[keyPath: keyPath] ?? session[keyPath: keyPath]
            },
            set: { value in
                if let index = draft.schedule.sessions.firstIndex(where: { $0.id == session.id }) {
                    draft.schedule.sessions[index][keyPath: keyPath] = value
                }
            }
        )
    }

    private func timePicker(_ label: String, minutes: Binding<Int>) -> some View {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .gmt
        let time = Binding(
            get: { Date(timeIntervalSince1970: Double(minutes.wrappedValue) * 60) },
            set: {
                let parts = calendar.dateComponents([.hour, .minute], from: $0)
                minutes.wrappedValue = (parts.hour ?? 0) * 60 + (parts.minute ?? 0)
            }
        )
        return DatePicker(label, selection: time, displayedComponents: .hourAndMinute)
            .datePickerStyle(.stepperField)
            .labelsHidden()
            .accessibilityLabel(label)
            .environment(\.calendar, calendar)
            .environment(\.timeZone, .gmt)
    }
}

private final class WorkdaysDraft: ObservableObject {
    @Published var isPresented = false
    @Published var days: Set<Workday> = Workday.standard
}

private struct WorkdaysEditor: View {
    @ObservedObject var draft: WorkdaysDraft
    let save: (Set<Workday>) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Work days")
                    .font(.title2.weight(.semibold))
                    .accessibilityAddTraits(.isHeader)
                Text("Choose the days that include your work sessions each week.")
                    .foregroundStyle(Graphite.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }

            VStack(alignment: .leading, spacing: 8) {
                ForEach(Workday.allCases, id: \.self) { day in
                    Toggle(day.title, isOn: selection(for: day))
                }
            }
            .toggleStyle(.checkbox)

            HStack(spacing: 8) {
                Spacer()
                Button("Cancel") { draft.isPresented = false }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    save(draft.days)
                    draft.isPresented = false
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 390)
        .background(Graphite.panel)
        .foregroundStyle(Graphite.ink)
    }

    private func selection(for day: Workday) -> Binding<Bool> {
        Binding(
            get: { draft.days.contains(day) },
            set: { selected in
                if selected {
                    draft.days.insert(day)
                } else {
                    draft.days.remove(day)
                }
            }
        )
    }
}

private struct RecommendationPanel: View {
    let data: DashboardData
    let showsAdvice: Bool

    var body: some View {
        if showsAdvice && data.showsRecommendation {
            Panel {
                VStack(alignment: .leading, spacing: 14) {
                    Text(data.status)
                        .font(.subheadline.weight(.semibold))

                    VStack(alignment: .leading, spacing: 4) {
                        Text(data.recommendation)
                            .font(.system(size: 22, weight: .semibold))
                            .accessibilityAddTraits(.isHeader)
                            .fixedSize(horizontal: false, vertical: true)
                        if !data.recommendationDetail.isEmpty {
                            Text(data.recommendationDetail)
                                .font(.callout)
                                .foregroundStyle(Graphite.muted)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }

                    GraphiteDivider()
                    activity
                }
            }
        } else {
            activity
        }
    }

    private var activity: some View {
        Text("Recent Codex activity · \(data.recentActivity)")
            .font(.caption)
            .foregroundStyle(Graphite.muted)
            .fixedSize(horizontal: false, vertical: true)
    }
}

private struct QuotaReading: View {
    let label: String
    let percent: String
    let size: CGFloat
    var alignment: HorizontalAlignment = .leading

    var body: some View {
        VStack(alignment: alignment, spacing: 3) {
            Text(label)
                .font(.caption.weight(.medium))
                .foregroundStyle(Graphite.muted)
            Text(percent)
                .font(.system(size: size, weight: .bold).monospacedDigit())
                .tracking(-size * 0.035)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .accessibilityElement(children: .combine)
    }
}

private struct WeeklyPanel: View {
    let quota: QuotaSample
    let pace: Pace?
    let shortQuota: QuotaSample?
    let targetDisplay: WeeklyTargetDisplay
    let manualResetTarget: Date?
    let demoNow: Date?
    let changeResetTarget: (Date?, Date) -> Bool

    private var usesManualResetTarget: Bool {
        guard let manualResetTarget else { return false }
        return pace?.planEndsAt == manualResetTarget
    }

    var body: some View {
        let used = quota.usedPercent / 100
        let plannedTarget = pace.flatMap { $0.sessions.isEmpty ? nil : $0.targetPercent }
        let target = plannedTarget.map { $0 / 100 }
        let actual = targetDisplay.percent(forUsed: quota.usedPercent)
        let actualText = DashboardFormat.percent(actual)
        let targetText = plannedTarget.map { DashboardFormat.percent(targetDisplay.percent(forUsed: $0)) }
        let status = "\(actualText) \(targetDisplay.rawValue)"
        let other = targetDisplay.complement

        Panel {
            VStack(alignment: .leading, spacing: 13) {
                SectionTitle(text: "Weekly quota")

                HStack(alignment: .bottom, spacing: 10) {
                    QuotaReading(label: "Quota \(targetDisplay.rawValue)", percent: actualText, size: 32)
                    Spacer(minLength: 0)
                    if let targetText {
                        QuotaReading(label: "Target \(targetDisplay.rawValue) by now", percent: targetText,
                                     size: 29, alignment: .trailing)
                    }
                }

                QuotaUsageBar(used: used, target: target, showsRemaining: targetDisplay == .left)
                    .accessibilityRepresentation {
                        ProgressView(value: min(100, max(0, actual)), total: 100)
                            .accessibilityLabel("Weekly allowance \(targetDisplay.rawValue)")
                            .accessibilityValue(targetText.map { "\(status), \($0) target by now" } ?? status)
                    }

                VStack(alignment: .leading, spacing: 4) {
                    Text("\(DashboardFormat.percent(other.percent(forUsed: quota.usedPercent))) \(other.rawValue)")
                    if let exhaustionDate = pace?.exhaustionDate {
                        Text("Projected to run out \(DashboardFormat.dateTime(exhaustionDate))")
                    }
                }
                .font(.caption)
                .foregroundStyle(Graphite.muted)
                .monospacedDigit()

                if let pace {
                    GraphiteDivider()
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 10), GridItem(.flexible())],
                              alignment: .leading, spacing: 10) {
                        Metric(label: "Recent pace", value: DashboardFormat.rate(pace.recentRate))
                        Metric(label: "Safe pace", value: DashboardFormat.rate(pace.safeRate))
                        Metric(label: usesManualResetTarget ? "Projected use by target" : "Projected weekly use",
                               value: DashboardFormat.percent(pace.projectedPercent))
                        Metric(label: usesManualResetTarget ? "Work time to target" : "Work time left",
                               value: DashboardFormat.hours(pace.remainingWorkHours))
                    }
                    if pace.recentRate == nil && pace.remainingWorkHours > 0 && quota.usedPercent < 100 {
                        Text("The forecast appears after enough quota readings during work hours.")
                            .font(.caption)
                            .foregroundStyle(Graphite.muted)
                    }
                }

                GraphiteDivider()
                ResetTargetDetails(
                    accountReset: quota.resetsAt,
                    manualTarget: manualResetTarget,
                    usesManualTarget: usesManualResetTarget,
                    demoNow: demoNow,
                    changeTarget: changeResetTarget
                )

                if let shortQuota {
                    GraphiteDivider()
                    ShortQuotaSummary(quota: shortQuota)
                }
            }
        }
    }
}

private struct ResetTargetDetails: View {
    let accountReset: Date
    let manualTarget: Date?
    let usesManualTarget: Bool
    let demoNow: Date?
    let changeTarget: (Date?, Date) -> Bool
    @StateObject private var draft = ResetTargetDraft()

    var body: some View {
        let now = demoNow ?? Date()
        let canSetTarget = ResetTargetDraft.nextMinute(after: now) < accountReset

        VStack(alignment: .leading, spacing: 7) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Scheduled reset")
                    .font(.caption)
                    .foregroundStyle(Graphite.muted)
                Text(DashboardFormat.dateTime(accountReset))
                    .font(.callout.weight(.semibold))
                    .monospacedDigit()
                if let manualTarget {
                    Text("Manual reset target · \(DashboardFormat.dateTime(manualTarget))")
                        .fontWeight(.medium)
                    if manualTarget <= now {
                        Text("Target passed. Pacing uses the scheduled account reset.")
                    } else if usesManualTarget {
                        Text("Pacing uses this target.")
                    }
                }
            }
            .fixedSize(horizontal: false, vertical: true)
            Button(manualTarget == nil ? "Set reset target" : "Edit reset target") {
                let now = demoNow ?? Date()
                guard ResetTargetDraft.nextMinute(after: now) < accountReset else { return }
                draft.begin(accountReset: accountReset, manualTarget: manualTarget, now: now)
            }
            .controlSize(.small)
            .disabled(!canSetTarget)
            .help(canSetTarget ? "Plan around an earlier manual reset" : "The quota window has ended or closes too soon to set a target")
        }
        .sheet(isPresented: $draft.isPresented) {
            ResetTargetEditor(draft: draft, demoNow: demoNow, changeTarget: changeTarget)
        }
    }
}

private final class ResetTargetDraft: ObservableObject {
    @Published var isPresented = false
    @Published var selection = Date.distantFuture
    @Published var windowChanged = false
    private(set) var accountReset = Date.distantPast
    private(set) var hasTarget = false

    func begin(accountReset: Date, manualTarget: Date?, now: Date) {
        self.accountReset = accountReset
        hasTarget = manualTarget != nil
        windowChanged = false
        let nextMinute = Self.nextMinute(after: now)
        let nextHour = DashboardFormat.calendar.dateInterval(of: .hour, for: now)?.end ?? nextMinute
        selection = manualTarget ?? (nextHour < accountReset ? nextHour : nextMinute)
        isPresented = true
    }

    var target: Date {
        DashboardFormat.calendar.dateInterval(of: .minute, for: selection)?.start ?? selection
    }

    static func nextMinute(after date: Date) -> Date {
        DashboardFormat.calendar.dateInterval(of: .minute, for: date)?.end ?? date.addingTimeInterval(60)
    }
}

private struct ResetTargetEditor: View {
    @ObservedObject var draft: ResetTargetDraft
    let demoNow: Date?
    let changeTarget: (Date?, Date) -> Bool

    var body: some View {
        TimelineView(.periodic(from: .now, by: 30)) { timeline in
            form(now: demoNow ?? timeline.date)
        }
    }

    private func form(now: Date) -> some View {
        let validation = validationMessage(now: now)

        return VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 5) {
                Text("Manual reset target")
                    .font(.title2.weight(.semibold))
                    .accessibilityAddTraits(.isHeader)
                Text("Use your manual reset in Codex by this time. The target applies to this quota window.")
                    .foregroundStyle(Graphite.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Text("Scheduled account reset · \(DashboardFormat.dateTime(draft.accountReset))")
                .font(.callout)

            DatePicker("Date and time", selection: $draft.selection,
                       displayedComponents: [.date, .hourAndMinute])
                .datePickerStyle(.stepperField)
                .environment(\.calendar, DashboardFormat.calendar)
                .environment(\.timeZone, DashboardFormat.timeZone)

            if let validation {
                Text(validation)
                    .font(.callout)
                    .foregroundStyle(Graphite.risk)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 8) {
                if draft.hasTarget {
                    Button("Clear target") { commit(nil) }
                        .disabled(draft.windowChanged)
                }
                Spacer()
                Button("Cancel") { draft.isPresented = false }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    guard validationMessage(now: demoNow ?? Date()) == nil else { return }
                    commit(draft.target)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(validation != nil)
            }
        }
        .padding(24)
        .frame(width: 430)
        .background(Graphite.panel)
        .foregroundStyle(Graphite.ink)
    }

    private func validationMessage(now: Date) -> String? {
        if draft.windowChanged { return "The quota window changed. Reopen the editor for the current window." }
        if draft.target <= now { return "Choose a date and time in the future." }
        if draft.target >= draft.accountReset { return "Choose a time before the scheduled account reset." }
        return nil
    }

    private func commit(_ target: Date?) {
        if changeTarget(target, draft.accountReset) {
            draft.isPresented = false
        } else {
            draft.windowChanged = true
        }
    }
}

private struct ShortQuotaSummary: View {
    let quota: QuotaSample

    var body: some View {
        Text("\(DashboardFormat.hours(Double(quota.windowMinutes) / 60)) limit · \(DashboardFormat.percent(quota.usedPercent)) used · Resets \(DashboardFormat.dateTime(quota.resetsAt))")
            .font(.caption)
            .foregroundStyle(Graphite.muted)
    }
}

enum WeeklyTargetDisplay: String, CaseIterable {
    case used, left

    var complement: Self {
        self == .used ? .left : .used
    }

    func percent(forUsed amount: Double) -> Double {
        self == .used ? amount : max(0, 100 - amount)
    }

    func percent(for session: WorkSession) -> Double {
        percent(forUsed: session.cumulativeTargetPercent)
    }

    func percent(for comparison: ShiftEndComparison) -> Double {
        self == .used ? comparison.actualUsed : comparison.actualLeft
    }
}

private struct WorkPlanPanel: View {
    let pace: Pace
    let schedule: WorkSchedule
    let quota: QuotaSample?
    let now: Date
    let targetDisplay: WeeklyTargetDisplay
    private let sessionColumnWidth: CGFloat = 145

    private var templates: [SessionTemplate] {
        schedule.sessions.sorted { $0.startMinute < $1.startMinute }
    }

    var body: some View {
        Panel {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .firstTextBaseline) {
                    SectionTitle(text: "Work sessions")
                    Spacer()
                    Text(targetDisplay == .used ? "Used at session end" : "Left at session end")
                        .font(.caption)
                        .foregroundStyle(Graphite.muted)
                        .multilineTextAlignment(.trailing)
                }

                if pace.sessions.isEmpty {
                    Text("The quota window contains no scheduled work hours.")
                        .font(.callout)
                        .foregroundStyle(Graphite.muted)
                } else {
                    if templates.count <= 2 {
                        sessionTable
                    } else {
                        ViewThatFits(in: .horizontal) {
                            sessionTable
                            ScrollView(.horizontal) {
                                sessionTable.fixedSize(horizontal: true, vertical: false)
                            }
                        }
                    }
                }
            }
        }
    }

    private var sessionTable: some View {
        let byDay = Dictionary(grouping: pace.sessions) {
            DashboardFormat.calendar.startOfDay(for: $0.start)
        }
        let days = byDay.keys.sorted()

        return Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 8) {
            GridRow {
                columnHeader("Workday", minimumWidth: 96)
                ForEach(templates) { template in
                    let name = template.name.trimmingCharacters(in: .whitespacesAndNewlines)
                    let title = "\(name) · \(scheduledRange(template))"
                    columnHeader(name, time: scheduledRange(template), minimumWidth: sessionColumnWidth)
                        .help(title)
                }
            }
            GraphiteDivider()
                .gridCellUnsizedAxes(.horizontal)
            ForEach(days, id: \.self) { day in
                let sessions = Dictionary(uniqueKeysWithValues: (byDay[day] ?? []).map { ($0.template.id, $0) })
                GridRow(alignment: .top) {
                    Text(DashboardFormat.day(day))
                        .font(.callout.weight(.medium))
                        .frame(maxWidth: .infinity, alignment: .leading)
                    ForEach(templates) { template in
                        allocation(sessions[template.id], scheduledTime: scheduledRange(template))
                            .frame(minWidth: sessionColumnWidth, maxWidth: .infinity, alignment: .leading)
                    }
                }
                if day != days.last {
                    GraphiteDivider()
                        .gridCellUnsizedAxes(.horizontal)
                }
            }
        }
        .font(.callout)
    }

    private func columnHeader(_ title: String, time: String? = nil, minimumWidth: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(.caption.weight(.semibold))
                .lineLimit(1)
            if let time {
                Text(time)
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(Graphite.muted)
            }
        }
        .frame(minWidth: minimumWidth, maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isHeader)
    }

    private func scheduledRange(_ template: SessionTemplate) -> String {
        DashboardFormat.timeRange(start: template.startMinute, end: template.endMinute)
    }

    @ViewBuilder
    private func allocation(_ session: WorkSession?, scheduledTime: String) -> some View {
        if let session {
            sessionAllocation(session, scheduledTime: scheduledTime)
        } else {
            Text("—")
                .foregroundStyle(Graphite.muted)
                .accessibilityLabel("Outside this quota window")
        }
    }

    private func sessionAllocation(_ session: WorkSession, scheduledTime: String) -> some View {
        let isCurrent = session.id == pace.currentSession?.id
        let comparison = isCurrent ? shiftEndComparison(for: session, quota: quota, now: now) : nil
        let target = DashboardFormat.percent(targetDisplay.percent(for: session))

        return VStack(alignment: .leading, spacing: 2) {
            if let comparison {
                activeComparison(target: target, comparison: comparison)
            } else {
                Text(target)
                    .fontWeight(.semibold)
                    .monospacedDigit()
            }
            HStack(spacing: 7) {
                Text("\(DashboardFormat.percent(session.targetPercent)) session share")
                    .font(.caption)
                    .foregroundStyle(Graphite.muted)
                    .monospacedDigit()
                if isCurrent {
                    Text("Now")
                        .font(.caption2.weight(.semibold))
                }
            }
            let range = DashboardFormat.timeRange(session)
            if range != scheduledTime {
                Text(range)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(Graphite.muted)
            }
        }
        .padding(5)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(isCurrent ? Graphite.selection : .clear, in: RoundedRectangle(cornerRadius: 6))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(allocationLabel(session, comparison: comparison))
    }

    private func activeComparison(target: String, comparison: ShiftEndComparison) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Target \(targetDisplay.rawValue)")
                        .font(.caption)
                        .foregroundStyle(Graphite.muted)
                    Text(target)
                        .fontWeight(.semibold)
                        .monospacedDigit()
                }
                VStack(alignment: .leading, spacing: 2) {
                    Text("Actual \(targetDisplay.rawValue)")
                        .font(.caption)
                        .foregroundStyle(Graphite.muted)
                    Text(DashboardFormat.percent(targetDisplay.percent(for: comparison)))
                        .fontWeight(.medium)
                        .monospacedDigit()
                }
            }
            Text(comparison.isStale ? lastReading : roomDescription(comparison.roomToTarget))
                .font(.caption)
                .foregroundStyle(Graphite.muted)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var lastReading: String {
        guard let quota else { return "Last reading" }
        return "Last reading \(DashboardFormat.time(quota.observedAt))"
    }

    private func roomDescription(_ room: Double) -> String {
        let rounded = (room * 10).rounded() / 10
        guard rounded != 0 else { return "At target" }
        let amount = DashboardFormat.percent(abs(rounded))
        return rounded > 0 ? "\(amount) room to target" : "\(amount) over target"
    }

    private func allocationLabel(_ session: WorkSession, comparison: ShiftEndComparison?) -> String {
        let allocation = DashboardFormat.percent(session.targetPercent)
        let target = DashboardFormat.percent(targetDisplay.percent(for: session))
        let current = session.id == pace.currentSession?.id ? ", current session" : ""
        let range = DashboardFormat.timeRange(session)
        var label = "Planned weekly quota \(targetDisplay.rawValue) by session end \(target), \(range), \(allocation) session share\(current)"
        if let comparison {
            let actual = DashboardFormat.percent(targetDisplay.percent(for: comparison))
            let room = comparison.isStale ? lastReading : roomDescription(comparison.roomToTarget)
            label += ", actual weekly quota \(targetDisplay.rawValue) \(actual), \(room)"
        }
        return label
    }
}

private struct GraphiteDivider: View {
    var body: some View {
        Rectangle()
            .fill(Graphite.line)
            .frame(height: 1)
            .accessibilityHidden(true)
    }
}

private struct Panel<Content: View>: View {
    var surface = Graphite.panel
    var padding: CGFloat = 16
    var cornerRadius: CGFloat = 14
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(surface, in: RoundedRectangle(cornerRadius: cornerRadius))
            .overlay {
                RoundedRectangle(cornerRadius: cornerRadius)
                    .strokeBorder(Graphite.line, lineWidth: 1)
                    .allowsHitTesting(false)
            }
    }
}

private struct SectionTitle: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.headline)
            .accessibilityAddTraits(.isHeader)
    }
}

private struct Metric: View {
    let label: String
    let value: String

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(label)
                .font(.caption)
                .foregroundStyle(Graphite.muted)
            Text(value)
                .font(.system(size: 17, weight: .semibold).monospacedDigit())
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

enum DashboardFormat {
    static var timeZone: TimeZone { .autoupdatingCurrent }
    static var locale: Locale { .autoupdatingCurrent }
    static var calendar: Calendar { WorkClock.calendar }

    static func dateTime(_ date: Date) -> String {
        "\(day(date)) \(time(date))"
    }

    static func day(_ date: Date) -> String {
        date.formatted(Date.FormatStyle(locale: locale, timeZone: timeZone).weekday(.abbreviated).day().month(.abbreviated))
    }

    static func timeRange(_ session: WorkSession) -> String {
        timeRange(start: minuteOfDay(session.start), end: minuteOfDay(session.end))
    }

    static func sessionName(_ session: WorkSession) -> String {
        session.template.name.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func timeRange(start: Int, end: Int) -> String {
        "\(time(start))–\(time(end))"
    }

    static func time(_ date: Date) -> String {
        time(minuteOfDay(date))
    }

    static func timeRemaining(until end: Date, now: Date) -> String {
        let minutes = Int(ceil(max(0, end.timeIntervalSince(now)) / 60))
        let hours = minutes / 60
        let fraction = minutes % 60
        if hours == 0 { return "\(fraction)m" }
        if fraction == 0 { return "\(hours)h" }
        return "\(hours)h \(fraction)m"
    }

    static func minuteOfDay(_ date: Date) -> Int {
        let components = calendar.dateComponents([.hour, .minute], from: date)
        return (components.hour ?? 0) * 60 + (components.minute ?? 0)
    }

    private static func time(_ minutes: Int) -> String {
        String(format: "%02d:%02d", minutes / 60, minutes % 60)
    }

    static func percent(_ value: Double?) -> String {
        value.map { ($0 / 100).formatted(.percent.precision(.fractionLength(0 ... 1))) } ?? "—"
    }

    static func sessionPercent(_ amount: Double?, budget: Double?) -> String {
        guard let amount, let budget, budget > 0 else { return percent(nil) }
        return percent(100 * amount / budget)
    }

    static func sessionRemainingPercent(_ pace: Pace) -> String? {
        guard let budget = pace.sessionBudget, budget > 0, let room = pace.sessionRoomToTarget else { return nil }
        return sessionPercent(min(budget, max(0, room)), budget: budget)
    }

    static func sessionPercent(_ pace: Pace, display: WeeklyTargetDisplay) -> String? {
        pace.sessionUsedFraction.map { percent(display.percent(forUsed: 100 * $0)) }
    }

    static func menuBarTitle(for data: DashboardData, now: Date, display: WeeklyTargetDisplay) -> String {
        guard let pace = data.pace else { return "Codex Pace" }
        let sessionNow = data.isDemo ? data.quota?.observedAt ?? now : now
        guard let session = pace.currentSession, session.start <= sessionNow, sessionNow < session.end else {
            return "Codex · Off hours"
        }

        let action = data.guidance?.action ?? .awaitingReadings
        var title = "\(session.menuLabel) · \(action.title)"
        if action != .checkUsage, let used = sessionPercent(pace, display: display) {
            title += " · \(used)"
            if let target = pace.sessionTargetFraction {
                title += " / \(percent(display.percent(forUsed: 100 * target)))"
            }
        }
        return title
    }

    static func rate(_ value: Double?) -> String {
        value.map { "\(percent($0))/h" } ?? "—"
    }

    static func hours(_ value: Double) -> String {
        "\(value.formatted(.number.precision(.fractionLength(0 ... 1)))) h"
    }
}
