import SwiftUI

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
    var usesManualResetTarget: Bool
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
        budgetWarning != nil || recommendationTone == .critical || recommendationTone == .healthy
    }
}

enum HeroFocus: String {
    case session, weekly
}

struct Dashboard: View {
    private enum Editor: String, Identifiable {
        case sessions, workdays

        var id: String { rawValue }
    }

    let data: DashboardData
    @Binding var schedule: WorkSchedule
    @Binding var sessionTemplates: [SavedSessionTemplate]
    let workdays: Set<Workday>
    let changeWorkdays: (Set<Workday>) -> Void
    let manualResetTarget: Date?
    let changeResetTarget: (Date?) -> Bool
    let refresh: () -> Void
    @Binding var targetDisplay: WeeklyTargetDisplay
    @Binding var heroFocus: HeroFocus
    @StateObject private var sessionsDraft = ScheduleDraft()
    @StateObject private var workdaysDraft = WorkdaysDraft()
    @ViewState private var activeEditor: Editor?

    var body: some View {
        let now = data.isDemo ? data.quota?.observedAt ?? Date.now : Date.now
        GeometryReader { geometry in
            let width = min(1024, max(0, geometry.size.width - 44))
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    header
                    dashboardCards(width: width, now: now)
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
        .sheet(item: $activeEditor) { editor in
            editorContent(editor)
                .padding(24)
                    .frame(width: 500)
                .background(Graphite.panel)
                .foregroundStyle(Graphite.ink)
        }
    }

    private func dashboardCards(width: CGFloat, now: Date) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            hero(width: width, now: now)
            if let error = data.error {
                Text(error)
                    .font(.callout)
                    .foregroundStyle(Graphite.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if let pace = data.pace {
                workPlan(pace, now: now)
            }
            DashboardFooter(activity: data.recentActivity, source: data.source)
        }
    }

    @ViewBuilder
    private func editorContent(_ editor: Editor) -> some View {
        switch editor {
        case .sessions:
            ScheduleEditor(draft: sessionsDraft, sessionTemplates: $sessionTemplates,
                           workdays: workdays, cancel: { activeEditor = nil }) {
                schedule = $0
                activeEditor = nil
            }
        case .workdays:
            WorkdaysEditor(draft: workdaysDraft, cancel: { activeEditor = nil }) {
                changeWorkdays($0)
                activeEditor = nil
            }
        }
    }

    private func openEditor(_ editor: Editor) {
        switch editor {
        case .sessions:
            sessionsDraft.begin(schedule, workdays: workdays)
        case .workdays:
            workdaysDraft.days = workdays
        }
        activeEditor = editor
    }

    private func hero(width: CGFloat, now: Date) -> some View {
        Panel(surface: Graphite.hero, padding: 22, cornerRadius: 16) {
            VStack(alignment: .leading, spacing: 20) {
                heroHeader(now: now)

                switch heroFocus {
                case .session:
                    sessionContent(width: width, now: now)
                case .weekly:
                    if let quota = data.quota {
                        weeklyContent(quota, width: width, now: now)
                    } else {
                        Text(data.error == nil && data.shortQuota == nil ? "Checking quota" : "Weekly quota unavailable")
                            .font(.system(size: 16))
                            .foregroundStyle(Graphite.muted)
                    }
                }
                if data.quota == nil, let shortQuota = data.shortQuota {
                    ShortQuotaSummary(quota: shortQuota)
                }
                if data.showsRecommendation {
                    GraphiteDivider()
                    HeroRecommendation(data: data)
                }
            }
        }
    }

    private func heroHeader(now: Date) -> some View {
        let session = data.pace?.currentSession.flatMap { $0.start <= now && now < $0.end ? $0 : nil }
        return HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text(heroFocus == .weekly ? "Weekly quota" :
                     session.map(DashboardFormat.sessionName) ?? "Current session")
                    .font(.system(size: 24, weight: .semibold))
                    .accessibilityAddTraits(.isHeader)
                if let subtitle = heroSubtitle(session: session, now: now) {
                    Text(subtitle)
                        .font(.system(size: 16).monospacedDigit())
                        .foregroundStyle(Graphite.muted)
                }
            }
            Spacer(minLength: 0)
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 6) {
                    focusPicker
                    quotaView
                }
                VStack(alignment: .trailing, spacing: 6) {
                    focusPicker
                    quotaView
                }
            }
        }
    }

    private var focusPicker: some View {
        GraphiteSegments("Hero focus", selection: $heroFocus, options: [
            ("Session", .session), ("Weekly", .weekly),
        ])
        .help("Choose which target appears at the top of the dashboard")
    }

    private func heroSubtitle(session: WorkSession?, now: Date) -> String? {
        if heroFocus == .weekly, let quota = data.quota {
            if data.usesManualResetTarget, let manualResetTarget {
                return "Reset target \(DashboardFormat.dateTime(manualResetTarget))"
            }
            return "Resets \(DashboardFormat.day(quota.resetsAt)) at \(DashboardFormat.time(quota.resetsAt))"
        }
        if heroFocus == .session, let session {
            return "Ends at \(DashboardFormat.time(session.end)) · \(DashboardFormat.timeRemaining(until: session.end, now: now)) left"
        }
        return nil
    }

    private var header: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 16) {
                identity.fixedSize(horizontal: true, vertical: false)
                Spacer(minLength: 0)
                actions
            }
            VStack(alignment: .leading, spacing: 10) {
                identity
                actions
            }
        }
        .padding(.bottom, 4)
    }

    private var identity: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Codex Pace")
                .font(.system(size: 27, weight: .semibold))
                .tracking(-0.7)
                .accessibilityAddTraits(.isHeader)
            Text(data.isDemo ? "Demo data" : quotaTimestamp)
                .font(.system(size: 15))
                .foregroundStyle(Graphite.muted)
        }
    }

    private var quotaView: some View {
        GraphiteSegments("Quota view", selection: $targetDisplay, options: [
            ("Used", .used), ("Left", .left),
        ])
        .help("Changes Current session, Weekly quota and Work sessions")
    }

    private var actions: some View {
        HStack(spacing: 8) {
            Button("Edit sessions") { openEditor(.sessions) }
                .graphiteButton(.secondary)
                .help("Set work sessions for each day of the week")

            Button("Work days") { openEditor(.workdays) }
                .graphiteButton(.secondary)
                .help("Choose your recurring work days")

            Button(action: refresh) {
                Label(data.isRefreshing ? "Refreshing…" : "Refresh", systemImage: "arrow.clockwise")
            }
                .graphiteButton(.quiet)
                .disabled(data.isRefreshing)
                .keyboardShortcut("r", modifiers: .command)
                .help("Check for the latest Codex usage")
        }
        .fixedSize(horizontal: true, vertical: false)
    }

    private func sessionContent(width: CGFloat, now: Date) -> some View {
        CurrentSessionContent(data: data, now: now, width: width, targetDisplay: targetDisplay)
    }

    private func weeklyContent(_ quota: QuotaSample, width: CGFloat, now: Date) -> some View {
        WeeklyQuotaContent(quota: quota, pace: data.pace, shortQuota: data.shortQuota,
                           targetDisplay: targetDisplay, manualResetTarget: manualResetTarget,
                           usesManualResetTarget: data.usesManualResetTarget,
                           demoNow: data.isDemo ? quota.observedAt : nil, changeResetTarget: changeResetTarget,
                           width: width, now: now)
    }

    private func workPlan(_ pace: Pace, now: Date) -> some View {
        WorkPlanPanel(pace: pace, schedule: schedule, quota: data.quota, now: now, targetDisplay: $targetDisplay)
    }

    private var quotaTimestamp: String {
        guard let observedAt = data.quota?.observedAt else { return "Weekly usage tracker" }
        let day = DashboardFormat.calendar.isDateInToday(observedAt) ? "today" : DashboardFormat.day(observedAt)
        return "Updated \(day) at \(DashboardFormat.time(observedAt))"
    }
}

