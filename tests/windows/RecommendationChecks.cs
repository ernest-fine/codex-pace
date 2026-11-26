using System.Text.Json;
using CodexTicker;

internal static class RecommendationChecks
{
    private static readonly DateOnly Monday = new(2026, 11, 16);
    private static readonly DateTimeOffset Now = WorkClock.At(Monday, 10 * 60 + 30);
    private static readonly DateTimeOffset Reset = WorkClock.At(Monday.AddDays(7), 0);
    private static readonly ModelOption Astra = new("gpt-6.2-astra", "Astra",
        ["low", "medium", "high", "max", "ultra"], "low", false, true);
    private static readonly ModelOption Luna = new("gpt-6-luna", "Luna",
        ["low", "medium", "high"], "medium", false, true);

    public static void ReadsExplicitModelCapabilities()
    {
        foreach (string json in new[]
                 {
                     """{"service_tiers":[{"id":"priority","name":"Fast"}]}""",
                     """{"serviceTiers":[{"id":"fast"}]}""",
                     """{"additional_speed_tiers":["fast"]}""",
                     """{"service_tiers":[],"additional_speed_tiers":["fast"]}"""
                 })
        {
            using var document = JsonDocument.Parse(json);
            Equal<bool?>(true, ModelSpeedCapabilities.Read(document.RootElement));
        }
        using var unsupported = JsonDocument.Parse("""{"service_tiers":[]}""");
        Equal<bool?>(false, ModelSpeedCapabilities.Read(unsupported.RootElement));
        using var absent = JsonDocument.Parse("""{"name":"Fast sounding model"}""");
        Equal<bool?>(null, ModelSpeedCapabilities.Read(absent.RootElement));

        var cache = Model(supportsFast: true);
        var unknown = Model(supportsFast: null);
        var denied = Model(supportsFast: false);
        Equal<bool?>(true, ModelSpeedCapabilities.Merge([unknown], [cache]).Single().SupportsFastMode);
        Equal<bool?>(false, ModelSpeedCapabilities.Merge([denied], [cache]).Single().SupportsFastMode);
        Equal<bool?>(null, ModelSpeedCapabilities.Merge([unknown], [cache with { Id = "another-model" }]).Single().SupportsFastMode);
    }

    public static void SlowsDownWithPacingAdvice()
    {
        var result = Calculate(used: 34, deadlineHour: 14, previousUsed: 25);
        Equal<SessionAction?>(SessionAction.SlowDown, result.Guidance?.Action);
        Equal<bool?>(false, result.Guidance?.WeeklyRisk);
        Equal("Run fewer turns", result.Recommendation);
        Equal(true, result.ShowsRecommendation);
        Equal(result.Recommendation, result.Warning?.Recommendation);
        Equal(true, result.RecommendationDetail.Contains($"Target by now is {QuotaDisplay.Percent(50)}."));
    }

    public static void ReservesQuotaForUrgentWork()
    {
        var result = Calculate(used: 34, deadlineHour: 14, previousUsed: 20);
        Equal<SessionAction?>(SessionAction.SlowDown, result.Guidance?.Action);
        Equal<bool?>(true, result.Guidance?.WeeklyRisk);
        Equal("Use Codex for urgent work only", result.Recommendation);
        Equal<bool?>(true, result.Warning?.Key.StartsWith("weekly.", StringComparison.Ordinal));
    }

    public static void PacingAdviceIsIndependentOfTheModel()
    {
        var sol = new Activity(Now, "gpt-6.2-sol", "low", 1, 3_000);
        var routine = Calculate(used: 34, deadlineHour: 14, previousUsed: 25, activity: sol);
        var noCatalog = Calculate(used: 34, deadlineHour: 14, previousUsed: 25, available: []);
        Equal(routine.Recommendation, noCatalog.Recommendation);
        Equal(true, routine.RecentActivity.Contains("Sol · Low"));

        var luna = new Activity(Now, Luna.Id, "low", 1, 3_000);
        Equal("Use Codex for urgent work only", Calculate(used: 34, deadlineHour: 14, previousUsed: 20, activity: luna).Recommendation);
    }

    public static void PausesWhenTheSessionBudgetIsUsed()
    {
        var manual = Calculate(used: 51.7, deadlineHour: 13, previousUsed: 25);
        Equal<SessionAction?>(SessionAction.Pause, manual.Guidance?.Action);
        Equal("Pause non-urgent turns until your next session", manual.Recommendation);
        Equal(true, manual.ShowsRecommendation);
        Equal<bool?>(true, manual.Warning?.Key.StartsWith("session.", StringComparison.Ordinal));
    }

    public static void KeepsPaceWhenOnTrack()
    {
        var result = Calculate(used: 29, deadlineHour: 14, previousUsed: 25);
        Equal(true, result.Guidance?.Action is SessionAction.KeepPace or SessionAction.RoomToSpeedUp);
        Equal("Keep this pace", result.Recommendation);
        Equal(true, result.ShowsRecommendation);
    }

    public static void SkipsUnavailableOrExhaustedQuota()
    {
        var stale = Calculate(used: 25, deadlineHour: 13, observedAt: Now.AddMinutes(-46));
        Equal(false, stale.ShowsRecommendation);
        Equal("Refresh to check your pace", stale.Recommendation);
        var spent = Calculate(used: 100, deadlineHour: 13);
        Equal(true, spent.ShowsRecommendation);
        Equal(true, spent.Recommendation.StartsWith("Weekly quota resets ", StringComparison.Ordinal));
        Equal(false, Calculate(used: 25, deadlineHour: 13, workdays: []).ShowsRecommendation);
    }

    private static DashboardData Calculate(double used, int? deadlineHour = null, double? previousUsed = null,
        DateTimeOffset? observedAt = null, Activity? activity = null, IReadOnlyList<ModelOption>? available = null,
        DayOfWeek[]? workdays = null)
    {
        var quota = new QuotaSample(observedAt ?? Now, Reset, 10080, used);
        QuotaSample[] history = previousUsed is { } before
            ? [new QuotaSample(WorkClock.At(Monday, 10 * 60), Reset, 10080, before), quota]
            : [quota];
        var telemetry = new Telemetry(quota, null, history, activity, available ?? [Astra, Model(true), Luna],
            "Codex account", null);
        var preferences = new Preferences
        {
            Schedule = WorkSchedule.FromSessions(Enumerable.Range(9, 5).Select(hour =>
                new SessionTemplate(Guid.NewGuid(), $"Work {hour}", hour * 60, (hour + 1) * 60))),
            Workdays = workdays ?? [.. Preferences.StandardWorkdays],
            ResetTarget = deadlineHour is { } resetHour ? new(WorkClock.At(Monday, resetHour * 60), Reset) : null
        };
        return Planner.Calculate(telemetry, history, preferences, Now);
    }

    private static ModelOption Model(bool? supportsFast) =>
        new("gpt-6.2-sol", "Sol", ["low", "medium", "high", "max"], "medium", true, supportsFast);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
