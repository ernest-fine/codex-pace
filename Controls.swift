import AppKit
import SwiftUI

typealias ViewState<Value> = State<Value>

enum Graphite {
    static let surface = adaptive(0xeeefed, 0x151716)
    static let panel = adaptive(0xfafbf9, 0x1d201e)
    static let ink = adaptive(0x252826, 0xf3f4f1)
    static let muted = adaptive(0x5d645f, 0xbac2bd)
    static let line = adaptive(0xdce1dc, 0x3b443e)
    static let selection = adaptive(0xe6ebe6, 0x353e37)
    static let hero = adaptive(0xfdfefc, 0x222723)
    static let inset = adaptive(0xedf0eb, 0x2e3630)
    static let track = adaptive(0xe8ece8, 0x48554e)
    static let quotaCredit = adaptive(0x4e7ca9, 0xa3c9ed)
    static let quotaUsage = adaptive(0x468464, 0xa0d5af)
    static let onPace = adaptive(0x287653, 0x8ac99e)
    static let caution = adaptive(0x946217, 0xe2ba73)
    static let risk = adaptive(0xa93846, 0xefa5a9)
    static let field = adaptive(0xf2f4f0, 0x292f2a)
    static let primary = adaptive(0x263b2d, 0xd7eadb)
    static let primaryInk = adaptive(0xffffff, 0x17241a)
    static let focus = adaptive(0x37774e, 0xa3c9a8)

    private static func adaptive(_ light: UInt32, _ dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let rgb = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: CGFloat((rgb >> 16) & 255) / 255,
                           green: CGFloat((rgb >> 8) & 255) / 255,
                           blue: CGFloat(rgb & 255) / 255, alpha: 1)
        })
    }
}

extension QuotaProgress {
    var fillColor: Color {
        if carryoverCredit != nil { return Graphite.quotaCredit }
        return switch level {
        case .unknown: Graphite.muted
        case .withinTarget: Graphite.quotaUsage
        case .slightlyOverTarget: Graphite.caution
        case .overTarget: Graphite.risk
        }
    }
}

enum GraphiteButtonRole {
    case primary, secondary, quiet, danger

    fileprivate var text: Color {
        switch self {
        case .primary: Graphite.primaryInk
        case .danger: Graphite.risk
        case .secondary, .quiet: Graphite.ink
        }
    }

    fileprivate var border: Color {
        self == .secondary ? Graphite.line : .clear
    }

    fileprivate func fill(highlighted: Bool) -> Color {
        if self == .primary {
            return Graphite.primary.opacity(highlighted ? 0.85 : 1)
        }
        if highlighted {
            return self == .danger ? Graphite.risk.opacity(0.08) : Graphite.selection
        }
        return self == .secondary ? Graphite.hero : .clear
    }
}

extension View {
    func graphiteButton(_ role: GraphiteButtonRole) -> some View {
        buttonStyle(GraphiteButtonStyle(role: role))
    }

    func graphiteField() -> some View {
        textFieldStyle(.plain)
            .modifier(GraphiteFieldChrome())
    }
}

private struct GraphiteButtonStyle: ButtonStyle {
    let role: GraphiteButtonRole

    func makeBody(configuration: Configuration) -> some View {
        Label(role: role, configuration: configuration)
    }

    private struct Label: View {
        let role: GraphiteButtonRole
        let configuration: Configuration
        @Environment(\.isEnabled) private var isEnabled
        @Environment(\.isFocused) private var isFocused
        @ViewState private var isHovered = false

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: 8)
            let highlighted = isEnabled && (isHovered || configuration.isPressed)
            configuration.label
                .font(.callout.weight(.semibold))
                .foregroundStyle(role.text)
                .padding(.horizontal, 10)
                .frame(minHeight: 32)
                .background(role.fill(highlighted: highlighted), in: shape)
                .overlay {
                    shape.strokeBorder(isFocused ? Graphite.focus : role.border, lineWidth: isFocused ? 2 : 1)
                        .allowsHitTesting(false)
                }
                .opacity(isEnabled ? (configuration.isPressed ? 0.8 : 1) : 0.48)
                .contentShape(.rect)
                .onHover { isHovered = $0 }
        }
    }
}