private struct PaceHeroLayout<Readings: View, Details: View>: View {
    let width: CGFloat
    @ViewBuilder var readings: Readings
    @ViewBuilder var details: Details

    var body: some View {
        if width >= 640 {
            HStack(alignment: .top, spacing: 24) {
                readings
                details.frame(width: floor((width - 68) * 0.43))
            }
        } else {
            VStack(alignment: .leading, spacing: 13) {
                readings
                details
            }
        }
    }
}

private struct HeroGuidancePanel<Content: View>: View {
    let action: PaceAction
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            PaceStatusPill(action: action)
            content
                .font(.system(size: 16))
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .background(Graphite.inset, in: RoundedRectangle(cornerRadius: 12))
    }
}

private struct HeroQuotaNote: View {
    let amount: String
    let detail: String

    var body: some View {
        let emphasis = Text(amount).fontWeight(.semibold).foregroundColor(Graphite.ink)
        Text("\(emphasis) \(detail)")
            .font(.system(size: 16))
            .foregroundStyle(Graphite.muted)
            .fixedSize(horizontal: false, vertical: true)
    }
}

private struct CurrentSessionContent: View {
    let data: DashboardData
    let now: Date
    let width: CGFloat
    let targetDisplay: WeeklyTargetDisplay

    private var quota: QuotaSample? { data.quota }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let pace = data.pace, let session = pace.currentSession, session.start <= now, now < session.end {
                PaceHeroLayout(width: width) {
                    sessionReadings(pace, session: session)
                } details: {
                    sessionDetails(pace)
                }
            } else if let pace = data.pace {
                outsideSession(pace.sessions)
            } else {
                Text("Checking quota")
                    .font(.system(size: 16))
                    .foregroundStyle(Graphite.muted)
            }
        }
    }

    private func sessionReadings(_ pace: Pace, session: WorkSession) -> some View {
        let progress = pace.sessionProgress(showsRemaining: targetDisplay == .left)
        let shownPercent = DashboardFormat.percent(progress.map { 100 * $0.displayedAmount })
        let carryoverCredit = DashboardFormat.carryoverCredit(progress)
        let targetPercent = progress.flatMap(\.displayedTarget).map { DashboardFormat.percent(100 * $0) }
        let comparison = shiftEndComparison(for: session, quota: quota, now: now)

        return VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .bottom, spacing: 16) {
                    if let carryoverCredit {
                        QuotaReading(label: "Carryover credit", percent: carryoverCredit, size: 54)
                            .help("\(shownPercent) net used")
                            .accessibilityHint("\(shownPercent) net used.")
                    } else {
                        QuotaReading(label: "Session quota \(targetDisplay.rawValue)", percent: shownPercent, size: 54)
                    }
                    Spacer(minLength: 0)
                    if let targetPercent {
                        QuotaReading(label: "Target \(targetDisplay.rawValue) by now", percent: targetPercent, size: 36, alignment: .trailing)
                    }
                }
                if carryoverCredit != nil {
                    Text("Extra quota from earlier sessions, measured against this session’s scheduled quota.")
                        .font(.system(size: 14))
                        .foregroundStyle(Graphite.muted)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let progress, let change = pace.sessionUsageChange {
                    let previous = progress.displayedFraction(forUsed: progress.used - change / 100)
                    let displayedChange = 100 * (progress.displayedAmount - previous)
                    let difference = (displayedChange * 10).rounded() / 10
                    if difference != 0 {
                        let sign = difference > 0 ? "+" : "−"
                        let amount = abs(difference).formatted(.number.precision(.fractionLength(0...1)))
                        let caption = carryoverCredit != nil && difference > 0
                            ? "\(amount)% of session quota used since last reading"
                            : "\(sign)\(amount)% of session quota since last reading"
                        Text(caption)
                            .font(.system(size: 14).monospacedDigit())
                            .foregroundStyle(Graphite.muted)
                    }
                }
            }

            if let progress {
                let kind = targetDisplay.rawValue
                let reading = carryoverCredit ?? "\(shownPercent) \(kind)"
                QuotaUsageBar(progress: progress, thickness: 10)
                    .accessibilityRepresentation {
                        ProgressView(value: progress.barFraction, total: 1)
                            .accessibilityLabel(carryoverCredit == nil ? "Session quota \(kind)" : "Carryover credit")
                            .accessibilityValue(targetPercent.map { "\(reading), \($0) target by now" } ?? reading)
                    }
            }

            if let comparison {
                let room = DashboardFormat.percent(max(0, comparison.roomToTarget))
                HeroQuotaNote(amount: "\(room) of weekly quota",
                              detail: comparison.isStale ? "available at last reading" : "available this session")
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func sessionDetails(_ pace: Pace) -> some View {
        let guidance = data.guidance
        let action = guidance?.action ?? .awaitingReadings

        return HeroGuidancePanel(action: action) {
            guidanceDetail(pace, action: action, guidance: guidance)
        }
    }

    private func guidanceDetail(_ pace: Pace, action: PaceAction, guidance: SessionGuidance?) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            if action == .checkUsage {
                Text("Refresh to see how much quota is available.")
            } else if action == .awaitingReadings {
                Text("Waiting for a current quota reading.")
            } else if action == .pause {
                if (quota?.usedPercent ?? 0) >= 100 {
                    Text("You’ve reached your weekly quota limit.")
                } else if let room = pace.sessionRoomToTarget, room < 0 {
                    Text("You’ve used \(DashboardFormat.percent(abs(room))) of weekly quota beyond the session target.")
                } else {
                    Text("You’ve reached your session target.")
                }
            }

            if let rate = guidance?.safeSessionPercentPerHour {
                Text("Aim for \(DashboardFormat.percent(rate)) of your session quota per hour or less.")
            }
            if action != .pause, guidance?.weeklyRisk == true {
                Text("At this pace, your weekly quota could run out before reset.")
            }
        }
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
                    .font(.system(size: 16))
                    .foregroundStyle(Graphite.muted)
            }
        }
    }
}

