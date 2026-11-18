using System.Globalization;
using System.Text.Json.Serialization;

namespace CodexPace;

internal sealed record QuotaSample(DateTimeOffset ObservedAt, DateTimeOffset ResetsAt, int WindowMinutes, double UsedPercent);
internal sealed record ModelOption(string Id, string Name, IReadOnlyList<string> Efforts, string? DefaultEffort, bool IsDefault);
internal sealed record Activity(DateTimeOffset ObservedAt, string? Model, string? Effort, int Turns, long Tokens);
internal sealed record Telemetry(
    QuotaSample? Quota, QuotaSample? ShortQuota, IReadOnlyList<QuotaSample> History,
    Activity? Activity, IReadOnlyList<ModelOption> Models, string Source, string? Error);

internal sealed record SessionTemplate(Guid Id, string Name, int StartMinute, int EndMinute);

internal sealed record WorkSchedule(int MorningStart, int MorningEnd, int AfternoonStart, int AfternoonEnd)
{
    public static Guid MorningId { get; } = new("00000000-0000-4000-8000-000000000001");
    public static Guid AfternoonId { get; } = new("00000000-0000-4000-8000-000000000002");
    public static WorkSchedule Standard { get; } = new(570, 750, 840, 1095);

    // Keep the original fields for reading schedules saved before named sessions were available.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionTemplate[]? Sessions { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<DayOfWeek, SessionTemplate[]>? DaySessions { get; init; }

    [JsonIgnore]
    public IReadOnlyList<SessionTemplate> Entries => Sessions ??
        [new(MorningId, "Morning", MorningStart, MorningEnd), new(AfternoonId, "Afternoon", AfternoonStart, AfternoonEnd)];

    public static WorkSchedule FromSessions(IEnumerable<SessionTemplate> entries) =>
        Standard with { Sessions = entries.ToArray() };

    public IReadOnlyList<SessionTemplate> EntriesOn(DayOfWeek day) =>
        DaySessions is not null && DaySessions.TryGetValue(day, out var entries) && entries is not null
            ? entries : Entries;

    public WorkSchedule WithDaySessions(DayOfWeek day, IEnumerable<SessionTemplate> entries)
    {
        if (!Enum.IsDefined(day)) throw new ArgumentOutOfRangeException(nameof(day));
        var updated = DaySessions is null
            ? new Dictionary<DayOfWeek, SessionTemplate[]>()
            : new Dictionary<DayOfWeek, SessionTemplate[]>(DaySessions);
        // Retain an empty override so clearing one day doesn't restore the shared sessions.
        updated[day] = entries.ToArray();
        return this with { DaySessions = updated };
    }

    [JsonIgnore]
    public bool IsValid => AreValid(Entries) && (DaySessions is null || DaySessions.All(pair =>
        Enum.IsDefined(pair.Key) && pair.Value is not null && AreValid(pair.Value)));

    private static bool AreValid(IReadOnlyList<SessionTemplate> entries)
    {
        var ids = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name) || !ids.Add(entry.Id) ||
                entry.StartMinute < 0 || entry.StartMinute >= entry.EndMinute || entry.EndMinute >= 1440)
                return false;
        }

        int previousEnd = 0;
        foreach (var entry in entries.OrderBy(entry => entry.StartMinute))
        {
            if (entry.StartMinute < previousEnd) return false;
            previousEnd = entry.EndMinute;
        }

        return true;
    }
}

internal sealed record ManualResetTarget(DateTimeOffset TargetAt, DateTimeOffset WindowResetsAt);

internal sealed record Preferences
{
    public static IReadOnlyList<DayOfWeek> StandardWorkdays { get; } = Array.AsReadOnly<DayOfWeek>(
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);

    public WorkSchedule Schedule { get; init; } = WorkSchedule.Standard;
    public DayOfWeek[] Workdays { get; init; } = [.. StandardWorkdays];
    public ManualResetTarget? ResetTarget { get; init; }
    public bool ShowUsed { get; init; }
}

