using System.Text.Json;

namespace SpireAutopilot;

/// <summary>Deterministic progress actions whose legal choice is already fixed by the native scene.</summary>
public static class LocalDecisionPolicy
{
    public static LegalAction? Choose(DecisionFrame frame)
    {
        if (frame.Scene == "NCrystalSphereScreen")
        {
            var state = JsonSerializer.SerializeToElement(frame.State);
            if (state.TryGetProperty("context", out var context)
                && context.TryGetProperty("recommendationIsCertain", out var certain) && certain.ValueKind == JsonValueKind.True
                && context.TryGetProperty("recommendedActionId", out var id) && id.ValueKind == JsonValueKind.String)
                return frame.Actions.FirstOrDefault(a => a.Id == id.GetString() && a.Kind is "scry_cell" or "scry_tool");
        }
        if (frame.Scene == "NRewardsScreen")
        {
            var gold = frame.Actions.FirstOrDefault(a => a.Kind == "reward" && IsGold(a.Details));
            if (gold != null) return gold;
        }
        if (frame.Actions.Count != 1) return null;
        LegalAction only = frame.Actions[0];
        return only.Kind is "tutorial" or "continue" or "confirm" or "open_shop" or "close_shop" or "open_chest" ? only : null;
    }
    private static bool IsGold(object? details)
    {
        var json = JsonSerializer.SerializeToElement(details);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("type", out var type) && type.GetString() == "GoldReward";
    }
}