private struct QuotaUsageBar: View {
    let progress: QuotaProgress
    var thickness: CGFloat = 8
    var weeklyAction: PaceAction?

    var body: some View {
        GeometryReader { geometry in
            let width = geometry.size.width
            ZStack(alignment: .leading) {
                Capsule()
                    .fill(Graphite.track)
                    .frame(height: thickness)
                Capsule()
                    .fill(progress.fillColor(weeklyAction: weeklyAction))
                    .frame(width: width * progress.barFraction, height: thickness)
                if let target = progress.markerFraction {
                    let marker = width * target
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

private final class WorkdaysDraft: ObservableObject {
    @Published var days: Set<Workday> = Workday.standard
}

private struct WorkdaysEditor: View {
    @ObservedObject var draft: WorkdaysDraft
    let cancel: () -> Void
    let save: (Set<Workday>) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Work days")
                    .font(.system(size: 21, weight: .semibold))
                    .accessibilityAddTraits(.isHeader)
                Text("Choose which days count toward your quota plan.")
                    .font(.system(size: 15))
                    .foregroundStyle(Graphite.muted)
            }

            LazyVGrid(columns: [GridItem(.flexible(), spacing: 8), GridItem(.flexible())], spacing: 8) {
                ForEach(Workday.allCases, id: \.self) { day in
                    dayButton(day)
                }
            }

            HStack(spacing: 8) {
                Spacer()
                Button("Cancel", action: cancel)
                    .graphiteButton(.quiet)
                    .keyboardShortcut(.cancelAction)
                Button("Save work days") { save(draft.days) }
                    .graphiteButton(.primary)
                    .keyboardShortcut(.defaultAction)
            }
        }
    }

    private func dayButton(_ day: Workday) -> some View {
        let selected = draft.days.contains(day)
        return Button {
            if selected {
                draft.days.remove(day)
            } else {
                draft.days.insert(day)
            }
        } label: {
            HStack {
                Text(day.title)
                Spacer(minLength: 6)
                if selected {
                    Image(systemName: "checkmark")
                        .foregroundStyle(Graphite.onPace)
                        .accessibilityHidden(true)
                }
            }
        }
        .buttonStyle(WorkdayTileStyle(selected: selected))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

private struct WorkdayTileStyle: ButtonStyle {
    let selected: Bool
    @Environment(\.isFocused) private var isFocused

    func makeBody(configuration: Configuration) -> some View {
        let shape = RoundedRectangle(cornerRadius: 8)
        configuration.label
            .font(.system(size: 15, weight: selected ? .semibold : .regular))
            .foregroundStyle(Graphite.ink)
            .padding(.horizontal, 10)
            .frame(minHeight: 40)
            .background(selected || configuration.isPressed ? Graphite.selection : Graphite.hero, in: shape)
            .overlay {
                shape.strokeBorder(isFocused ? Graphite.focus : Graphite.line, lineWidth: isFocused ? 2 : 1)
                    .allowsHitTesting(false)
            }
            .contentShape(.rect)
    }
}

private struct HeroRecommendation: View {
    let data: DashboardData

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(data.quota?.usedPercent ?? 0 >= 100 ? data.status : "Pacing advice")
                .font(.system(size: 16, weight: .medium))
                .foregroundStyle(Graphite.muted)
                .fixedSize()
            VStack(alignment: .leading, spacing: 4) {
                Text(data.recommendation)
                    .font(.system(size: 18, weight: .semibold))
                    .fixedSize(horizontal: false, vertical: true)
                if data.recommendationTone == .caution || data.budgetWarning?.isWeekly == true {
                    Text(data.recommendationDetail)
                        .font(.system(size: 16))
                        .foregroundStyle(Graphite.muted)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .accessibilityElement(children: .combine)
        .help(data.recommendationDetail)
    }
}

private struct DashboardFooter: View {
    let activity: String
    let source: String

    var body: some View {
        Panel {
            ViewThatFits(in: .horizontal) {
                HStack(alignment: .top, spacing: 24) {
                    fields
                }
                .fixedSize(horizontal: true, vertical: false)

                VStack(alignment: .leading, spacing: 12) {
                    fields
                }
            }
        }
    }

    @ViewBuilder private var fields: some View {
        field("Recent Codex activity", value: activity)
        if !source.isEmpty {
            field("Quota source", value: source)
        }
    }

    private func field(_ label: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.system(size: 14, weight: .semibold))
            Text(value)
                .font(.system(size: 15))
                .foregroundStyle(Graphite.muted)
                .fixedSize(horizontal: false, vertical: true)
        }
        .accessibilityElement(children: .combine)
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
                .font(.system(size: 16, weight: .medium))
                .foregroundStyle(Graphite.muted)
                .multilineTextAlignment(alignment == .trailing ? .trailing : .leading)
                .fixedSize(horizontal: false, vertical: true)
            Text(percent)
                .font(.system(size: size, weight: .bold).monospacedDigit())
                .tracking(-size * 0.035)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .accessibilityElement(children: .combine)
    }
}

private struct WeeklyQuotaContent: View {
    let quota: QuotaSample
    let pace: Pace?
    let shortQuota: QuotaSample?
    let targetDisplay: WeeklyTargetDisplay
    let manualResetTarget: Date?
    let usesManualResetTarget: Bool
    let demoNow: Date?
    let changeResetTarget: (Date?) -> Bool
    let width: CGFloat
    let now: Date
    @ViewState private var detailsExpanded = false

    private var action: PaceAction { .weekly(pace: pace, quota: quota, now: now) }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            PaceHeroLayout(width: width) {
                readings
            } details: {
                guidance
            }
            GraphiteDivider()
            DisclosureGroup(isExpanded: $detailsExpanded) {
                details.padding(.top, 12)
            } label: {
                Text("Weekly pacing details")
                    .font(.system(size: 16, weight: .medium))
            }
            .disclosureGroupStyle(FullRowDisclosureStyle())
        }
    }

    private var guidance: some View {
        HeroGuidancePanel(action: action) {
            switch action {
            case .keepPace, .roomToSpeedUp:
                Text("Your recent pace fits your remaining weekly quota.")
            case .slowDown:
                if let exhaustionDate = pace?.exhaustionDate {
                    Text("At this pace, you could reach your weekly limit \(DashboardFormat.dateTime(exhaustionDate)).")
                } else {
                    Text("At this pace, you could reach your weekly limit before reset.")
                }
            case .pause:
                Text("You’ve reached your weekly quota limit.")
            case .checkUsage:
                Text("Refresh to see your current weekly pace.")
            case .awaitingReadings:
                Text("Waiting for more usage readings during work hours.")
            case .offHours:
                Text("No work hours are scheduled before your next reset.")
            }
        }
    }

    private var readings: some View {
        let plannedTarget = DashboardFormat.weeklyTarget(for: pace)
        let progress = QuotaProgress(used: quota.usedPercent / 100, target: plannedTarget.map { $0 / 100 },
                                     showsRemaining: targetDisplay == .left)
        let actual = targetDisplay.percent(forUsed: quota.usedPercent)
        let actualText = DashboardFormat.percent(actual)
        let targetText = plannedTarget.map { DashboardFormat.percent(targetDisplay.percent(forUsed: $0)) }
        let status = "\(actualText) \(targetDisplay.rawValue)"
        let other = targetDisplay.complement

        return VStack(alignment: .leading, spacing: 13) {
            HStack(alignment: .bottom, spacing: 10) {
                QuotaReading(label: "Weekly quota \(targetDisplay.rawValue)", percent: actualText, size: 54)
                Spacer(minLength: 0)
                if let targetText {
                    QuotaReading(label: "Target \(targetDisplay.rawValue) by now", percent: targetText,
                                 size: 36, alignment: .trailing)
                }
            }

            QuotaUsageBar(progress: progress, thickness: 10, weeklyAction: action)
                .accessibilityRepresentation {
                    ProgressView(value: min(100, max(0, actual)), total: 100)
                        .accessibilityLabel("Weekly allowance \(targetDisplay.rawValue)")
                        .accessibilityValue(targetText.map { "\(status), \($0) target by now" } ?? status)
                }

            HeroQuotaNote(amount: "\(DashboardFormat.percent(other.percent(forUsed: quota.usedPercent))) of weekly quota",
                          detail: other == .used ? "used so far" : "remaining")
        }
    }

    private var details: some View {
        VStack(alignment: .leading, spacing: 13) {
            if let pace {
                LazyVGrid(columns: [GridItem(.flexible(), spacing: 10), GridItem(.flexible())],
                          alignment: .leading, spacing: 10) {
                    Metric(label: "Recent pace", value: DashboardFormat.rate(pace.recentRate))
                    Metric(label: "Safe pace", value: DashboardFormat.rate(pace.safeRate))
                    Metric(label: usesManualResetTarget ? "Projected use by target" : "Projected weekly use",
                           value: DashboardFormat.percent(pace.projectedPercent))
                    Metric(label: usesManualResetTarget ? "Work time to target" : "Work time left",
                           value: DashboardFormat.hours(pace.remainingWorkHours))
                }
                GraphiteDivider()
            }

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

private struct FullRowDisclosureStyle: DisclosureGroupStyle {
    func makeBody(configuration: Configuration) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Button {
                configuration.isExpanded.toggle()
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: configuration.isExpanded ? "chevron.down" : "chevron.right")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(Graphite.muted)
                        .frame(width: 12)
                        .accessibilityHidden(true)
                    configuration.label
                    Spacer(minLength: 0)
                }
                .frame(maxWidth: .infinity, minHeight: 32, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityValue(configuration.isExpanded ? "Expanded" : "Collapsed")
            if configuration.isExpanded {
                configuration.content
            }
        }
    }
}

private struct ResetTargetDetails: View {
    let accountReset: Date
    let manualTarget: Date?
    let usesManualTarget: Bool
    let demoNow: Date?
    let changeTarget: (Date?) -> Bool
    @StateObject private var draft = ResetTargetDraft()

    var body: some View {
        let now = demoNow ?? Date()
        let canEdit = manualTarget != nil || ResetTargetDraft.nextMinute(after: now) < accountReset

        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .center, spacing: 14) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Scheduled reset")
                        .font(.system(size: 16))
                        .foregroundStyle(Graphite.muted)
                    Text(DashboardFormat.dateTime(accountReset))
                        .font(.system(size: 16, weight: .semibold))
                        .monospacedDigit()
                }
                .fixedSize(horizontal: false, vertical: true)

                Spacer(minLength: 0)

                Button(manualTarget == nil ? "Set reset target" : "Edit reset target") {
                    let now = demoNow ?? Date()
                    draft.begin(accountReset: accountReset, manualTarget: manualTarget, now: now)
                }
                .graphiteButton(.secondary)
                .fixedSize()
                .disabled(!canEdit)
                .help(canEdit ? "Plan around an earlier manual reset" : "The quota window has ended or closes too soon to set a target")
            }

