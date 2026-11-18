using System.Globalization;

namespace CodexPace;

internal sealed record SessionQuotaDelta(double UsedPercentagePoints, double LeftPercentagePoints)
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(45);

    public static SessionQuotaDelta? Calculate(
        QuotaSample? quota, Pace? pace, IReadOnlyList<QuotaSample> history, DateTimeOffset now)
    {
        if (quota is null || pace?.CurrentSession is not { } session
            || now < session.Start || now >= session.End || quota.ResetsAt <= now
            || pace.SessionBudget is not double budget || !double.IsFinite(budget) || budget <= 0
            || pace.SessionUsed is not double usedAmount
            || QuotaDisplay.SessionUsage(pace) is not { } usage
            || !ValidReading(quota, session, now)) return null;

        var previous = history.Where(sample => sample is not null
                && sample.ResetsAt == quota.ResetsAt && sample.WindowMinutes == quota.WindowMinutes
                && sample.ObservedAt < quota.ObservedAt && ValidReading(sample, session, now))
            .MaxBy(sample => sample.ObservedAt);
        if (previous is null || quota.UsedPercent < previous.UsedPercent) return null;

        double used = usage.Fraction * 100;
        double previousUsed = Math.Max(0, usedAmount - (quota.UsedPercent - previous.UsedPercent)) * 100 / budget;
        double usedChange = used - previousUsed;
        double leftChange = Math.Max(0, 100 - used) - Math.Max(0, 100 - previousUsed);
        return double.IsFinite(usedChange) && double.IsFinite(leftChange)
            ? new SessionQuotaDelta(usedChange, leftChange) : null;
    }

    public string? Caption(bool showUsed)
    {
        double amount = Math.Round(showUsed ? UsedPercentagePoints : LeftPercentagePoints, 1, MidpointRounding.AwayFromZero);
        if (amount == 0) return null;
        string value = Math.Abs(amount).ToString("0.#", CultureInfo.CurrentCulture);
        string sign = amount > 0 ? "+" : "−";
        return $"{sign}{value}% of session quota since last reading";
    }

    private static bool ValidReading(QuotaSample sample, WorkSession session, DateTimeOffset now) =>
        sample.UsedPercent is >= 0 and <= 100
        && sample.ObservedAt >= session.Start && sample.ObservedAt < session.End
        && sample.ObservedAt <= now && now - sample.ObservedAt <= Freshness;
}
