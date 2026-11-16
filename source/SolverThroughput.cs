using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace SpireAutopilot;

/// <summary>Skips only renderer-oriented voluntary CPU pacing during owned maximum-speed searches.</summary>
internal static class SolverThroughput
{
    private static Action<object>? observeGc;
    private static int checkMask;
    private static bool announced;

    internal static void Install(Harmony patcher, Assembly solver)
    {
        SolverMemoryGuard.Install(patcher, solver);
        var executor = solver.GetType("CombatSolver.CombatBeamSolver+ParallelExpansionExecutor", true)!;
        var constructor = executor.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 2 && c.GetParameters()[1].ParameterType == typeof(int));
        patcher.Patch(constructor, postfix: new HarmonyMethod(typeof(SolverThroughput), nameof(ExecutorPostfix)));
        var pacer = solver.GetType("CombatSolver.SearchWorkPacer", true)!;
        var checks = pacer.GetField("_checks", BindingFlags.Instance | BindingFlags.NonPublic);
        if (checks?.FieldType != typeof(int))
            throw new MissingFieldException(pacer.FullName, "_checks: Int32");
        int interval = (int)(solver.GetType("CombatSolver.SolverWeights", true)!
            .GetField("BackgroundYieldCheckInterval", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetRawConstantValue()!);
        if (interval <= 0 || (interval & (interval - 1)) != 0)
            throw new InvalidOperationException("Solver pacing interval requires a positive power of two.");
        checkMask = interval - 1;
        var yield = pacer.GetMethod("YieldIfNeeded", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(pacer.FullName, "YieldIfNeeded");
        var observer = pacer.GetMethod("ObserveGcPause", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            Type.EmptyTypes) ?? throw new MissingMethodException(pacer.FullName, "ObserveGcPause()");
        var instance = Expression.Parameter(typeof(object));
        observeGc = Expression.Lambda<Action<object>>(Expression.Call(Expression.Convert(instance, pacer), observer), instance).Compile();
        patcher.Patch(yield, prefix: new HarmonyMethod(typeof(SolverThroughput), nameof(PacerPrefix)));
    }

    private static bool PacerPrefix(object __instance, ref int ____checks)
    {
        // This runs on search workers: read ownership flags only, never native Godot state.
        if (!SolverPresentation.Active || !SolverPresentation.MaxSpeed) return true;
        // Harmony's three-underscore field binding + native _checks. Sharing its
        // original counter keeps the observation cadence continuous across toggles.
        if ((unchecked(++____checks) & checkMask) == 0) observeGc!(__instance);
        return false;
    }

    private static void ExecutorPostfix(int __1)
    {
        if (SolverPresentation.Active)
            GD.Print($"[SpireAutopilot] SEARCH_EXECUTOR workers={__1}");
    }

    internal static void AnnounceOnce()
    {
        if (announced || !SolverPresentation.MaxSpeed) return;
        announced = true;
        GD.Print("[SpireAutopilot] SOLVER_MAX_SPEED renderer_pacing=bypassed gc_observation=retained memory_policy=adaptive_headroom default_gc=true persisted=false");
    }

}
