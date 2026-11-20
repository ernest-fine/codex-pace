using System.Text.Json;
using CodexPace;

internal static class FastModeChecks
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

    public static void ComparesEffortAndSpeedCosts()
    {
        Equal("Use Astra · Ultra · Standard", Calculate(used: 70, deadlineHour: 13).Recommendation);
        Equal("Use Astra · Ultra · Standard", Calculate(used: 70, deadlineHour: 13, fastTradeoffCostRatio: "1").Recommendation);
        Equal("Use Astra · Ultra · Standard", Calculate(used: 70, deadlineHour: 13, fastTradeoffCostRatio: "1.01").Recommendation);
        Equal("Use Astra · Max · Fast", Calculate(used: 70, deadlineHour: 13, fastTradeoffCostRatio: " 0.99 ").Recommendation);

        var skipsMax = Astra with { Efforts = ["ultra", "high", "medium"] };
        Equal("Use Astra · High · Fast", Calculate(used: 70, deadlineHour: 13, available: [skipsMax],
            fastTradeoffCostRatio: "0.9").Recommendation);
    }

    public static void DefaultsInvalidEstimatesToEqualCost()
    {
        foreach (string invalid in new[] { "unknown", "0", "-1", "NaN", "Infinity", "0,9" })
            Equal("Use Astra · Ultra · Standard", Calculate(used: 70, deadlineHour: 13, fastTradeoffCostRatio: invalid).Recommendation);
    }

    public static void KeepsFastWhenThereIsNoComparableEffort()
    {
        var onlyUltra = Astra with { Efforts = ["ultra"], DefaultEffort = "ultra" };
        Equal("Use Astra · Ultra · Fast", Calculate(used: 70, deadlineHour: 13, available: [onlyUltra]).Recommendation);

        var unknown = Astra with { Efforts = ["adaptive"], DefaultEffort = "adaptive" };
        Equal("Use Astra · Adaptive · Fast", Calculate(used: 70, deadlineHour: 13, available: [unknown]).Recommendation);
    }

    public static void UsesQuotaThroughTheResetTarget()
    {
        var manual = Calculate(used: 51.7, deadlineHour: 13, previousUsed: 51.2);
        if (manual.Pace?.SessionRoomToTarget is not < 0 || manual.Warning is null)
            throw new InvalidOperationException("The fixture should exceed the session slice before the manual reset.");
        Equal("Use Astra · Ultra · Standard", manual.Recommendation);
        Equal(true, manual.HasModelRecommendation);

        var scheduled = Calculate(used: 51.7, previousUsed: 51.2);
        Equal("Use Sol · High · Standard", scheduled.Recommendation);
    }

    public static void IgnoresRecentModelEffortAndWorkload()
    {
        var heavy = new Activity(Now, "gpt-6.2-sol", "max", 8, 800_000);
        var light = new Activity(Now, Luna.Id, "low", 1, 3_000);
        const string expected = "Use Astra · Ultra · Standard";
        Equal(expected, Calculate(used: 51.7, deadlineHour: 13, activity: heavy).Recommendation);
        Equal(expected, Calculate(used: 51.7, deadlineHour: 13, activity: light).Recommendation);
        Equal(expected, Calculate(used: 51.7, deadlineHour: 13).Recommendation);
    }

    public static void RequiresBothKindsOfHeadroomForFast()
    {
        Equal("Use Astra · Max · Fast", Calculate(used: 70, deadlineHour: 13, fastTradeoffCostRatio: "0.9").Recommendation);
        Equal("Use Astra · Ultra · Standard", Calculate(used: 71, deadlineHour: 12, fastTradeoffCostRatio: "0.9").Recommendation);
        Equal("Use Astra · Ultra · Standard", Calculate(used: 60, deadlineHour: 14, fastTradeoffCostRatio: "0.9").Recommendation);
        Equal("Use Astra · Max · Standard", Calculate(used: 75, deadlineHour: 14).Recommendation);
        Equal("Use Luna · Low · Standard", Calculate(used: 98, deadlineHour: 13).Recommendation);
    }

    public static void RespectsAvailableModelSettings()
    {
        var limited = Astra with { Efforts = ["low", "max"], SupportsFastMode = false };
        var result = Calculate(used: 50, deadlineHour: 13, available: [limited], fastTradeoffCostRatio: "0.9");
        Equal("Use Astra · Max", result.Recommendation);
        Equal(true, result.HasModelRecommendation);

        var unknownFast = limited with { SupportsFastMode = null };
        Equal("Use Astra · Max", Calculate(used: 50, deadlineHour: 13, available: [unknownFast],
            fastTradeoffCostRatio: "0.9").Recommendation);
    }

    public static void SkipsUnavailableOrExhaustedQuota()
    {
        Equal(false, Calculate(used: 25, deadlineHour: 13, observedAt: Now.AddMinutes(-46)).HasModelRecommendation);
        Equal(false, Calculate(used: 100, deadlineHour: 13).HasModelRecommendation);
        Equal(false, Calculate(used: 25, deadlineHour: 13, available: []).HasModelRecommendation);
        Equal(false, Calculate(used: 25, deadlineHour: 13, workdays: []).HasModelRecommendation);
    }

    private static DashboardData Calculate(double used, int? deadlineHour = null, double? previousUsed = null,
        DateTimeOffset? observedAt = null, Activity? activity = null, IReadOnlyList<ModelOption>? available = null,
        DayOfWeek[]? workdays = null, string fastTradeoffCostRatio = "")
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
        return Planner.Calculate(telemetry, history, preferences, Now, fastTradeoffCostRatio);
    }

    private static ModelOption Model(bool? supportsFast) =>
        new("gpt-6.2-sol", "Sol", ["low", "medium", "high", "max"], "medium", true, supportsFast);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
