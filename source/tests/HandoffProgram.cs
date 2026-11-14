using SpireAutopilot;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
var policy = new CombatHandoffPolicy();
Check(!policy.Observe("battle-a", "Hybrid", true, 29.999, false, false) && !policy.UsesModel, "solver owns before timeout");
Check(policy.Observe("battle-a", "Hybrid", true, 30, false, false) && policy.UsesModel, "at 30 seconds unfinished search hands off");
Check(!policy.Observe("battle-a", "Hybrid", false, 0, true, false) && policy.UsesModel, "cancellation completion keeps model in same battle");
Check(!policy.Observe("battle-a", "Hybrid", true, 0, false, false) && policy.UsesModel, "later turn keeps model without another solver window");
Check(!policy.Observe("battle-b", "Hybrid", true, 0, false, false) && !policy.UsesModel, "next battle returns solver");
Check(!policy.Observe("battle-b", "Hybrid", true, 30, true, false) && !policy.UsesModel, "completed worker awaiting callback takes precedence");
Check(!policy.Observe("battle-b", "Hybrid", true, 30, false, true) && !policy.UsesModel, "deployment takes precedence");
Check(!policy.Observe("battle-b", "Hybrid", false, 30, false, false) && !policy.UsesModel, "idle and enemy turns never trigger");
Check(!policy.Observe("battle-b", "Hybrid", true, 12, false, false) && !policy.UsesModel, "each search uses its own elapsed time");
Check(!policy.Observe("battle-b", "Hybrid", true, double.NaN, false, false), "invalid elapsed cannot trigger");
Check(!policy.Observe("battle-b", "Hybrid", true, double.PositiveInfinity, false, false), "nonfinite elapsed cannot trigger");
Check(policy.Observe("battle-b", "Hybrid", true, 31, false, false), "second search may time out");
Check(!policy.Observe(null, "Hybrid", false, 0, false, false) && !policy.UsesModel, "battle exit clears latch");
Check(!policy.Observe("battle-c", "Solver", true, 100, false, false) && !policy.UsesModel, "pure solver remains solver");
Check(!policy.Observe("battle-c", "Model", false, 0, false, false) && policy.UsesModel, "pure model starts immediately");
Check(!policy.Observe("battle-c", "Hybrid", true, 0, false, false) && !policy.UsesModel, "explicit mode change starts a fresh policy");
policy.Observe("battle-c", "Hybrid", true, 31, false, false); policy.Reset();
Check(!policy.UsesModel, "explicit reset releases model latch");
foreach (double seconds in new[] { 1d, 10d, 30d, 90d, 600d })
{
    policy.Reset();
    Check(!policy.Observe("custom", "Hybrid", true, seconds - .001, false, false, seconds), "custom threshold waits " + seconds);
    Check(policy.Observe("custom", "Hybrid", true, seconds, false, false, seconds), "custom threshold triggers " + seconds);
}
foreach (double invalid in new[] { 0d, -1d, 601d, double.NaN, double.PositiveInfinity })
{
    policy.Reset();
    Check(!policy.Observe("invalid", "Hybrid", true, 29.999, false, false, invalid), "invalid threshold uses default");
    Check(policy.Observe("invalid", "Hybrid", true, 30, false, false, invalid), "invalid threshold falls back to 30");
}
Console.WriteLine($"Handoff policy checks passed: {passed}");
