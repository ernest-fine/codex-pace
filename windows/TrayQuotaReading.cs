using System.Globalization;

namespace CodexPace;

internal enum TrayQuotaTone { Unknown, WithinTarget, SlightlyOverTarget, OverTarget }

internal sealed record TrayQuotaReading(
    string Title, string Caption, string Tooltip, string? Actual, string? Target,
    double? Amount, double? Marker, TrayQuotaTone Tone, string? Note)
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(45);

    public static TrayQuotaReading Make(DashboardData data, DateTimeOffset now, bool showWeekly, bool showUsed)
    {
        if (showWeekly)
        {
            const string period = "Weekly quota";
            if (data.Quota is not { } quota || !ValidAmount(quota.UsedPercent) || quota.ObservedAt > now)
                return Unavailable(period, "Waiting for a current quota reading");

            bool stale = !IsFresh(quota, now);
            double? target = data.Pace is { Sessions.Count: > 0 } pace && double.IsFinite(pace.TargetPercent)
                ? pace.TargetPercent : null;
            string? note = stale ? $"Last reading {Stamp(quota.ObservedAt)}" : null;
            return Reading(period, quota.UsedPercent, target, showUsed, stale, note);
        }

        if (data.Pace is not { } sessionPace || sessionPace.CurrentSession is not { } session ||
            now < session.Start || now >= session.End)
            return Unavailable("Off hours", "No active work session");

        string name = session.Template.Name.Trim();
        if (data.Quota is not { } current || !IsFresh(current, now) ||
            data.Guidance?.Action == SessionAction.CheckUsage)
        {
            string note = data.Quota is { } last && last.ObservedAt <= now
                ? $"Last reading {Stamp(last.ObservedAt)} · Refresh for current session usage"
                : "Waiting for a current quota reading";
            return Unavailable(name, note);
        }
        if (QuotaDisplay.SessionUsage(sessionPace) is not { } usage)
            return Unavailable(name, "Session usage unavailable");

        double? sessionTarget = sessionPace.SessionTarget is { } amount && sessionPace.SessionBudget is { } budget
            ? amount / budget * 100 : null;
        if (sessionTarget is { } planned && !double.IsFinite(planned)) sessionTarget = null;
        string? estimate = sessionPace.SessionStartInferred ? "Estimated session usage" : null;
        return Reading(name, usage.Fraction * 100, sessionTarget, showUsed, false, estimate);
    }

    private static TrayQuotaReading Reading(
        string period, double used, double? target, bool showUsed, bool stale, string? note)
    {
        string kind = showUsed ? "used" : "left";
        string actualText = QuotaDisplay.OrientedPercent(used, showUsed);
        string? targetText = target is { } planned ? QuotaDisplay.OrientedPercent(planned, showUsed) : null;
        string figures = targetText is null ? actualText : $"{actualText} / {targetText}";
        string caption = $"{figures} {kind}";
        string tooltip = $"{period} · {actualText} {kind}";
        if (targetText is not null) tooltip += $" · Target {targetText} {kind} by now";
        if (note is not null) tooltip += $" · {note}";
        double amount = Math.Clamp((showUsed ? used : 100 - used) / 100, 0, 1);
        double? marker = target is { } targetAmount
            ? Math.Clamp((showUsed ? targetAmount : 100 - targetAmount) / 100, 0, 1) : null;
        var tone = ToneFor(used, target, stale);
        return new TrayQuotaReading(period, caption, tooltip, actualText, targetText, amount, marker, tone, note);
    }

    private static TrayQuotaTone ToneFor(double used, double? target, bool stale)
    {
        if (stale || target is not { } planned) return TrayQuotaTone.Unknown;
        if (used <= planned) return TrayQuotaTone.WithinTarget;
        return used <= planned * 1.10 ? TrayQuotaTone.SlightlyOverTarget : TrayQuotaTone.OverTarget;
    }

    private static TrayQuotaReading Unavailable(string period, string note) =>
        new(period, "Codex Pace", $"{period} · {note}", null, null, null, null, TrayQuotaTone.Unknown, note);

    private static bool ValidAmount(double amount) => double.IsFinite(amount) && amount is >= 0 and <= 100;

    private static bool IsFresh(QuotaSample quota, DateTimeOffset now) =>
        ValidAmount(quota.UsedPercent) && quota.ResetsAt > now && quota.ObservedAt <= now &&
        now - quota.ObservedAt <= Freshness;

    private static string Stamp(DateTimeOffset instant) =>
        WorkClock.Local(instant).ToString("ddd, d MMM HH:mm", CultureInfo.CurrentCulture);
}