internal enum SessionAction { CheckUsage, AwaitingReadings, Pause, SlowDown, KeepPace, RoomToSpeedUp }
internal enum SessionPaceStatus { AwaitingReadings, OnTrack, OverPace, BudgetUsed }
internal sealed record SessionGuidance(SessionAction Action, double? SafeSessionPercentPerHour, bool WeeklyRisk);
internal sealed record WorkSession(DateTimeOffset Start, DateTimeOffset End, double TargetPercent,
    double CumulativeTargetPercent, SessionTemplate Template)
{
    private const int MenuLabelLimit = 20;

    public string MenuLabel
    {
        get
        {
            string name = Template.Name.Trim();
            if (Template.Id == WorkSchedule.MorningId && name == "Morning") return "AM";
            if (Template.Id == WorkSchedule.AfternoonId && name == "Afternoon") return "PM";
            if (name.Length <= MenuLabelLimit) return name;

            int cutoff = Array.FindLast(StringInfo.ParseCombiningCharacters(name), start => start < MenuLabelLimit);
            return name[..cutoff].TrimEnd() + "…";
        }
    }
}

internal sealed record Pace(
    DateTimeOffset PlanEndsAt, IReadOnlyList<WorkSession> Sessions,
    WorkSession? CurrentSession, double TargetPercent, double RemainingWorkHours, double? SafeRate,
    double? RecentRate, double? ProjectedPercent, DateTimeOffset? ExhaustionDate, double? SessionRoomToTarget,
    double? SessionProjectedRoomToTarget, double? SessionBudget, double? SessionTarget, double? SessionUsed,
    bool SessionStartInferred, DateTimeOffset? PartialSince, SessionPaceStatus? SessionStatus);

internal sealed record BudgetWarning(string Key, string Title, string Recommendation, string Detail, DateTimeOffset ExpiresAt);

internal sealed record DashboardData(
    QuotaSample? Quota, QuotaSample? ShortQuota, Pace? Pace, SessionGuidance? Guidance, string Status,
    string Recommendation, string RecommendationDetail, string RecentActivity, string Source, string? Error,
    BudgetWarning? Warning);

internal static class QuotaDisplay
{
    public static (double Fraction, string Percent)? SessionUsage(Pace pace)
    {
        if (pace.SessionBudget is not double budget || !double.IsFinite(budget) || budget <= 0
            || pace.SessionUsed is not double used || used < 0)
            return null;

        var fraction = used / budget;
        var percent = fraction * 100;
        return double.IsFinite(percent) ? (fraction, Percent(percent)) : null;
    }

    public static string OrientedPercent(double used, bool showUsed) => Percent(showUsed ? used : Math.Max(0, 100 - used));

    public static string Percent(double? amount) =>
        amount?.ToString("0.#", CultureInfo.CurrentCulture) is { } formatted ? formatted + "%" : "—";
}

internal static class WorkClock
{
    public static DateTimeOffset Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, TimeZoneInfo.Local);
    public static DateOnly Day(DateTimeOffset time) => DateOnly.FromDateTime(Local(time).DateTime);
    public static DateTimeOffset At(DateOnly day, int minutes) =>
        FromLocal(day.ToDateTime(TimeOnly.MinValue).AddMinutes(minutes));

    public static DateTimeOffset FromLocal(DateTime time)
    {
        var zone = TimeZoneInfo.Local;
        var wallTime = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
        // Use the first valid minute after a clock gap and the first occurrence of repeated times.
        while (zone.IsInvalidTime(wallTime)) wallTime = wallTime.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(wallTime)
            ? zone.GetAmbiguousTimeOffsets(wallTime).Max()
            : zone.GetUtcOffset(wallTime);
        return new DateTimeOffset(wallTime, offset);
    }
}
