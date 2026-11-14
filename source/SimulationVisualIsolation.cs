using System.Reflection;
using HarmonyLib;

namespace SpireAutopilot;

/// <summary>Keep the Fool skin's native Spine queries on the live game thread.</summary>
internal static class SimulationVisualIsolation
{
    private static Func<bool>? inSimulation;
    private static int gameThread;

    internal static int Install(Harmony patcher, Assembly solver, IEnumerable<Assembly>? assemblies = null)
    {
        gameThread = Environment.CurrentManagedThreadId;
        var isolation = solver.GetType("CombatSolver.SimulationNotificationIsolation", true)!;
        inSimulation = isolation.GetProperty("IsActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetMethod!.CreateDelegate<Func<bool>>();
        int count = 0;
        foreach (var assembly in assemblies ?? AppDomain.CurrentDomain.GetAssemblies())
        {
            var motion = assembly.GetType("NailongFool.Motion", false);
            var retarget = motion?.GetMethod("Retarget", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (retarget == null) continue;
            patcher.Patch(retarget, prefix: new HarmonyMethod(typeof(SimulationVisualIsolation), nameof(LiveVisualPrefix))
                { priority = Priority.First });
            count++;
        }
        return count;
    }

    private static bool LiveVisualPrefix()
        => Environment.CurrentManagedThreadId == gameThread && inSimulation?.Invoke() != true;
}
