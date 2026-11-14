using SpireAutopilot;
using static SpireAutopilot.SolverMemoryPolicy;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
var normal = new SolverMemorySample(32 * GiB, 20 * GiB, 24 * GiB);
Check(EpochBudget(normal, 16 * GiB) == 4 * GiB, "Allocation epoch bounded");
var busy = new SolverMemorySample(32 * GiB, 5 * GiB, 24 * GiB);
Check(SystemLimit(busy, long.MaxValue) < 30 * GiB, "Physical reserve affects native limit");
var commit = normal with { AvailableCommit = 2 * GiB };
Check(SystemLimit(commit, long.MaxValue) == 13 * GiB, "Commit headroom translated into native load limit");
Check(EpochBudget(normal with { AvailablePhysical = 0 }, 16 * GiB) == 4 * GiB, "Native scheduler resolves current headroom");
Check(EpochBudget(normal, 128 * MiB) == 128 * MiB, "Smaller configured budget preserved");
Check(SystemLimit(normal, 10 * GiB) == 10 * GiB, "Stricter native limit preserved");
Check(SystemLimit(default, 15 * GiB) == 15 * GiB, "Unavailable sample preserves native guard");
for (int physical = 0; physical <= 32; physical++)
for (int availableCommit = 0; availableCommit <= 40; availableCommit++)
{
    var value = new SolverMemorySample(32 * GiB, physical * GiB, availableCommit * GiB);
    Check(SystemLimit(value, 30 * GiB) is > 0 and <= 30 * GiB, "System limit bounds over pressure grid");
    Check(EpochBudget(value, 16 * GiB) == 4 * GiB, "Epoch bounds over pressure grid");
}
Console.WriteLine($"Memory policy checks passed: {checks}");
