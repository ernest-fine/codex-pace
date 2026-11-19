import SwiftUI

@MainActor
final class ScheduleDraft: ObservableObject {
    enum ApplyScope: Hashable {
        case editedDays, entireWeek
    }

    enum SessionList: Hashable {
        case day(Workday)
        case week
    }

    @Published var day: Workday = .monday
    @Published private(set) var applyScope: ApplyScope = .editedDays
    @Published private var editedSchedule: WorkSchedule = .standard
    @Published private var sharedSessions: [SessionTemplate]?

    var sessionList: SessionList { applyScope == .entireWeek ? .week : .day(day) }
    var sessions: [SessionTemplate] { sessions(in: sessionList) }
    var canAddSession: Bool { nextTimeSlot != nil }

    var scheduleToSave: WorkSchedule {
        applyScope == .entireWeek ? WorkSchedule(sessions: sessions) : editedSchedule
    }

    func begin(_ schedule: WorkSchedule, workdays: Set<Workday>) {
        editedSchedule = schedule
        sharedSessions = nil
        applyScope = .editedDays
        let weekday = Workday(rawValue: WorkClock.calendar.component(.weekday, from: .now))
        day = weekday.flatMap { workdays.contains($0) ? $0 : nil }
            ?? Workday.allCases.first(where: workdays.contains) ?? weekday ?? .monday
    }

    func selectScope(_ scope: ApplyScope) {
        guard applyScope != scope else { return }
        if scope == .entireWeek && sharedSessions == nil {
            sharedSessions = editedSchedule.sessions(on: day)
        }
        applyScope = scope
    }

    func sessions(in list: SessionList) -> [SessionTemplate] {
        switch list {
        case let .day(day): editedSchedule.sessions(on: day)
        case .week: sharedSessions ?? []
        }
    }

    func addSession() -> UUID? {
        guard let slot = nextTimeSlot else { return nil }
        let session = SessionTemplate(
            id: UUID(), name: "Session \(sessions.count + 1)", startMinute: slot.start, endMinute: slot.end
        )
        replaceSessions(sessions + [session], in: sessionList)
        return session.id
    }

    func removeSession(_ id: UUID, from list: SessionList) {
        replaceSessions(sessions(in: list).filter { $0.id != id }, in: list)
    }

    func applyTemplate(_ template: SavedSessionTemplate) {
        replaceSessions(template.sessions, in: sessionList)
    }

    func setSessionField<Value>(
        _ keyPath: WritableKeyPath<SessionTemplate, Value>, to value: Value, id: UUID, in list: SessionList
    ) {
        var values = sessions(in: list)
        guard let index = values.firstIndex(where: { $0.id == id }) else { return }
        values[index][keyPath: keyPath] = value
        replaceSessions(values, in: list)
    }

    private func replaceSessions(_ sessions: [SessionTemplate], in list: SessionList) {
        switch list {
        case let .day(day): editedSchedule.setSessions(sessions, on: day)
        case .week: sharedSessions = sessions
        }
    }

    private var nextTimeSlot: (start: Int, end: Int)? {
        let sessions = sessions.sorted { $0.startMinute < $1.startMinute }
        if sessions.isEmpty { return (9 * 60 + 30, 10 * 60 + 30) }

        let lastMinute = 24 * 60 - 1
        var next = 0
        var lastGap: Range<Int>?
        for session in sessions {
            if next < session.startMinute { lastGap = next..<session.startMinute }
            next = max(next, session.endMinute)
        }
        if next < lastMinute { return (next, min(next + 60, lastMinute)) }
        guard let lastGap else { return nil }
        return (lastGap.lowerBound, min(lastGap.lowerBound + 60, lastGap.upperBound))
    }
}

struct ScheduleEditor: View {
    @ObservedObject var draft: ScheduleDraft
    @Binding var sessionTemplates: [SavedSessionTemplate]
    let workdays: Set<Workday>
    let cancel: () -> Void
    let save: (WorkSchedule) -> Void
    @FocusState private var focusedName: UUID?
    @ViewState private var addedSession: UUID?
    @ViewState private var fieldsHeight: CGFloat?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Edit sessions")
                .font(.system(size: 16, weight: .semibold))
                .accessibilityAddTraits(.isHeader)

