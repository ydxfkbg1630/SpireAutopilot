using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Combat;

namespace SpireAutopilot;

internal sealed record Sts2SolverStatus(bool Available, bool IsSearching, bool IsDeploying, bool ComputationCompleted,
    double ElapsedSeconds, bool IsQuiescent, bool AutomationActive, SolverProgressView Progress, string Status);

/// <summary>Main-thread bridge to STS2 Solver's native command and cancellation lifecycle.</summary>
internal sealed class Sts2SolverBridge
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Harmony Patcher = new("SpireAutopilot.Sts2SolverOwnership");
    private static volatile bool blocked = true;
    private static bool patched;
    private static bool hideOverlay;
    private static CanvasLayer? ownedOverlay;
    private static bool overlayWasVisible;
    private static Type? controller;
    private static Type? session;
    private static Type? store;
    private static MethodInfo? setAuto, startSearch, executeCombat, stopExecution, cancelAndWait;
    private static FieldInfo? autoField;
    private bool? originalAuto;
    private bool owned, engaged;
    private Task<bool>? cancellation;
    private object? timedWorker;
    private long workerStarted;
    private object? combatIdentity;
    internal string Status { get; private set; } = "等待 STS2 Solver 加载";

    internal bool EnsureInstalled()
    {
        if (patched) return true;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("Sts2Solver.CombatSessionController", false) != null);
        if (assembly == null) return false;
        controller = assembly.GetType("Sts2Solver.CombatSessionController", true)!;
        session = assembly.GetType("Sts2Solver.SearchSession", true)!;
        store = assembly.GetType("Sts2Solver.CombatUiStore", true)!;
        setAuto = Required("SetAutoSearch", typeof(bool), typeof(bool)); startSearch = Required("StartSearch"); executeCombat = Required("ExecuteCombat");
        stopExecution = Required("StopExecution"); cancelAndWait = Required("CancelAndWaitAsync", typeof(NGame), typeof(string), typeof(int));
        autoField = controller.GetField("_autoSearch", Static) ?? throw new MissingFieldException(controller.FullName, "_autoSearch");
        Patcher.Patch(controller.GetMethod("RequestSearch", Static)!, prefix: new HarmonyMethod(typeof(Sts2SolverBridge), nameof(BlockSearch)));
        Patcher.Patch(controller.GetMethod("Execute", Static)!, prefix: new HarmonyMethod(typeof(Sts2SolverBridge), nameof(BlockExecute)));
        // Suppress autonomous entry points while another solver owns combat. Settings remain on disk untouched.
        var automatic = controller.GetProperty("ShouldAutomaticallySearchNextTurn", Static)?.GetMethod;
        if (automatic != null) Patcher.Patch(automatic, prefix: new HarmonyMethod(typeof(Sts2SolverBridge), nameof(BlockAutomatic)));
        var capability = controller.GetProperty("TurnSetupCapable", Static)?.GetMethod;
        if (capability != null) Patcher.Patch(capability, prefix: new HarmonyMethod(typeof(Sts2SolverBridge), nameof(BlockAutomatic)));
        var overlay = assembly.GetType("Sts2Solver.SolverOverlay", true)!;
        Patcher.Patch(overlay.GetMethod("_Process", Instance)!, postfix: new HarmonyMethod(typeof(Sts2SolverBridge), nameof(HidePrimaryOverlay)));
        patched = true; Status = "STS2 Solver 接口已绑定"; return true;
        MethodInfo Required(string name, params Type[] parameters) => controller!.GetMethod(name, Static, null, parameters, null) ?? throw new MissingMethodException(controller.FullName, name);
    }

    internal void PrepareOwnership()
    {
        if (!EnsureInstalled()) return;
        if (!owned) { originalAuto = (bool)autoField!.GetValue(null)!; engaged = false; }
        owned = true; blocked = false; hideOverlay = true;
        setAuto!.Invoke(null, new object[] { true, false });
    }

    internal bool EnsureAutomaticCombat()
    {
        if (!EnsureInstalled()) { Status = "请启用 STS2 Solver"; return false; }
        if (cancellation is { IsCompleted: false }) { Status = "等待主求解器原生取消完成"; return false; }
        var battle = CombatManager.Instance.DebugOnlyGetState();
        if (!ReferenceEquals(combatIdentity, battle)) { combatIdentity = battle; engaged = false; }
        if (!owned) PrepareOwnership();
        var snapshot = Snapshot();
        if (ReadBool(snapshot, "CombatRunActive") || ReadBool(snapshot, "IsDeploying")) { engaged = true; Status = "STS2 Solver 自动执行"; return true; }
        if (engaged && ReadBool(snapshot, "CombatRunPaused")) { Status = "STS2 Solver 已按原生条件暂停"; return false; }
        if (ReadBool(snapshot, "CurrentTurnRouteReady") && !ReadBool(snapshot, "IsSearching"))
        { bool accepted = (bool)executeCombat!.Invoke(null, null)!; engaged |= accepted; Status = accepted ? "STS2 Solver 接管本场" : "等待主求解器接受执行"; return accepted; }
        if (ReadBool(snapshot, "CanStartSearch") && !ReadBool(snapshot, "IsSearching")) startSearch!.Invoke(null, null);
        Status = ReadBool(snapshot, "IsSearching") ? "STS2 Solver 计算路线" : "等待主求解器操作阶段";
        return true;
    }

    internal Task<bool> CancelAndDisableAutomation(string reason = "pilot_handoff")
    {
        blocked = true;
        if (!EnsureInstalled()) return Task.FromResult(true);
        if (cancellation is { IsCompleted: false }) return cancellation;
        setAuto!.Invoke(null, new object[] { false, false }); stopExecution!.Invoke(null, null);
        cancellation = (Task<bool>)cancelAndWait!.Invoke(null, new object[] { NGame.Instance!, reason, 15000 })!;
        return cancellation;
    }

    internal void Pause()
    {
        _ = CancelAndDisableAutomation("pilot_pause");
        owned = false; engaged = false;
    }
    internal void Release()
    {
        if (cancellation is { IsCompleted: false }) return;
        if (patched && originalAuto is bool value) setAuto!.Invoke(null, new object[] { value, false });
        originalAuto = null; owned = false; engaged = false; blocked = false;
        hideOverlay = false;
        if (GodotObject.IsInstanceValid(ownedOverlay)) ownedOverlay!.Visible = overlayWasVisible;
        ownedOverlay = null;
    }
    internal Sts2SolverStatus Capture()
    {
        if (!EnsureInstalled()) return new(false, false, false, false, 0, true, false, new("waiting", "等待 STS2 Solver"), Status);
        object snapshot = Snapshot();
        var worker = session!.GetField("_worker", Static)?.GetValue(null) as Task;
        if (!ReferenceEquals(worker, timedWorker)) { timedWorker = worker; workerStarted = worker == null ? 0 : Stopwatch.GetTimestamp(); }
        bool searching = worker is { IsCompleted: false };
        bool deploying = ReadBool(snapshot, "IsDeploying") || ReadBool(snapshot, "TurnSetupDriving");
        bool running = (bool)session.GetProperty("IsRunning", Static)!.GetValue(null)!;
        bool busy = (bool)controller!.GetProperty("IsBusy", Static)!.GetValue(null)!;
        double elapsed = searching ? Stopwatch.GetElapsedTime(workerStarted).TotalSeconds : 0;
        bool complete = ReadBool(snapshot, "CurrentTurnRouteReady") || worker?.IsCompleted == true;
        var search = Read(snapshot, "Search");
        var progress = new SolverProgressView(searching ? "search" : deploying ? "deployment" : "waiting",
            searching ? "STS2 Solver 搜索路线" : deploying ? "STS2 Solver 自动出牌" : "等待主求解器",
            searching ? elapsed : null, ReadLong(search, "ExpandedNodes"), CurrentAction: Read(snapshot, "CurrentAction")?.ToString());
        return new(true, searching, deploying, complete, elapsed, !busy && !running && !deploying && (cancellation?.IsCompleted ?? true), ReadBool(snapshot, "CombatRunActive"), progress, Status);
    }
    private static object Snapshot() => store!.GetProperty("Snapshot", Static)!.GetValue(null)!;
    private static object? Read(object? value, string name) => value?.GetType().GetProperty(name, Instance)?.GetValue(value);
    private static bool ReadBool(object? value, string name) => Read(value, name) is true;
    private static long? ReadLong(object? value, string name) => Read(value, name) is { } number ? Convert.ToInt64(number) : null;
    private static bool BlockSearch() => !blocked;
    private static bool BlockExecute(ref bool __result) { if (!blocked) return true; __result = false; return false; }
    private static bool BlockAutomatic(ref bool __result) { if (!blocked) return true; __result = false; return false; }
    private static void HidePrimaryOverlay(CanvasLayer __instance)
    {
        if (!hideOverlay) return;
        if (!ReferenceEquals(ownedOverlay, __instance)) { ownedOverlay = __instance; overlayWasVisible = __instance.Visible; }
        __instance.Visible = false;
    }
}
