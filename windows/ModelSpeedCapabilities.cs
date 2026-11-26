using System.Text.Json;

namespace CodexTicker;

internal static class ModelSpeedCapabilities
{
    public static bool? Read(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object) return null;
        bool reported = false;
        foreach (var key in new[] { "service_tiers", "serviceTiers" })
        {
            if (!model.TryGetProperty(key, out var tiers) || tiers.ValueKind != JsonValueKind.Array) continue;
            reported = true;
            if (tiers.EnumerateArray().Any(tier => tier.ValueKind == JsonValueKind.Object
                && tier.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is "priority" or "fast")) return true;
        }
        foreach (var key in new[] { "additional_speed_tiers", "additionalSpeedTiers" })
        {
            if (!model.TryGetProperty(key, out var tiers) || tiers.ValueKind != JsonValueKind.Array) continue;
            reported = true;
            if (tiers.EnumerateArray().Any(tier => tier.ValueKind == JsonValueKind.String && tier.GetString() == "fast"))
                return true;
        }
        return reported ? false : null;
    }

    public static IReadOnlyList<ModelOption> Merge(
        IReadOnlyList<ModelOption> live, IReadOnlyList<ModelOption> cached)
    {
        if (live.Count == 0) return cached;
        return live.Select(model => model.SupportsFastMode is null
            ? model with { SupportsFastMode = cached.FirstOrDefault(entry => entry.Id == model.Id)?.SupportsFastMode }
            : model).ToArray();
    }
}
