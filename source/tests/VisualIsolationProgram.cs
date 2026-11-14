using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SpireAutopilot;

internal static class Program
{
    private static void Main()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var harmony = new Harmony("SpireAutopilot.VisualIsolationTests");
        if (SimulationVisualIsolation.Install(harmony, assembly, [assembly]) != 1) throw new Exception("Patch binding");
        NailongFool.Motion.Retarget();
        if (NailongFool.Motion.Calls != 1) throw new Exception("Live visuals");
        CombatSolver.SimulationNotificationIsolation.Active = true;
        NailongFool.Motion.Retarget();
        if (NailongFool.Motion.Calls != 1) throw new Exception("Main-thread simulation isolation");
        CombatSolver.SimulationNotificationIsolation.Active = false;
        Task.Run(() => { for (int i = 0; i < 10000; i++) NailongFool.Motion.Retarget(); }).GetAwaiter().GetResult();
        if (NailongFool.Motion.Calls != 1) throw new Exception("Worker native isolation");
        NailongFool.Motion.Retarget();
        if (NailongFool.Motion.Calls != 2) throw new Exception("Live visuals resume");
        harmony.UnpatchAll(harmony.Id);
        Console.WriteLine("PASS 5 visual isolation checks; 10000 worker calls isolated");
    }
}

namespace NailongFool
{
    internal static class Motion
    {
        internal static int Calls;
        [MethodImpl(MethodImplOptions.NoInlining)] internal static void Retarget() => Interlocked.Increment(ref Calls);
    }
}

namespace CombatSolver
{
    internal static class SimulationNotificationIsolation
    {
        [ThreadStatic] internal static bool Active;
        public static bool IsActive => Active;
    }
}
