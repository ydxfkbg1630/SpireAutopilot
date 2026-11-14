using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;

namespace SpireAutopilot;

/// <summary>Temporary presentation ownership. Never touches native combat or choice UI.</summary>
public static class SolverPresentation
{
    private const BindingFlags Members = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Harmony Patcher = new("SpireAutopilot.SolverPresentation");
    private static FieldInfo? layerField;
    private static FieldInfo? lastSnapshotField;
    private static MethodInfo? populateRoute;
    private static MethodInfo? refreshControls;
    private static bool routePresentationDeferred;
    private static long deferredRouteUpdates;
    private static CanvasLayer? ownedLayer;
    private static bool originalVisible;
    private static volatile bool active;
    private static bool bound;
    private static readonly bool SuppressPresentation = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_SOLVER_PRESENTATION") != "0";
    private static long suppressedUpdates;
    private static Func<bool>? searching;
    private static Func<bool>? deploying;
    private static long lastFrame;
    private static readonly FrameSamples[] samples = { new(), new(), new() };
    private static string? profilePath;
    private static double lastWrite;
    private static int workerLimit;
    private static MethodInfo? cloneSettings;
    private static PropertyInfo? snapshotWorkers;
    private static bool workerLimitReported;
    private static int? originalWorkers;
    private static int? effectiveWorkers;
    private static int? actualCaptureOriginalWorkers;
    private static int? actualCaptureEffectiveWorkers;
    private static volatile bool maxSpeed = true;

    /// <summary>Effective search-worker ceiling during pilot ownership; zero follows user settings.</summary>
    public static int WorkerLimit
    {
        get => workerLimit;
        set
        {
            if (value is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(value), "Solver worker limit must be 0 through 16.");
            if (workerLimit == value) return;
            workerLimit = value;
            workerLimitReported = false;
        }
    }

    public static bool Active => active;
    public static bool LightweightDiagnostics { get; set; } = true;
    public static bool MaxSpeed { get => maxSpeed; set => maxSpeed = value; }

    public static void Activate(Type controller)
    {
        if (!bound) Bind(controller);
        active = true;
        SolverThroughput.AnnounceOnce();
        if (SuppressPresentation) HideOwnedLayer();
    }

    public static void Restore()
    {
        active = false;
        // Populate only once, as ownership returns to the player. Keep the layer hidden
        // until its controls describe the latest retained native solver result.
        if (routePresentationDeferred && ownedLayer != null && GodotObject.IsInstanceValid(ownedLayer)
            && lastSnapshotField?.GetValue(null) is { } latestSnapshot)
        {
            populateRoute!.Invoke(null, new[] { latestSnapshot, (object)true });
            refreshControls!.Invoke(null, null);
        }
        routePresentationDeferred = false;
        if (ownedLayer != null && GodotObject.IsInstanceValid(ownedLayer)) ownedLayer.Visible = originalVisible;
        ownedLayer = null;
        lastFrame = 0;
        workerLimitReported = false;
        WriteProfile();
    }

