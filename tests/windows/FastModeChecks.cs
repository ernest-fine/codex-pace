using System.Text.Json;
using CodexPace;

internal static class FastModeChecks
{
    private static readonly DateTimeOffset Now = WorkClock.At(new DateOnly(2026, 11, 18), 15 * 60);
    private static readonly DateTimeOffset Reset = WorkClock.At(new DateOnly(2026, 11, 23), 21 * 60 + 15);

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

    public static void SuggestsFastWithQuotaHeadroom()
    {
        var data = Calculate(used: 25, turns: 3);
        Equal(true, data.HasSpeedRecommendation);
        Contains(data.Recommendation, "GPT-6.1 Sol · High · Fast");
        Equal<BudgetWarning?>(null, data.Warning);
    }

    public static void SuggestsStandardUnderPressure()
    {
        var warning = Calculate(used: 39, turns: 3);
        Equal(true, warning.HasSpeedRecommendation);
        Contains(warning.Recommendation, "· Standard");
        if (warning.Warning is null) throw new InvalidOperationException("The session fixture should be over pace.");

        var busy = Calculate(used: 25, turns: 5);
        Equal(true, busy.HasSpeedRecommendation);
        Contains(busy.Recommendation, "· Standard");
    }

    public static void OmitsUnverifiedOrStaleAdvice()
    {
        Equal(false, Calculate(used: 25, turns: 3, supportsFast: null).HasSpeedRecommendation);
        Equal(false, Calculate(used: 25, turns: 3, supportsFast: false).HasSpeedRecommendation);
        Equal(false, Calculate(used: 25, turns: 0).HasSpeedRecommendation);
        Equal(false, Calculate(used: 25, turns: 3, observedAt: Now.AddMinutes(-46)).HasSpeedRecommendation);
    }

    private static DashboardData Calculate(double used, int turns, bool? supportsFast = true, DateTimeOffset? observedAt = null)
    {
        var quota = new QuotaSample(observedAt ?? Now, Reset, 10080, used);
        var before = new QuotaSample(Now.AddHours(-1), Reset, 10080, used - 1);
        var history = new[] { before, quota };
        var activity = new Activity(Now, "gpt-6.1-sol", "high", turns, 150_000);
        var telemetry = new Telemetry(quota, null, history, activity, [Model(supportsFast)], "Codex account", null);
        return Planner.Calculate(telemetry, history, new Preferences(), Now);
    }

    private static ModelOption Model(bool? supportsFast) =>
        new("gpt-6.1-sol", "GPT-6.1 Sol", ["low", "medium", "high"], "medium", true, supportsFast);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void Contains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{expected}' in '{value}'.");
    }
}
