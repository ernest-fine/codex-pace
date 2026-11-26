import AppKit
import SwiftUI

struct MenuBarSummary {
    let period: String
    let progress: QuotaProgress?
    var weeklyAction: PaceAction?

    private var direction: String { progress?.showsRemaining == true ? "left" : "used" }

    var label: String? {
        guard let progress, progress.displayedAmount.isFinite else { return nil }
        let percent = FloatingPointFormatStyle<Double>.Percent.percent.precision(.fractionLength(0))
        if let credit = progress.carryoverCredit {
            return "+\(credit.formatted(percent))"
        }
        let amount = max(0, progress.displayedAmount)
        let overBudget = !progress.showsRemaining && amount > 1
        let reading = overBudget ? 1 : amount
        let suffix = overBudget ? "+" : ""
        if let target = progress.displayedTarget, target.isFinite {
            let wholeNumber = FloatingPointFormatStyle<Double>.number.grouping(.never).precision(.fractionLength(0))
            return "\((reading * 100).formatted(wholeNumber))\(suffix)/\((target * 100).formatted(wholeNumber))"
        }
        return "\(reading.formatted(percent))\(suffix)"
    }

    var title: String {
        guard let progress else { return period }
        let actual = DashboardFormat.percent(100 * progress.displayedAmount)
        let reading = DashboardFormat.carryoverCredit(progress).map { "\($0) carryover credit · \(actual) net used" }
            ?? "\(actual) \(direction)"
        var parts = [period, reading]
        if let target = progress.displayedTarget {
            parts.append("Target \(DashboardFormat.percent(100 * target)) \(direction) by now")
        }
        if weeklyAction == .checkUsage { parts.append("Last reading") }
        return parts.joined(separator: " · ")
    }

    static func make(data: DashboardData, now: Date, focus: HeroFocus, display: WeeklyTargetDisplay) -> Self {
        let now = data.isDemo ? data.quota?.observedAt ?? now : now
        let showsRemaining = display == .left

        if focus == .weekly {
            guard let quota = data.quota else { return Self(period: "Weekly quota", progress: nil) }
            let action = PaceAction.weekly(pace: data.pace, quota: quota, now: now)
            return Self(period: "Weekly quota", progress: QuotaProgress(
                used: quota.usedPercent / 100, target: DashboardFormat.weeklyTarget(for: data.pace).map { $0 / 100 },
                showsRemaining: showsRemaining
            ), weeklyAction: action)
        }

        guard let pace = data.pace, let session = pace.currentSession,
              session.start <= now, now < session.end else {
            return Self(period: "Off hours", progress: nil)
        }
        let period = DashboardFormat.sessionName(session)
        guard data.guidance?.action != .checkUsage, let progress = pace.sessionProgress(showsRemaining: showsRemaining) else {
            return Self(period: period, progress: nil)
        }
        return Self(period: period, progress: progress)
    }

    @MainActor
    func image(dark: Bool) -> NSImage {
        guard let progress, let label else {
            return NSImage(systemSymbolName: "gauge.with.dots.needle.50percent", accessibilityDescription: title)
                ?? NSImage(size: NSSize(width: 18, height: 18))
        }

        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)!
        let size = NSSize(width: 42, height: 22)
        let text = label as NSString
        let font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        let unscaledWidth = text.size(withAttributes: [.font: font]).width
        let fittedFont = unscaledWidth > size.width
            ? NSFont.monospacedDigitSystemFont(ofSize: 12 * size.width / unscaledWidth, weight: .regular) : font
        let attributes: [NSAttributedString.Key: Any] = [.font: fittedFont, .foregroundColor: NSColor.labelColor]
        let textSize = text.size(withAttributes: attributes)

        let image = NSImage(size: size, flipped: false) { rect in
            appearance.performAsCurrentDrawingAppearance {
                let textX = (rect.width - textSize.width) / 2
                text.draw(at: NSPoint(x: textX, y: rect.height - textSize.height), withAttributes: attributes)

                let track = NSRect(x: 0, y: 2, width: rect.width, height: 3)
                NSColor(Graphite.track).setFill()
                NSBezierPath(roundedRect: track, xRadius: 1.5, yRadius: 1.5).fill()

                let fillWidth = track.width * progress.barFraction
                if fillWidth > 0 {
                    let fill = NSRect(x: track.minX, y: track.minY, width: fillWidth, height: track.height)
                    let radius = min(1.5, fillWidth / 2)
                    NSColor(progress.fillColor(weeklyAction: weeklyAction)).setFill()
                    NSBezierPath(roundedRect: fill, xRadius: radius, yRadius: radius).fill()
                }
                if let target = progress.markerFraction {
                    let position = min(track.width - 1, max(0, track.width * target - 0.5))
                    NSColor(Graphite.ink).setFill()
                    NSRect(x: position, y: 1, width: 1, height: 5).fill()
                }
            }
            return true
        }
        image.isTemplate = false
        return image
    }
}

struct MenuBarStatusLabel: View {
    let summary: MenuBarSummary
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        Image(nsImage: summary.image(dark: colorScheme == .dark))
            .renderingMode(summary.label == nil ? .template : .original)
            .accessibilityLabel("Codex Ticker, \(summary.title)")
    }
}
