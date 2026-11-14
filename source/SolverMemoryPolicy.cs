namespace SpireAutopilot;

/// <summary>Pure byte accounting shared by native checkpoint and worker admission guards.</summary>
internal readonly record struct SolverMemorySample(long TotalPhysical, long AvailablePhysical,
    long AvailableCommit)
{
    internal bool Valid => TotalPhysical > 0 && AvailablePhysical >= 0 && AvailableCommit >= 0;
}

internal static class SolverMemoryPolicy
{
    internal const long MiB = 1024L * 1024;
    internal const long GiB = 1024L * MiB;
    internal static long PhysicalReserve(SolverMemorySample sample)
        => Math.Max(2 * GiB, sample.TotalPhysical / 10);
    internal static long Headroom(SolverMemorySample sample)
        => Math.Max(0, Math.Min(sample.AvailablePhysical - PhysicalReserve(sample),
            sample.AvailableCommit - GiB));
    internal static long SystemLimit(SolverMemorySample sample, long original)
    {
        if (!sample.Valid) return original;
        long used = Math.Max(0, sample.TotalPhysical - sample.AvailablePhysical);
        long available = Headroom(sample);
        return Math.Min(original, used > long.MaxValue - available ? long.MaxValue : Math.Max(1, used + available));
    }
    internal static long EpochBudget(SolverMemorySample sample, long requested)
        => sample.Valid ? Math.Min(requested, Math.Clamp(Headroom(sample) / 4, 256 * MiB, GiB))
            : Math.Min(requested, GiB);
    internal static int WorkerLimit(SolverMemorySample sample, int requested)
        => sample.Valid ? (int)Math.Clamp(Headroom(sample) / (384 * MiB), 1, Math.Max(1, requested))
            : requested;
}

