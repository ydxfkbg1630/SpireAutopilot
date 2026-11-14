using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace SpireAutopilot;

internal sealed record SolverSearchStatus(string? SessionId, bool IsRunning, double ElapsedSeconds,
    bool ComputationCompleted, bool IsDeploying, bool IsQuiescent, string Phase);

/// <summary>Times the native worker's GC admission/search interval separately from root capture and native choice waiting.</summary>
internal static class SolverSearchStatusCapture
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ConditionalWeakTable<object, Stamp> Stamps = new();
    private static readonly ConditionalWeakTable<object, Stamp> Fallback = new();
    private static readonly Harmony Patcher = new("SpireAutopilot.NativeSearchStatus");
    private static long nextBind;
    private static FieldInfo? searchField, deploymentField, setupField;
    private static PropertyInfo? workerTask, coreSignal, setupResult, setupDriving;
    private static PropertyInfo? coreInteraction, setupInteraction;
    private static FieldInfo? setupCancellation;
    private static Func<object?>? currentSetupSignal;
    private static FieldInfo[] releaseLists = [];
    private static long sequence;
    internal static bool Installed { get; private set; }

    internal static bool EnsureInstalled()
    {
        if (Installed) return true;
        long now = System.Environment.TickCount64;
        if (now < nextBind) return false;
        nextBind = now + 5000;
        var solver = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("CombatSolver.SolverController", false) != null);
        if (solver == null) return false;
        Install(Patcher, solver);
        return true;
    }

    internal static void Install(Harmony patcher, Assembly solver)
    {
        if (Installed) return;
        var solve = solver.GetType("CombatSolver.SearchGcPolicy", true)!.GetMethod("EnterSearchScope", Static)!;
        var controller = solver.GetType("CombatSolver.SolverController", true)!;
        var setup = solver.GetType("CombatSolver.PlayerTurnSetupCoordinator", true)!;
        searchField = controller.GetField("_search", Static)!;
        deploymentField = controller.GetField("_deployment", Static)!;
        workerTask = searchField.FieldType.GetProperty("WorkerCompletion", Instance)!;
        coreSignal = searchField.FieldType.GetProperty("MemoryPressureSignal", Instance)!;
        coreInteraction = searchField.FieldType.GetProperty("Interaction", Instance)!;
        setupField = setup.GetField("_active", Static)!;
        setupResult = setupField.FieldType.GetProperty("Result", Instance)!;
        setupDriving = setupField.FieldType.GetProperty("ReplayDrivingStarted", Instance)!;
        setupInteraction = setupField.FieldType.GetProperty("Interaction", Instance)!;
        setupCancellation = setupField.FieldType.GetField("SearchCancellation", Instance)!;
        currentSetupSignal = setup.GetProperty("CurrentMemoryPressureSignal", Static)!.GetMethod!
            .CreateDelegate<Func<object?>>();
        releaseLists = new[] { "PendingSearchReferenceReleases", "PendingDeploymentReferenceReleases", "PendingDeferredSearchReleases" }
            .Select(name => controller.GetField(name, Static) ?? throw new MissingFieldException(controller.FullName, name)).ToArray();
        patcher.Patch(solve, prefix: new HarmonyMethod(typeof(SolverSearchStatusCapture), nameof(Begin)));
        var finalize = solver.GetType("CombatSolver.SearchInteractionState", true)!.GetMethod("FinalizeWorkerResult", Instance)!;
        patcher.Patch(finalize, prefix: new HarmonyMethod(typeof(SolverSearchStatusCapture), nameof(End)));
        Installed = true;
    }

    private static void Begin(object __2)
    {
        var stamp = new Stamp(Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp());
        object? core = searchField!.GetValue(null), setup = setupField!.GetValue(null);
        object? interaction = core != null && ReferenceEquals(coreSignal!.GetValue(core), __2)
            ? coreInteraction!.GetValue(core) : setup != null && ReferenceEquals(currentSetupSignal!(), __2)
                ? setupInteraction!.GetValue(setup) : null;
        lock (Stamps)
        {
            Stamps.Remove(__2); Stamps.Add(__2, stamp);
            if (interaction != null) { Stamps.Remove(interaction); Stamps.Add(interaction, stamp); }
        }
    }
    private static void End(object __instance)
    {
        lock (Stamps)
            if (Stamps.TryGetValue(__instance, out var stamp)) Volatile.Write(ref stamp.EndedAt, Stopwatch.GetTimestamp());
    }

    /// <summary>Call on the game thread, which owns native session pointers and release task lists.</summary>
    internal static SolverSearchStatus Capture()
    {
        if (!EnsureInstalled()) return new(null, false, 0, false, false, true, "unbound");
        object? core = searchField!.GetValue(null);
        object? setup = setupField!.GetValue(null);
        object? signal = core != null ? coreSignal!.GetValue(core) : currentSetupSignal!();
        Task? task = core == null ? null : workerTask!.GetValue(core) as Task;
        bool deploy = deploymentField!.GetValue(null) != null || setup != null && (bool)setupDriving!.GetValue(setup)!;
        bool ready = task?.IsCompleted == true || setup != null && setupResult!.GetValue(setup) != null;
        Stamp? stamp = null;
        if (signal != null) lock (Stamps) Stamps.TryGetValue(signal, out stamp);
        // A search may have begun before pilot activation. Begin timing at first observation
        // instead of counting native UI/root-capture time as actual solver computation.
        object? fallbackIdentity = core ?? (setup == null ? null : setupCancellation!.GetValue(setup));
        if (stamp == null && fallbackIdentity != null)
            stamp = Fallback.GetValue(fallbackIdentity, static _ => new Stamp(Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp()));
        long ended = stamp == null ? 0 : Volatile.Read(ref stamp.EndedAt);
        bool nativeWorkerActive = core != null && task?.IsCompleted == false
            || setup != null && setupCancellation!.GetValue(setup) != null;
        bool computing = stamp != null && ended == 0 && !ready && nativeWorkerActive;
        bool released = releaseLists.All(f => ((IEnumerable)f.GetValue(null)!).Cast<Task>().All(t => t.IsCompleted));
        bool idle = (task == null || task.IsCompleted) && !deploy
            && released && (setup == null || setupCancellation!.GetValue(setup) == null);
        double seconds = stamp == null ? 0 : Stopwatch.GetElapsedTime(stamp.StartedAt, ended == 0 ? Stopwatch.GetTimestamp() : ended).TotalSeconds;
        return new(stamp == null ? null : "native-search:" + stamp.Id, computing, seconds,
            stamp != null && (ended != 0 || ready), deploy, idle,
            computing ? core == null ? "turn_setup_search" : "combat_search" : deploy ? "deployment" : ready || ended != 0 ? "computed" : "waiting");
    }
    private sealed class Stamp(long id, long startedAt)
    {
        internal readonly long Id = id, StartedAt = startedAt;
        internal long EndedAt;
    }
}
