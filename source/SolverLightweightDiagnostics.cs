using System.Reflection;
using Godot;
using HarmonyLib;

namespace SpireAutopilot;

/// <summary>Scoped log/forensic cost control; keeps solver and native save semantics intact.</summary>
internal static class SolverLightweightDiagnostics
{
    private static Func<bool>? ownsCombat;
    private static readonly Dictionary<string, PropertyInfo> ResultProperties = new();
    private static bool announced;
    internal static long CompactResultCount { get; private set; }
    internal static long DeferredRoutineCheckpointCount { get; private set; }

    internal static void Install(Harmony patcher, Assembly solver, Func<bool> isOwnedCombat)
    {
        ownsCombat = isOwnedCombat;
        const BindingFlags members = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var diagnostics = solver.GetType("CombatSolver.SolverDiagnostics", true)!;
        var result = solver.GetType("CombatSolver.SolverResult", true)!;
        var descriptions = diagnostics.GetMethods(members)
            .Where(m => m.Name == "DescribeResult" && m.ReturnType == typeof(string)
                && m.GetParameters().FirstOrDefault()?.ParameterType == result).ToArray();
        if (descriptions.Length == 0) throw new MissingMethodException(diagnostics.FullName, "DescribeResult");
        foreach (string name in new[] { "StartTurnNumber", "DeathTurn", "ProjectedBattleHpLost", "CombatEndedTurn", "WasReused" })
            ResultProperties[name] = result.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(result.FullName, name);
        foreach (var describe in descriptions)
            patcher.Patch(describe, prefix: new HarmonyMethod(typeof(SolverLightweightDiagnostics), nameof(DescribePrefix)));
        var exporter = solver.GetType("CombatSolver.CombatBugReportExporter", true)!;
        var checkpoints = exporter.GetMethods(members).Where(m => m.Name == "RecordCheckpoint"
            && m.GetParameters().Any(p => p.Name == "label" && p.ParameterType == typeof(string))).ToArray();
        if (checkpoints.Length == 0) throw new MissingMethodException(exporter.FullName, "RecordCheckpoint");
        foreach (var checkpoint in checkpoints)
            patcher.Patch(checkpoint, prefix: new HarmonyMethod(typeof(SolverLightweightDiagnostics), nameof(CheckpointPrefix)));
    }

    private static bool IsLightweight()
        => SolverPresentation.LightweightDiagnostics && ownsCombat?.Invoke() == true;

    private static void Announce()
    {
        if (announced) return;
        announced = true;
        GD.Print("[SpireAutopilot] LIGHTWEIGHT_DIAGNOSTICS active=true routine_search_checkpoints=omitted combat_boundary_and_error_checkpoints=retained persisted=false");
    }

    private static bool DescribePrefix(object __0, ref string __result)
    {
        if (!IsLightweight()) return true;
        Announce();
        CompactResultCount++;
        object? Read(string name) => ResultProperties[name].GetValue(__0);
        __result = "[CombatSolver/Test] RESULT lightweight=true"
            + " turn=" + Read("StartTurnNumber")
            + " projected_battle_hp_lost=" + Read("ProjectedBattleHpLost")
            + " death_turn=" + (Read("DeathTurn") ?? "-")
            + " combat_ended_turn=" + (Read("CombatEndedTurn") ?? "-")
            + " reused=" + Read("WasReused");
        return false;
    }

    private static bool CheckpointPrefix(string label)
    {
        if (!IsLightweight()) return true;
        // All combat boundaries, exports and error/failure checkpoints keep the upstream path.
        // Search-root IDs continue to refer to the last fully captured forensic root. They
        // are diagnostic cross-references, not the live combat/continuation validation keys.
        bool routine = label is "search_completed" or "search_reused"
            || label.StartsWith("search_request_", StringComparison.Ordinal);
        if (!routine || label.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || label.Contains("error", StringComparison.OrdinalIgnoreCase)) return true;
        Announce();
        DeferredRoutineCheckpointCount++;
        return false;
    }
}
