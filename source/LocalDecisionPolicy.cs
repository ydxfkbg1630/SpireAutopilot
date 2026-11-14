using System.Text.Json;

namespace SpireAutopilot;

/// <summary>Deterministic progress actions whose legal choice is already fixed by the native scene.</summary>
public static class LocalDecisionPolicy
{
    public static LegalAction? Choose(DecisionFrame frame)
    {
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
