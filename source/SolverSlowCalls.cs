using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace SpireAutopilot;

/// <summary>Opt-in inclusive timings of synchronous native/solver call segments.</summary>
internal static class SolverSlowCalls
{
    private static readonly Dictionary<MethodBase, CallSamples> Calls = new();
    private static readonly Queue<object> SlowEvents = new();
    private static readonly List<string> Missing = new();
    private static readonly List<object> Failed = new();
    private static readonly HashSet<MethodInfo> Attempted = new();
    private static readonly Harmony DiagnosticPatcher = new("SpireAutopilot.SolverSlowCalls");
    private static bool installed;
    private static Func<bool>? enabled;
    private static int mainThread;
    private static string? output;

    internal static void Install(Harmony patcher, Assembly solver, string profilePath, Func<bool> isActive)
    {
        if (installed) return;
        installed = true;
        enabled = isActive;
        mainThread = System.Environment.CurrentManagedThreadId;
        output = Path.Combine(Path.GetDirectoryName(profilePath)!, "slow-calls.json");
        foreach (var item in new (string Type, string[] Methods)[]
        {
            ("CombatSolver.SolverController", new[] { "DeployCurrentTurn", "EnqueueAndCaptureActionAsync", "AwaitDeploymentActionChoicesAsync", "FindCardForDeployment", "CaptureDeploymentActionChoices", "CaptureDeploymentEndTurnChoices", "CompleteSearchCore", "StartFullAutoDeployment", "MonitorCombatPresence", "RefreshSearchProgress" }),
            ("CombatSolver.LiveCombatStamp", new[] { "Capture" }),
            ("CombatSolver.ContinuationStamp", new[] { "CaptureLive" }),
            ("CombatSolver.NativeChoiceRuntime", new[] { "Begin", "Observe" }),
            ("CombatSolver.NativeChoiceSession", new[] { "SetPlanAndStartDriving", "DriveAsync", "ResolvePlannedCards", "ValidateImplicitSelection", "ReleaseVisibleSurface", "Dispose", "CompleteAndDetachAsync", "AwaitProducerAndCompleteAsync" }),
            ("CombatSolver.SearchGcPolicy", new[] { "CaptureRootSnapshotBarrier", "CollectGeneration2", "ReclaimIfPendingAsync", "ReclaimAfterReferenceReleaseAsync", "EndNoGcRegion", "EnterSearchScopeCore" }),
            ("CombatSolver.RunStatistics", new[] { "Activity" }),
            ("CombatSolver.CombatSolverLog", new[] { "Write" }),
            ("CombatSolver.CombatBugReportExporter", new[] { "RecordCheckpoint", "RecordCheckpointCore", "CaptureCheckpointCore", "CaptureReplayCheckpoint", "CaptureNativeCombatState", "CaptureInMemoryRunSave", "CaptureReplayState", "CaptureMetadataState" }),
            ("CombatSolver.CombatReplayRecording", new[] { "Start", "ObserveChoiceCandidates" }),
            ("CombatSolver.CombatShowcaseCollector", new[] { "TryQueueCompletedRoute" }),
            ("CombatSolver.SolverDiagnostics", new[] { "DescribeResult" }),
            ("CombatSolver.SolverOverlay", new[] { "ShowResult", "PopulateRoute", "ShowDeploying", "ShowDeploymentComplete" }),
            ("CombatSolver.SolverOverlaySnapshot", new[] { "CaptureWithReviewedWorldlines", "CaptureCurrentTurn" })
        })
            AddType(DiagnosticPatcher, solver.GetType(item.Type), item.Type, item.Methods);
        var game = typeof(CardModel).Assembly;
        foreach (var item in new (string Type, string[] Methods)[]
        {
            ("MegaCrit.Sts2.Core.Models.CardModel", new[] { "TryManualPlay", "CanPlayTargeting", "OnPlay" }),
            ("MegaCrit.Sts2.Core.GameActions.PlayCardAction", new[] { "ExecuteAction" }),
            ("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction", new[] { "ExecuteAction" }),
            ("MegaCrit.Sts2.Core.Commands.CardCmd", new[] { "Play", "AutoPlay" })
        })
            AddType(DiagnosticPatcher, game.GetType(item.Type), item.Type, item.Methods);
        Flush();
    }