            if let manualTarget {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Manual reset target · \(DashboardFormat.dateTime(manualTarget))")
                        .fontWeight(.medium)
                    if manualTarget <= now {
                        Text("Target passed. Pacing uses the scheduled account reset.")
                    } else if usesManualTarget {
                        Text("Pacing uses this target.")
                    } else if manualTarget >= accountReset {
                        Text("Target saved. Pacing uses the scheduled account reset.")
                    }
                }
                .font(.system(size: 16))
                .fixedSize(horizontal: false, vertical: true)
            }
        }
        .sheet(isPresented: $draft.isPresented) {
            ResetTargetEditor(draft: draft, accountReset: accountReset, demoNow: demoNow, changeTarget: changeTarget)
        }
    }
}

private final class ResetTargetDraft: ObservableObject {
    @Published var isPresented = false
    @Published var selection = Date.distantFuture
    @Published var windowChanged = false
    private(set) var hasTarget = false

    func begin(accountReset: Date, manualTarget: Date?, now: Date) {
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
    let accountReset: Date
    let demoNow: Date?
    let changeTarget: (Date?) -> Bool

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
                Text("Use your manual reset in Codex by this time. Pace keeps your chosen date across usage refreshes.")
                    .foregroundStyle(Graphite.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Text("Scheduled account reset · \(DashboardFormat.dateTime(accountReset))")
                .font(.callout)

            DatePicker("Date and time", selection: $draft.selection,
                       displayedComponents: [.date, .hourAndMinute])
                .datePickerStyle(.field)
                .graphiteField()
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
                        .graphiteButton(.danger)
                }
                Spacer()
                Button("Cancel") { draft.isPresented = false }
                    .graphiteButton(.quiet)
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    guard validationMessage(now: demoNow ?? Date()) == nil else { return }
                    commit(draft.target)
                }
                .graphiteButton(.primary)
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
        if draft.target >= accountReset { return "Choose a time before the scheduled account reset." }
        return nil
    }

    private func commit(_ target: Date?) {
        if changeTarget(target) {
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
            .font(.system(size: 16))
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
    private struct PlanDay: Identifiable {
        let date: Date
        let templates: [SessionTemplate]
        let sessions: [UUID: WorkSession]

        var id: Date { date }
    }

    let pace: Pace
    let schedule: WorkSchedule
    let quota: QuotaSample?
    let now: Date
    @Binding var targetDisplay: WeeklyTargetDisplay
    private let workdayColumnWidth: CGFloat = 112
    private let sessionColumnWidth: CGFloat = 165

    private var days: [PlanDay] {
        let calendar = DashboardFormat.calendar
        let byDay = Dictionary(grouping: pace.sessions) { calendar.startOfDay(for: $0.start) }
        return byDay.keys.sorted().compactMap { date in
            guard let day = Workday(rawValue: calendar.component(.weekday, from: date)) else { return nil }
            let sessions = Dictionary(uniqueKeysWithValues: (byDay[date] ?? []).map { ($0.template.id, $0) })
            return PlanDay(date: date, templates: schedule.orderedSessions(on: day), sessions: sessions)
        }
    }

    var body: some View {
        Panel {
            VStack(alignment: .leading, spacing: 18) {
                HStack {
                    Text("Work sessions")
                        .font(.system(size: 19, weight: .semibold))
                        .accessibilityAddTraits(.isHeader)
                    Spacer()
                    GraphiteSegments("Work sessions quota view", selection: $targetDisplay, options: [
                        ("Used", .used), ("Left", .left),
                    ])
                    .help("Changes the quota display throughout Codex Pace")
                }

                if pace.sessions.isEmpty {
                    Text("The quota window contains no scheduled work hours.")
                        .font(.callout)
                        .foregroundStyle(Graphite.muted)
                } else {
                    let days = days
                    let count = days.map { $0.templates.count }.max() ?? 0
                    let sharedTemplates = days.first.map(\.templates).flatMap { first in
                        days.allSatisfy { $0.templates == first } ? first : nil
                    }
                    let table = sessionTable(days, columnCount: count, sharedTemplates: sharedTemplates)
                    ViewThatFits(in: .horizontal) {
                        table
                        ScrollView(.horizontal) {
                            table.fixedSize(horizontal: true, vertical: false)
                        }
                    }
                }
            }
        }
    }

    private func sessionTable(_ days: [PlanDay], columnCount: Int, sharedTemplates: [SessionTemplate]?) -> some View {
        Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 6) {
            GridRow(alignment: .top) {
                columnHeader("Workday", minimumWidth: workdayColumnWidth, inset: 0)
                    .frame(width: workdayColumnWidth, alignment: .leading)
                ForEach(0..<columnCount, id: \.self) { index in
                    if let template = sharedTemplates?[index] {
                        let name = template.name.trimmingCharacters(in: .whitespacesAndNewlines)
                        let time = scheduledRange(template)
                        columnHeader(name, time: time, minimumWidth: sessionColumnWidth)
                            .help("\(name) · \(time)")
                    } else {
                        columnHeader("Session \(index + 1)", minimumWidth: sessionColumnWidth)
                    }
                }
            }
            GraphiteDivider()
                .gridCellUnsizedAxes(.horizontal)
            ForEach(days) { day in
                GridRow(alignment: .top) {
                    workdayLabel(day.date)
                        .padding(.vertical, 10)
                        .frame(width: workdayColumnWidth, alignment: .leading)
                    ForEach(0..<columnCount, id: \.self) { index in
                        allocation(for: day, at: index, namesInHeader: sharedTemplates != nil)
                            .frame(minWidth: sessionColumnWidth, maxWidth: .infinity, alignment: .leading)
                    }
                }
                if day.id != days.last?.id {
                    GraphiteDivider()
                        .gridCellUnsizedAxes(.horizontal)
                }
            }
        }
        .font(.system(size: 16))
    }

    private func workdayLabel(_ date: Date) -> some View {
        let format = Date.FormatStyle(locale: DashboardFormat.locale, timeZone: DashboardFormat.timeZone)
        return VStack(alignment: .leading, spacing: 2) {
            Text(date.formatted(format.weekday(.abbreviated)))
                .font(.system(size: 16, weight: .semibold))
            Text(date.formatted(format.day().month(.abbreviated)))
                .font(.system(size: 14))
                .foregroundStyle(Graphite.muted)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(DashboardFormat.day(date))
    }

    private func columnHeader(_ title: String, time: String? = nil, minimumWidth: CGFloat, inset: CGFloat = 10) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(.system(size: 16, weight: .semibold))
                .fixedSize(horizontal: false, vertical: true)
            if let time {
                Text(time)
                    .font(.system(size: 14).monospacedDigit())
                    .foregroundStyle(Graphite.muted)
            }
        }
        .padding(.leading, inset)
        .frame(minWidth: minimumWidth, maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isHeader)
    }

    private func scheduledRange(_ template: SessionTemplate) -> String {
        DashboardFormat.timeRange(start: template.startMinute, end: template.endMinute)
    }

    @ViewBuilder
    private func allocation(for day: PlanDay, at index: Int, namesInHeader: Bool) -> some View {
        if !day.templates.indices.contains(index) {
            Text("—")
                .padding(.vertical, 10)
                .foregroundStyle(Graphite.muted)
                .accessibilityLabel("No session scheduled")
        } else if let session = day.sessions[day.templates[index].id] {
            sessionAllocation(session, namesInHeader: namesInHeader)
        } else {
            Text("—")
                .padding(.vertical, 10)
                .foregroundStyle(Graphite.muted)
                .accessibilityLabel("Outside this quota window")
        }
    }

    private func sessionAllocation(_ session: WorkSession, namesInHeader: Bool) -> some View {
        let isCurrent = session.id == pace.currentSession?.id
        let comparison = isCurrent ? shiftEndComparison(for: session, quota: quota, now: now) : nil
        let target = DashboardFormat.percent(targetDisplay.percent(for: session))

        return VStack(alignment: .leading, spacing: 5) {
            if !namesInHeader {
                Text(DashboardFormat.sessionName(session))
                    .font(.system(size: 16, weight: .semibold))
            }
            if let comparison {
                activeComparison(target: target, comparison: comparison)
            } else {
                quotaValue("Target \(targetDisplay.rawValue)", value: target)
                if isCurrent {
                    Text("Now")
                        .font(.system(size: 14, weight: .medium))
                        .foregroundStyle(Graphite.muted)
                }
            }
            let range = DashboardFormat.timeRange(session)
            if !namesInHeader || range != scheduledRange(session.template) {
                Text(range)
                    .font(.system(size: 14).monospacedDigit())
                    .foregroundStyle(Graphite.muted)
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(isCurrent ? Graphite.selection : .clear, in: RoundedRectangle(cornerRadius: 8))
        .help("\(DashboardFormat.percent(session.targetPercent)) session share")
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(allocationLabel(session, comparison: comparison))
    }

    private func activeComparison(target: String, comparison: ShiftEndComparison) -> some View {
        let actual = DashboardFormat.percent(targetDisplay.percent(for: comparison))
        let color = comparison.isStale ? Graphite.muted : comparison.roomToTarget < 0 ? Graphite.risk : Graphite.onPace
        return VStack(alignment: .leading, spacing: 6) {
            ViewThatFits(in: .horizontal) {
                HStack(alignment: .top, spacing: 16) {
                    quotaValue("Target \(targetDisplay.rawValue)", value: target)
                    quotaValue("Actual \(targetDisplay.rawValue)", value: actual)
                }
                .fixedSize(horizontal: true, vertical: false)
                VStack(alignment: .leading, spacing: 6) {
                    quotaValue("Target \(targetDisplay.rawValue)", value: target)
                    quotaValue("Actual \(targetDisplay.rawValue)", value: actual)
                }
            }
            Text("\(comparison.isStale ? lastReading : roomDescription(comparison.roomToTarget)) · Now")
                .font(.system(size: 14))
                .foregroundStyle(color)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func quotaValue(_ label: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label)
                .font(.system(size: 14))
                .foregroundStyle(Graphite.muted)
            Text(value)
                .font(.system(size: 23, weight: .semibold).monospacedDigit())
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
        return rounded > 0 ? "\(amount) available" : "\(amount) over target"
    }

    private func allocationLabel(_ session: WorkSession, comparison: ShiftEndComparison?) -> String {
        let allocation = DashboardFormat.percent(session.targetPercent)
        let target = DashboardFormat.percent(targetDisplay.percent(for: session))
        let current = session.id == pace.currentSession?.id ? ", current session" : ""
        let range = DashboardFormat.timeRange(session)
        let name = DashboardFormat.sessionName(session)
        var label = "\(name), planned weekly quota \(targetDisplay.rawValue) by session end \(target), \(range), \(allocation) session share\(current)"
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
                .font(.system(size: 16))
                .foregroundStyle(Graphite.muted)
            Text(value)
                .font(.system(size: 18, weight: .semibold).monospacedDigit())
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
        value.map {
            // Hide the minus sign when the percentage rounds to zero.
            let amount = (-QuotaProgress.zeroDisplayThreshold...0).contains($0) ? 0 : $0
            return (amount / 100).formatted(.percent.precision(.fractionLength(0 ... 1)))
        } ?? "—"
    }

    static func carryoverCredit(_ progress: QuotaProgress?) -> String? {
        progress.flatMap(\.carryoverCredit).map { percent(100 * $0) }
    }

    static func weeklyTarget(for pace: Pace?) -> Double? {
        pace.flatMap { $0.sessions.isEmpty ? nil : $0.targetPercent }
    }

    static func rate(_ value: Double?) -> String {
        value.map { "\(percent($0)) per work hour" } ?? "—"
    }

    static func hours(_ value: Double) -> String {
        "\(value.formatted(.number.precision(.fractionLength(0 ... 1)))) h"
    }
}
