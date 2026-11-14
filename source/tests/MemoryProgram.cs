using SpireAutopilot;
using static SpireAutopilot.SolverMemoryPolicy;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
var normal = new SolverMemorySample(32 * GiB, 20 * GiB, 24 * GiB);
Check(WorkerLimit(normal, 16) == 16, "Healthy admission retains requested workers");
Check(EpochBudget(normal, 16 * GiB) == GiB, "Allocation epoch bounded");
var busy = new SolverMemorySample(32 * GiB, 5 * GiB, 24 * GiB);
Check(WorkerLimit(busy, 16) == 4, "Workers scale to physical headroom");
Check(SystemLimit(busy, long.MaxValue) < 30 * GiB, "Physical reserve affects native limit");
var commit = normal with { AvailableCommit = 2 * GiB };
Check(WorkerLimit(commit, 16) == 2, "Commit reserve limits parallelism");
Check(SystemLimit(commit, long.MaxValue) == 13 * GiB, "Commit headroom translated into native load limit");
Check(WorkerLimit(normal with { AvailablePhysical = 0 }, 16) == 1, "Minimum admitted workers");
Check(EpochBudget(normal with { AvailablePhysical = 0 }, 16 * GiB) == 256 * MiB, "Native checkpoint minimum");
Check(EpochBudget(normal, 128 * MiB) == 128 * MiB, "Smaller configured budget preserved");
Check(SystemLimit(normal, 10 * GiB) == 10 * GiB, "Stricter native limit preserved");
Check(SystemLimit(default, 15 * GiB) == 15 * GiB, "Unavailable sample preserves native guard");
Check(WorkerLimit(default, 4) == 4, "Unavailable sample preserves worker setting");
for (int physical = 0; physical <= 32; physical++)
for (int availableCommit = 0; availableCommit <= 40; availableCommit++)
{
    var value = new SolverMemorySample(32 * GiB, physical * GiB, availableCommit * GiB);
    Check(WorkerLimit(value, 16) is >= 1 and <= 16, "Worker bounds over pressure grid");
    Check(SystemLimit(value, 30 * GiB) is > 0 and <= 30 * GiB, "System limit bounds over pressure grid");
    Check(EpochBudget(value, 16 * GiB) is >= 256 * MiB and <= GiB, "Epoch bounds over pressure grid");
}
Console.WriteLine($"Memory policy checks passed: {checks}");

