using System.Reflection;
using System.Runtime.InteropServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace SpireAutopilot;

/// <summary>Uses the solver's drained reclamation boundaries and adaptive worker admission.</summary>
internal static class SolverMemoryGuard
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly object SampleLock = new();
    private static SolverMemorySample sample;
    private static long nextSample;

    internal static void Install(Harmony patcher, Assembly solver)
    {
        Type gc = solver.GetType("CombatSolver.SearchGcPolicy", true)!;
        patcher.Patch(gc.GetMethod("ResolveSystemMemoryLimit", Static)!,
            postfix: new HarmonyMethod(typeof(SolverMemoryGuard), nameof(SystemLimitPostfix)));
        // A bounded ordinary-GC epoch preserves native parallel drain/reclaim handling.
        // No global runtime GC setting or persisted solver preference is changed.
        patcher.Patch(gc.GetMethod("EnterSearchScope", Static)!,
            prefix: new HarmonyMethod(typeof(SolverMemoryGuard), nameof(SearchScopePrefix)));
    }

    private static void SearchScopePrefix(ref bool enableNoGcRegion, ref long noGcRegionBudgetBytes)
    {
        if (!SolverPresentation.Active) return;
        enableNoGcRegion = false;
        noGcRegionBudgetBytes = SolverMemoryPolicy.EpochBudget(Capture(), noGcRegionBudgetBytes);
    }

    private static void SystemLimitPostfix(ref long __result)
    {
        if (SolverPresentation.Active) __result = SolverMemoryPolicy.SystemLimit(Capture(), __result);
    }

    internal static int EffectiveWorkerLimit(int requested)
        => SolverPresentation.Active ? SolverMemoryPolicy.WorkerLimit(Capture(), requested) : requested;

    private static SolverMemorySample Capture()
    {
        lock (SampleLock)
        {
            long now = System.Environment.TickCount64;
            if (now < nextSample) return sample;
            nextSample = now + 250;
            if (OperatingSystem.IsWindows())
            {
                MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref status))
                    sample = new(Clamp(status.TotalPhysical), Clamp(status.AvailablePhysical), Clamp(status.AvailablePageFile));
            }
            return sample;
        }
    }

    private static long Clamp(ulong value) => (long)Math.Min(value, (ulong)long.MaxValue);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