    private static void AddType(Harmony patcher, Type? type, string typeName, string[] names)
    {
        if (type == null) { Missing.Add(typeName); return; }
        var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        foreach (var name in names)
        {
            var matched = methods.Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters).ToArray();
            if (matched.Length == 0) Missing.Add(typeName + "." + name);
            foreach (var method in matched)
            {
                Add(patcher, method, typeName + "." + name + " [call]");
                var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
                var advance = stateMachine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (advance != null) Add(patcher, advance, typeName + "." + name + " [sync continuation]");
            }
        }
    }

    private static void Add(Harmony patcher, MethodInfo method, string label)
    {
        if (!Attempted.Add(method)) return;
        // MonoMod's exception-region emitter rejects some compiler-generated async
        // MoveNext bodies. Keep their ordinary kickoff methods and expose this coverage gap.
        if (method.Name == "MoveNext" && method.GetMethodBody()?.ExceptionHandlingClauses.Count > 0)
        {
            Failed.Add(new { method = label, exception = "SkippedAsyncExceptionRegions",
                message = "Async continuation contains exception regions unsupported by this diagnostic emitter.", cleanup = "Patch was skipped before registration." });
            return;
        }
        try
        {
            Calls[method] = new CallSamples { Method = label };
            patcher.Patch(method, prefix: new HarmonyMethod(typeof(SolverSlowCalls), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(SolverSlowCalls), nameof(Postfix)));
        }
        catch (Exception error)
        {
            Calls.Remove(method);
            string cleanup = "Removed this diagnostic owner's registrations.";
            try { patcher.Unpatch(method, HarmonyPatchType.All, patcher.Id); }
            catch (Exception cleanupError)
            {
                cleanup = cleanupError.GetType().Name + ": " + cleanupError.Message;
            }
            Failed.Add(new { method = label, exception = error.GetType().Name, message = error.Message, cleanup });
        }
    }

    private static void Prefix(out Timing __state)
    {
        __state = default;
        if (System.Environment.CurrentManagedThreadId != mainThread || enabled?.Invoke() != true) return;
        __state = new Timing(Stopwatch.GetTimestamp(), GC.GetTotalPauseDuration().TotalMilliseconds, GC.CollectionCount(2));
    }

    private static void Postfix(MethodBase __originalMethod, Timing __state)
    {
        if (__state.Start == 0) return;
        double elapsed = Stopwatch.GetElapsedTime(__state.Start).TotalMilliseconds;
        var sample = Calls[__originalMethod];
        sample.Calls++;
        sample.TotalMilliseconds += elapsed;
        sample.MaxMilliseconds = Math.Max(sample.MaxMilliseconds, elapsed);
        if (elapsed < 8) return;
        sample.SlowCalls++;
        sample.SlowMilliseconds += elapsed;
        SlowEvents.Enqueue(new
        {
            method = sample.Method, elapsedMilliseconds = elapsed,
            gcPauseDeltaMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds - __state.GcPause,
            generation2Delta = GC.CollectionCount(2) - __state.Gen2,
            utc = DateTimeOffset.UtcNow.ToString("O")
        });
        while (SlowEvents.Count > 512) SlowEvents.Dequeue();
    }

    internal static void Flush()
    {
        if (output == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            minimumSlowMilliseconds = 8,
            scope = "Main-thread inclusive synchronous call segments. Async continuation timings exclude awaited idle time; nested calls overlap. Profiling observes without changing results.",
            patchedMethods = Calls.Count, missingTargets = Missing, failedTargets = Failed,
            calls = Calls.Values.OrderByDescending(c => c.SlowMilliseconds).ToArray(), slow_calls = SlowEvents.ToArray()
        }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
    }

    private readonly record struct Timing(long Start, double GcPause, int Gen2);
    private sealed class CallSamples
    {
        public string Method = "";
        public long Calls;
        public long SlowCalls;
        public double TotalMilliseconds;
        public double SlowMilliseconds;
        public double MaxMilliseconds;
    }
}