struct GraphiteSegments<Value: Hashable>: View {
    private let label: String
    @Binding private var selection: Value
    private let options: [(String, Value)]
    @FocusState private var focusedOption: Value?
    @Environment(\.layoutDirection) private var layoutDirection

    init(_ label: String, selection: Binding<Value>, options: [(String, Value)]) {
        self.label = label
        _selection = selection
        self.options = options
    }

    var body: some View {
        HStack(spacing: 2) {
            ForEach(options.indices, id: \.self) { index in
                let (title, value) = options[index]
                Button(title) { selection = value }
                    .buttonStyle(SegmentStyle(selected: selection == value, focused: focusedOption == value))
                    .focused($focusedOption, equals: value)
                    .accessibilityAddTraits(selection == value ? .isSelected : [])
            }
        }
        .padding(3)
        .background(Graphite.field, in: RoundedRectangle(cornerRadius: 9))
        .overlay {
            RoundedRectangle(cornerRadius: 9).strokeBorder(Graphite.line)
                .allowsHitTesting(false)
        }
        .fixedSize(horizontal: true, vertical: false)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(label)
        .onMoveCommand { direction in
            guard let focusedOption, let index = options.firstIndex(where: { $0.1 == focusedOption }) else { return }
            let forward: Bool
            switch direction {
            case .left: forward = layoutDirection == .rightToLeft
            case .right: forward = layoutDirection == .leftToRight
            default: return
            }
            let next = index + (forward ? 1 : -1)
            guard options.indices.contains(next) else { return }
            selection = options[next].1
            self.focusedOption = selection
        }
    }

    private struct SegmentStyle: ButtonStyle {
        let selected: Bool
        let focused: Bool

        func makeBody(configuration: Configuration) -> some View {
            let shape = RoundedRectangle(cornerRadius: 6)
            configuration.label
                .font(.callout.weight(selected ? .semibold : .medium))
                .foregroundStyle(selected ? Graphite.ink : Graphite.muted)
                .padding(.horizontal, 9)
                .frame(minHeight: 24)
                .background(selected ? Graphite.hero : configuration.isPressed ? Graphite.selection : .clear, in: shape)
                .overlay {
                    shape.strokeBorder(focused ? Graphite.focus : selected ? Graphite.line : .clear,
                                       lineWidth: focused ? 2 : 1)
                        .allowsHitTesting(false)
                }
                .contentShape(.rect)
        }
    }
}

private struct GraphiteFieldChrome: ViewModifier {
    @FocusState private var isFocused: Bool

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: 7)
        content
            .focused($isFocused)
            .font(.callout)
            .padding(.horizontal, 8)
            .frame(minHeight: 34)
            .background(Graphite.field, in: shape)
            .overlay {
                shape.strokeBorder(isFocused ? Graphite.focus : Graphite.line, lineWidth: isFocused ? 2 : 1)
                    .allowsHitTesting(false)
            }
    }
}

extension PaceAction {
    func pillColors(dark: Bool) -> (background: NSColor, text: NSColor) {
        let colors: (UInt32, UInt32) = switch (self, dark) {
        case (.keepPace, false), (.roomToSpeedUp, false): (0xddeee2, 0x205c39)
        case (.keepPace, true), (.roomToSpeedUp, true): (0x254931, 0xc5ecd0)
        case (.slowDown, false): (0xffebbd, 0x6b4600)
        case (.slowDown, true): (0x604817, 0xffe4a7)
        case (.pause, false): (0xfbdce0, 0x8c2434)
        case (.pause, true): (0x642d37, 0xffd0d8)
        case (_, false): (0xe5e7e5, 0x404740)
        case (_, true): (0x3a413b, 0xe0e6e1)
        }
        func color(_ rgb: UInt32) -> NSColor {
            NSColor(srgbRed: CGFloat((rgb >> 16) & 255) / 255, green: CGFloat((rgb >> 8) & 255) / 255,
                    blue: CGFloat(rgb & 255) / 255, alpha: 1)
        }
        return (color(colors.0), color(colors.1))
    }
}