    private static void Bind(Type controller)
    {
        var overlay = controller.Assembly.GetType("CombatSolver.SolverOverlay", true)!;
        layerField = overlay.GetField("_layer", Members)
            ?? throw new MissingFieldException(overlay.FullName, "_layer");
        lastSnapshotField = overlay.GetField("_lastSnapshot", Members)
            ?? throw new MissingFieldException(overlay.FullName, "_lastSnapshot");
        populateRoute = overlay.GetMethod("PopulateRoute", Members)
            ?? throw new MissingMethodException(overlay.FullName, "PopulateRoute");
        refreshControls = overlay.GetMethod("RefreshControls", Members)
            ?? throw new MissingMethodException(overlay.FullName, "RefreshControls");
        if (layerField.FieldType != typeof(CanvasLayer))
            throw new InvalidOperationException("CombatSolver overlay root requires a CanvasLayer.");
        var showLayer = overlay.GetMethod("ShowLayer", Members)
            ?? throw new MissingMethodException(overlay.FullName, "ShowLayer");
        Patcher.Patch(showLayer, prefix: new HarmonyMethod(typeof(SolverPresentation), nameof(ShowLayerPrefix)));
        Patcher.Patch(populateRoute, prefix: new HarmonyMethod(typeof(SolverPresentation), nameof(PopulateRoutePrefix)));
        var notifier = controller.Assembly.GetType("CombatSolver.SearchCompletionNotifier", true)!;
        var notify = notifier.GetMethod("Notify", Members)
            ?? throw new MissingMethodException(notifier.FullName, "Notify");
        Patcher.Patch(notify, prefix: new HarmonyMethod(typeof(SolverPresentation), nameof(RenderPrefix)));
        // These methods only render controls, progress labels and animation readouts.
        // Keep ShowResult/ShowDeploying intact: their presentation bookkeeping is used later.
        foreach (string name in new[] { "RefreshControls", "ShowDeploymentStep", "ShowEndTurnDeploymentStep", "AdvanceSearchReadouts" })
        {
            var target = overlay.GetMethod(name, Members)
                ?? throw new MissingMethodException(overlay.FullName, name);
            Patcher.Patch(target, prefix: new HarmonyMethod(typeof(SolverPresentation), nameof(RenderPrefix)));
        }
        searching = controller.GetProperty("IsSearching", Members)!.GetMethod!.CreateDelegate<Func<bool>>();
        deploying = controller.GetProperty("IsDeploying", Members)!.GetMethod!.CreateDelegate<Func<bool>>();
        BindWorkerCeiling(controller.Assembly);
        SolverProgressCapture.Install(Patcher, controller.Assembly);
        SolverThroughput.Install(Patcher, controller.Assembly);
        int isolatedSkins = SimulationVisualIsolation.Install(Patcher, controller.Assembly);
        GD.Print($"[SpireAutopilot] SIMULATION_VISUAL_ISOLATION skin_hooks={isolatedSkins} scope=prediction_and_worker_threads");
        SolverLightweightDiagnostics.Install(Patcher, controller.Assembly, IsOwnedCombat);
        // Enable diagnostics explicitly for A/B runs; ordinary games pay no per-frame sampling cost.
        if (System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_FRAME_PROFILE") is { Length: > 0 } profile)
        {
            profilePath = Path.GetFullPath(profile);
            SolverSlowCalls.Install(Patcher, controller.Assembly, profilePath, IsOwnedCombat);
            var dispatcher = controller.Assembly.GetType("CombatSolver.SolverDispatcher", true)!;
            Patcher.Patch(dispatcher.GetMethod("_Process", BindingFlags.Instance | BindingFlags.Public)!,
                prefix: new HarmonyMethod(typeof(SolverPresentation), nameof(FramePrefix)),
                postfix: new HarmonyMethod(typeof(SolverPresentation), nameof(FramePostfix)));
        }
        bound = true;
    }

    private static void BindWorkerCeiling(Assembly solver)
    {
        var settings = solver.GetType("CombatSolver.SolverSettings", true)!;
        var capture = settings.GetMethod("Capture", Members, Type.EmptyTypes)
            ?? throw new MissingMethodException(settings.FullName, "Capture");
        var snapshot = capture.ReturnType;
        // This audited upstream API returns a sealed immutable record class. Clone it rather
        // than altering a snapshot that another consumer might already own.
        if (snapshot.IsValueType || !snapshot.IsSealed)
            throw new InvalidOperationException("CombatSolver settings Capture requires a sealed record reference snapshot.");
        cloneSettings = snapshot.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(snapshot.FullName, "<Clone>$");
        snapshotWorkers = snapshot.GetProperty("SearchMaxDegreeOfParallelism", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(snapshot.FullName, "SearchMaxDegreeOfParallelism");
        if (snapshotWorkers.PropertyType != typeof(int) || snapshotWorkers.SetMethod == null)
            throw new InvalidOperationException("CombatSolver settings snapshot worker field requires an integer initializer.");
        var postfix = typeof(SolverPresentation).GetMethod(nameof(ActualWorkerSnapshotPostfix), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(snapshot);
        Patcher.Patch(capture, postfix: new HarmonyMethod(postfix));
    }

    private static void WorkerSnapshotPostfix<T>(ref T __result) where T : class
    {
        if (!IsOwnedCombat()) return;
        int original = (int)snapshotWorkers!.GetValue(__result)!;
        int effective = workerLimit == 0 ? original : Math.Min(original, workerLimit);
        originalWorkers = original;
        effectiveWorkers = effective;
        if (effective >= original) return;
        var copy = cloneSettings!.Invoke(__result, null)!;
        snapshotWorkers.SetValue(copy, effective);
        __result = (T)copy;
        if (workerLimitReported) return;
        workerLimitReported = true;
        GD.Print($"[SpireAutopilot] SOLVER_WORKER_CEILING original={original} effective={effective} scope=pilot_snapshot persisted=false");
    }

    private static void ActualWorkerSnapshotPostfix<T>(ref T __result) where T : class
    {
        if (!IsOwnedCombat()) return;
        int original = (int)snapshotWorkers!.GetValue(__result)!;
        WorkerSnapshotPostfix(ref __result);
        actualCaptureOriginalWorkers = original;
        actualCaptureEffectiveWorkers = (int)snapshotWorkers.GetValue(__result)!;
    }

    private static bool IsOwnedCombat()
        => active && CombatManager.Instance.IsInProgress
            && CombatManager.Instance.DebugOnlyGetState()?.Players.Count == 1;

    private static bool ShowLayerPrefix()
    {
        if (!SuppressPresentation || !IsOwnedCombat()) return true;
        HideOwnedLayer();
        return false;
    }

    private static bool RenderPrefix()
    {
        if (!SuppressPresentation || !IsOwnedCombat()) return true;
        suppressedUpdates++;
        return false;
    }

    private static bool PopulateRoutePrefix()
    {
        if (!SuppressPresentation || !IsOwnedCombat()) return true;
        routePresentationDeferred = true;
        deferredRouteUpdates++;
        return false;
    }

    private static void HideOwnedLayer()
    {
        if (layerField?.GetValue(null) is not CanvasLayer layer || !GodotObject.IsInstanceValid(layer)) return;
        if (!ReferenceEquals(ownedLayer, layer))
        {
            if (ownedLayer != null && GodotObject.IsInstanceValid(ownedLayer)) ownedLayer.Visible = originalVisible;
            ownedLayer = layer;
            originalVisible = layer.Visible;
        }
        layer.Visible = false;
    }

    private static void FramePrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        if (!IsOwnedCombat()) { lastFrame = 0; return; }
        int phase = deploying!() ? 2 : searching!() ? 1 : 0;
        if (lastFrame != 0) samples[phase].Add(Stopwatch.GetElapsedTime(lastFrame, __state).TotalMilliseconds);
        lastFrame = __state;
    }

    private static void FramePostfix(long __state)
    {
        if (!IsOwnedCombat()) return;
        int phase = deploying!() ? 2 : searching!() ? 1 : 0;
        samples[phase].DispatcherMilliseconds += Stopwatch.GetElapsedTime(__state).TotalMilliseconds;
        double elapsed = Stopwatch.GetElapsedTime(0).TotalSeconds;
        if (elapsed - lastWrite >= 15) { lastWrite = elapsed; WriteProfile(); }
    }

    private static void WriteProfile()
    {
        if (profilePath == null) return;
        SolverSlowCalls.Flush();
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath, JsonSerializer.Serialize(new
        {
            presentationSuppressed = active && SuppressPresentation, suppressionEnabled = SuppressPresentation, suppressedUpdates,
            workerLimit, originalWorkers, effectiveWorkers,
            actualCaptureOriginalWorkers, actualCaptureEffectiveWorkers,
            workerEvidenceSource = "Actual SolverSettings.Capture Harmony return, excluding direct compatibility helper invocations.",
            maxSpeed = MaxSpeed,
            deferredRouteUpdates,
            lightweightDiagnostics = LightweightDiagnostics,
            compactResultCount = SolverLightweightDiagnostics.CompactResultCount,
            omittedRoutineCheckpointCount = SolverLightweightDiagnostics.DeferredRoutineCheckpointCount,
            description = "Godot dispatcher frame gaps by native solver phase; dispatcher duration excludes frame gap.",
            idle = samples[0], search = samples[1], deployment = samples[2]
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }

    private sealed class FrameSamples
    {
        public long Count;
        public double SumMilliseconds;
        public double MaxMilliseconds;
        public long Over33Milliseconds;
        public long Over50Milliseconds;
        public long Over100Milliseconds;
        public double DispatcherMilliseconds;
        public void Add(double gap)
        {
            Count++;
            SumMilliseconds += gap;
            MaxMilliseconds = Math.Max(MaxMilliseconds, gap);
            if (gap > 33.4) Over33Milliseconds++;
            if (gap > 50) Over50Milliseconds++;
            if (gap > 100) Over100Milliseconds++;
        }
    }
}