            scopeControls

            if draft.applyScope == .editedDays {
                dayBar
                if !workdays.contains(draft.day) {
                    Text("\(draft.day.title) is off in Work days.")
                        .font(.caption)
                        .foregroundStyle(Graphite.muted)
                }
            }

            if draft.sessions.isEmpty {
                Text("No sessions")
                    .font(.callout)
                    .foregroundStyle(Graphite.muted)
            } else {
                ScrollViewReader { scroll in
                    ScrollView {
                        sessionFields(in: draft.sessionList)
                            .padding(3)
                            .onGeometryChange(for: CGFloat.self) { geometry in
                                geometry.size.height
                            } action: { height in
                                fieldsHeight = height
                            }
                    }
                    .frame(height: min(256, fieldsHeight ?? 256))
                    .scrollBounceBehavior(.basedOnSize)
                    .onChange(of: addedSession, initial: true) { _, added in
                        guard let added else { return }
                        scroll.scrollTo(added, anchor: .bottom)
                        focusedName = added
                        addedSession = nil
                    }
                }
                .id(draft.sessionList)
            }

            HStack(spacing: 8) {
                Button {
                    addedSession = draft.addSession()
                } label: {
                    Label("Add session", systemImage: "plus")
                }
                .graphiteButton(.quiet)
                .disabled(!draft.canAddSession)

                Spacer(minLength: 0)

                SessionTemplateControls(templates: $sessionTemplates, sessions: draft.sessions) { template in
                    focusedName = nil
                    draft.applyTemplate(template)
                }
            }

            if draft.applyScope == .entireWeek {
                Text(draft.sessions.isEmpty ? "Clears sessions for all days." : "Replaces schedules for all days.")
                    .font(.caption)
                    .foregroundStyle(Graphite.muted)
            }

