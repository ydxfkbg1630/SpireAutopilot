using System.Reflection;
using Godot;

namespace SpireAutopilot;

internal static class CompatibilityProbe
{
    public static object Verify()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Assembly solver = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetType("CombatSolver.SolverController") != null);
        Type controller = solver.GetType("CombatSolver.SolverController")!;
        var capture = solver.GetType("CombatSolver.SolverSettings")!.GetMethod("Capture", flags)!;
        var native = capture.Invoke(null, null)!;
        if (SolverLiveSearch.Enabled)
        {
            var liveType = native.GetType();
            foreach (var name in new[] { "UseBeamWidthPortfolio", "UseNoveltyPortfolio", "UseEarlyTurnExploration" })
                if ((bool)liveType.GetProperty(name)!.GetValue(native)!) throw new InvalidOperationException("Live portfolio limit: " + name);
            var liveProfile = liveType.GetProperty("Profile")!.GetValue(native)!;
            foreach (var limit in new[] { ("BeamWidth", 64), ("MaxExpandedNodes", 50000), ("SoftTimeBudgetMilliseconds", 20000) })
                if (Convert.ToInt64(liveProfile.GetType().GetProperty(limit.Item1)!.GetValue(liveProfile)) > limit.Item2)
                    throw new InvalidOperationException("Live profile limit: " + limit.Item1);
        }
        Type snapshot = native.GetType();
        var clone = snapshot.GetMethod("<Clone>$")!;
        var workers = snapshot.GetProperty("SearchMaxDegreeOfParallelism")!;
        object input = clone.Invoke(native, null)!;
        workers.SetValue(input, 16);
        var hook = typeof(SolverPresentation).GetMethod("WorkerSnapshotPostfix", flags)!.MakeGenericMethod(snapshot);
        object?[] args = { input };
        int previousLimit = SolverPresentation.WorkerLimit;
        try
        {
            SolverPresentation.WorkerLimit = 8;
            hook.Invoke(null, args);
            int effective = (int)workers.GetValue(args[0])!;
            int untouched = (int)workers.GetValue(input)!;
            if (effective != 8 || untouched != 16 || ReferenceEquals(input, args[0])) throw new InvalidOperationException("Runtime worker snapshot ceiling assertion failed.");
            SolverPresentation.WorkerLimit = 0;
            object?[] follow = { input };
            hook.Invoke(null, follow);
            if ((int)workers.GetValue(follow[0])! != 16) throw new InvalidOperationException("Follow-user worker assertion failed.");
            var layer = solver.GetType("CombatSolver.SolverOverlay")!.GetField("_layer", flags)!.GetValue(null) as CanvasLayer;
            bool hidden = layer == null || !layer.Visible;
            if (!hidden) throw new InvalidOperationException("Solver overlay ownership assertion failed.");
            SolverPresentation.Restore();
            object?[] restored = { input };
            SolverPresentation.WorkerLimit = 8;
            hook.Invoke(null, restored);
            if ((int)workers.GetValue(restored[0])! != 16) throw new InvalidOperationException("Pause worker restoration assertion failed.");
            SolverPresentation.Activate(controller);
            return new { success=true, nativeSnapshotWorkers=workers.GetValue(native), syntheticOriginal=untouched, cappedWorkers=effective,
                followUserWorkers=workers.GetValue(follow[0]), pausedWorkers=workers.GetValue(restored[0]), solverOverlayHidden=hidden,
                actualSearchWorkersAltered=false, persistedSettingsAltered=false };
        }
        finally { SolverPresentation.WorkerLimit = previousLimit; }
    }
}
