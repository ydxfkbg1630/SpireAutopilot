namespace SpireAutopilot;

/// <summary>One-way ownership transition within a battle; the next battle starts fresh.</summary>
public sealed class CombatHandoffPolicy
{
    public const double SearchTimeoutSeconds = 30;
    private string? battle;
    private string? currentMode;
    public bool UsesModel { get; private set; }
    public static double NormalizeTimeout(double seconds)
        => double.IsFinite(seconds) && seconds >= 1 && seconds <= 600 ? seconds : SearchTimeoutSeconds;
    public bool Observe(string? battleId, string mode, bool searching, double elapsedSeconds, bool completed, bool deploying,
        double timeoutSeconds = SearchTimeoutSeconds)
    {
        if (battleId != battle || mode != currentMode)
        {
            battle = battleId;
            currentMode = mode;
            UsesModel = battleId != null && mode == "Model";
        }
        if (battleId == null || mode != "Hybrid" || UsesModel || !searching || completed || deploying
            || !double.IsFinite(elapsedSeconds) || elapsedSeconds < NormalizeTimeout(timeoutSeconds)) return false;
        UsesModel = true;
        return true;
    }
    public void Reset() { battle = null; currentMode = null; UsesModel = false; }
}