            let scheduleToSave = draft.scheduleToSave
            if !scheduleToSave.isValid {
                Text(validationMessage(for: scheduleToSave))
                    .font(.callout)
                    .foregroundStyle(Graphite.risk)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Divider()
            HStack(spacing: 8) {
                Spacer(minLength: 0)
                Button("Cancel", action: cancel)
                    .graphiteButton(.quiet)
                    .keyboardShortcut(.cancelAction)
                Button(draft.applyScope == .entireWeek ? "Apply to entire week" : "Save schedule", action: saveSchedule)
                    .graphiteButton(.primary)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!scheduleToSave.isValid)
            }
        }
    }

    private var scopeControls: some View {
        HStack(spacing: 10) {
            Text("Apply to")
                .font(.caption)
                .foregroundStyle(Graphite.muted)
                .accessibilityHidden(true)
            GraphiteSegments(
                "Apply to",
                selection: Binding(
                    get: { draft.applyScope },
                    set: { scope in
                        focusedName = nil
                        draft.selectScope(scope)
                    }
                ),
                options: [("Edited days", ScheduleDraft.ApplyScope.editedDays), ("Entire week", .entireWeek)]
            )
        }
    }

    private var dayBar: some View {
        HStack(spacing: 4) {
            ForEach(Workday.allCases, id: \.self) { day in
                let selected = draft.day == day
                let isWorkday = workdays.contains(day)
                Button {
                    focusedName = nil
                    draft.day = day
                } label: {
                    Text(String(day.title.prefix(3)))
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(ScheduleDayButtonStyle(selected: selected, isWorkday: isWorkday))
                .accessibilityLabel(isWorkday ? day.title : "\(day.title), off")
                .accessibilityAddTraits(selected ? .isSelected : [])
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Day")
    }

    private func sessionFields(in list: ScheduleDraft.SessionList) -> some View {
        Grid(alignment: .leading, horizontalSpacing: 8, verticalSpacing: 7) {
            GridRow {
                Text("Session name")
                Text("Start")
                Text("End")
                Color.clear.frame(width: 24, height: 1)
                    .accessibilityHidden(true)
            }
            .font(.caption)
            .foregroundStyle(Graphite.muted)

            ForEach(draft.sessions(in: list)) { session in
                GridRow {
                    TextField("Session name", text: field(session, \.name, in: list))
                        .graphiteField()
                        .frame(minWidth: 65, maxWidth: .infinity)
                        .focused($focusedName, equals: session.id)
                        .onSubmit(saveSchedule)
                    GraphiteTimeField("Start time", minutes: field(session, \.startMinute, in: list))
                    GraphiteTimeField("End time", minutes: field(session, \.endMinute, in: list))
                    Button {
                        draft.removeSession(session.id, from: list)
                    } label: {
                        Image(systemName: "trash")
                            .font(.caption)
                    }
                    .graphiteButton(.danger)
                    .accessibilityLabel(session.name.isEmpty ? "Remove session" : "Remove \(session.name)")
                }
                .id(session.id)
            }
        }
    }

    private func field<Value>(
        _ session: SessionTemplate, _ keyPath: WritableKeyPath<SessionTemplate, Value>, in list: ScheduleDraft.SessionList
    ) -> Binding<Value> {
        Binding(
            get: {
                draft.sessions(in: list).first { $0.id == session.id }?[keyPath: keyPath] ?? session[keyPath: keyPath]
            },
            set: { draft.setSessionField(keyPath, to: $0, id: session.id, in: list) }
        )
    }

    private func validationMessage(for schedule: WorkSchedule) -> String {
        let rules = "Give each session a name and valid hours that don’t overlap."
        guard draft.applyScope == .editedDays, !schedule.invalidDays.isEmpty else { return rules }
        let days = schedule.invalidDays.map(\.title).formatted(.list(type: .and))
        return "Check \(days). \(rules)"
    }

    private func saveSchedule() {
        let schedule = draft.scheduleToSave
        guard schedule.isValid else { return }
        save(schedule)
    }
}

private struct SessionTemplateControls: View {
    @Binding var templates: [SavedSessionTemplate]
    let sessions: [SessionTemplate]
    let apply: (SavedSessionTemplate) -> Void
    @ViewState private var isNaming = false
    @ViewState private var name = ""
    @FocusState private var nameFocused: Bool

    var body: some View {
        Button("Save as template…") {
            name = ""
            isNaming = true
        }
        .graphiteButton(.quiet)
        .disabled(!SavedSessionTemplate.canSave(sessions))
        .popover(isPresented: $isNaming, arrowEdge: .top) {
            nameForm
        }

        Menu("Apply template") {
            ForEach(templates, id: \.name) { template in
                Button(template.name) { apply(template) }
            }
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .disabled(templates.isEmpty)
    }

    private var nameExists: Bool {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        return templates.contains { $0.name.caseInsensitiveCompare(trimmed) == .orderedSame }
    }

    private var nameForm: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Save as template")
                .font(.headline)
            TextField("Template name", text: $name)
                .graphiteField()
                .focused($nameFocused)
                .onSubmit(saveTemplate)
            if nameExists {
                Text("A template with this name already exists.")
                    .font(.caption)
                    .foregroundStyle(Graphite.risk)
            }
            HStack {
                Spacer()
                Button("Cancel") { isNaming = false }
                    .graphiteButton(.quiet)
                Button("Save template", action: saveTemplate)
                    .graphiteButton(.primary)
                    .disabled(nameExists || name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(14)
        .frame(width: 285)
        .background(Graphite.panel)
        .foregroundStyle(Graphite.ink)
        .onAppear { nameFocused = true }
    }

    private func saveTemplate() {
        guard !nameExists, let template = SavedSessionTemplate(name: name, sessions: sessions) else { return }
        templates.append(template)
        isNaming = false
    }
}

private struct ScheduleDayButtonStyle: ButtonStyle {
    let selected: Bool
    let isWorkday: Bool
    @Environment(\.isFocused) private var isFocused

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.caption.weight(selected ? .semibold : .regular))
            .foregroundStyle(isWorkday || selected ? Graphite.ink : Graphite.muted)
            .frame(height: 33)
            .background(selected || configuration.isPressed ? Graphite.selection : .clear, in: RoundedRectangle(cornerRadius: 7))
            .overlay {
                RoundedRectangle(cornerRadius: 7)
                    .strokeBorder(isFocused ? Graphite.focus : selected ? Graphite.line : .clear)
                    .allowsHitTesting(false)
            }
            .contentShape(Rectangle())
    }
}