struct PaceStatusPill: View {
    let action: PaceAction
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let colors = action.pillColors(dark: colorScheme == .dark)
        HStack(spacing: 7) {
            Circle()
                .frame(width: 7, height: 7)
                .accessibilityHidden(true)
            Text(action.title)
                .font(.system(size: 16, weight: .semibold))
        }
        .foregroundStyle(Color(nsColor: colors.text))
        .padding(.horizontal, 11)
        .padding(.vertical, 4)
        .background(Color(nsColor: colors.background), in: Capsule())
    }
}

struct GraphiteTimeField: View {
    private let label: String
    @Binding private var minutes: Int

    init(_ label: String, minutes: Binding<Int>) {
        self.label = label
        _minutes = minutes
    }

    var body: some View {
        ClockEditor(label: label, minutes: $minutes)
            .fixedSize()
            .modifier(GraphiteFieldChrome())
    }
}

private struct ClockEditor: NSViewRepresentable {
    let label: String
    @Binding var minutes: Int
    @Environment(\.isEnabled) private var isEnabled

    // Use the Mac's time zone so NSDatePicker and accessibility agree on the displayed time.
    // The reference day keeps the Date representation separate from the saved wall-clock minutes.
    private static var clockCalendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        return calendar
    }

    func makeNSView(context: Context) -> NSDatePicker {
        let picker = NSDatePicker()
        picker.datePickerStyle = .textField
        picker.datePickerElements = [.hourMinute]
        let calendar = Self.clockCalendar
        picker.calendar = calendar
        picker.timeZone = calendar.timeZone
        picker.isBezeled = false
        picker.isBordered = false
        picker.drawsBackground = false
        picker.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        picker.textColor = .labelColor
        picker.isContinuous = true
        picker.target = context.coordinator
        picker.action = #selector(Coordinator.updateMinutes(_:))
        picker.setAccessibilityLabel(label)
        picker.dateValue = Self.referenceDate(for: minutes, calendar: calendar)
        return picker
    }

    func updateNSView(_ picker: NSDatePicker, context: Context) {
        context.coordinator.minutes = $minutes
        picker.isEnabled = isEnabled
        let calendar = Self.clockCalendar
        let clockChanged = picker.timeZone != calendar.timeZone
        if clockChanged {
            picker.calendar = calendar
            picker.timeZone = calendar.timeZone
        }
        if clockChanged || Self.minutes(from: picker.dateValue, calendar: calendar) != minutes {
            picker.dateValue = Self.referenceDate(for: minutes, calendar: calendar)
        }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: NSDatePicker, context: Context) -> CGSize? {
        let size = nsView.cell?.cellSize ?? nsView.fittingSize
        return CGSize(width: max(52, ceil(size.width)), height: max(18, ceil(size.height)))
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(minutes: $minutes)
    }

    private static func referenceDate(for minutes: Int, calendar: Calendar) -> Date {
        calendar.date(from: DateComponents(year: 2001, month: 1, day: 15, hour: minutes / 60, minute: minutes % 60))!
    }

    private static func minutes(from date: Date, calendar: Calendar) -> Int {
        let time = calendar.dateComponents([.hour, .minute], from: date)
        return (time.hour ?? 0) * 60 + (time.minute ?? 0)
    }

    final class Coordinator: NSObject {
        var minutes: Binding<Int>

        init(minutes: Binding<Int>) {
            self.minutes = minutes
        }

        @objc func updateMinutes(_ picker: NSDatePicker) {
            minutes.wrappedValue = ClockEditor.minutes(from: picker.dateValue, calendar: picker.calendar ?? ClockEditor.clockCalendar)
        }
    }
}
