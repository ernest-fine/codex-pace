import AppKit
import SwiftUI

struct MenuBarSummary {
    let period: String
    let progress: QuotaProgress?
    var isStale = false

    private var direction: String { progress?.showsRemaining == true ? "left" : "used" }

    var label: String {
        guard let progress else { return "Codex Pace" }
        if let credit = DashboardFormat.carryoverCredit(progress) {
            return "\(credit) credit"
        }
        let actual = DashboardFormat.percent(100 * progress.displayedAmount)
        let figures = progress.displayedTarget.map {
            "\(actual) / \(DashboardFormat.percent(100 * $0))"
        } ?? actual
        return "\(figures) \(direction)"
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
        if isStale { parts.append("Last reading") }
        return parts.joined(separator: " · ")
    }

    static func make(data: DashboardData, now: Date, focus: HeroFocus, display: WeeklyTargetDisplay) -> Self {
        let now = data.isDemo ? data.quota?.observedAt ?? now : now
        let showsRemaining = display == .left

        if focus == .weekly {
            guard let quota = data.quota else { return Self(period: "Weekly quota", progress: nil) }
            let isStale = PaceAction.weekly(pace: data.pace, quota: quota, now: now) == .checkUsage
            return Self(period: "Weekly quota", progress: QuotaProgress(
                used: quota.usedPercent / 100, target: DashboardFormat.weeklyTarget(for: data.pace).map { $0 / 100 },
                showsRemaining: showsRemaining
            ), isStale: isStale)
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
        let font = NSFont.menuBarFont(ofSize: 12)
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)!
        let text = label as NSString
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.labelColor]
        let textSize = text.size(withAttributes: attributes)
        let size = NSSize(width: ceil(textSize.width), height: 22)

        let image = NSImage(size: size, flipped: false) { rect in
            appearance.performAsCurrentDrawingAppearance {
                let textY = progress == nil ? (rect.height - textSize.height) / 2 : rect.height - textSize.height
                text.draw(at: NSPoint(x: 0, y: textY), withAttributes: attributes)
                if let progress {
                    let track = NSRect(x: 0, y: 2, width: rect.width, height: 3)
                    NSColor(Graphite.track).setFill()
                    NSBezierPath(roundedRect: track, xRadius: 1.5, yRadius: 1.5).fill()

                    let fillWidth = track.width * progress.barFraction
                    if fillWidth > 0 {
                        let fill = NSRect(x: track.minX, y: track.minY, width: fillWidth, height: track.height)
                        let radius = min(1.5, fillWidth / 2)
                        NSColor(isStale ? Graphite.muted : progress.fillColor).setFill()
                        NSBezierPath(roundedRect: fill, xRadius: radius, yRadius: radius).fill()
                    }
                    if let target = progress.markerFraction {
                        let position = min(track.width - 1, max(0, track.width * target - 0.5))
                        NSColor.labelColor.setFill()
                        NSRect(x: position, y: 1, width: 1, height: 5).fill()
                    }
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
            .renderingMode(.original)
            .accessibilityLabel(summary.title)
    }
}
