using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace SpireAutopilot;

/// <summary>Explicit primary solver mode temporarily owns combat input, preserving the solver's saved preferences.</summary>
internal static class CombatSolverSuppression
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Harmony Patcher = new("SpireAutopilot.PrimarySolverOwnership");
    private static volatile bool active;
    private static bool installed;
    private static Type? controller;
    private static Action? cancelDeferred, cancelSearch, cancelDeployment, cancelSetup;
    private static MethodInfo? setFullAuto;
    internal static bool Active => active;
    internal static string Status { get; private set; } = "released";

    /// <summary>Call only on the game thread when entering primary solver mode.</summary>
    internal static void Activate()
    {
        bool wasActive = active;
        bool wasInstalled = installed;
        active = true;
        if (!installed && !TryInstall()) return;
        if (wasActive && wasInstalled) return;
        // The native cancellation paths retain in-flight action barriers and reference release.
        // Setup cancellation releases any visible native selection back to the game adapter;
        // a deferred setup observes the temporary disabled getter and invokes the original setup.
        cancelSetup!();
        cancelDeferred!();
        cancelSearch!();
        cancelDeployment!();
        if (NGame.Instance is { } host && CombatManager.Instance.DebugOnlyGetState() is { } state)
            Invoke(setFullAuto!, new object?[] { host, state, false });
        Status = "active:native_input_released_to_model";
    }

    /// <summary>Restores native getter and entry-point behavior without changing persisted solver settings.</summary>
    internal static void Release()
    {
        active = false;
        Status = "released";
    }

    private static bool TryInstall()
    {
        controller = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("CombatSolver.SolverController", false)).FirstOrDefault(t => t != null);
        if (controller == null) { Status = "active:standalone_model"; return false; }
        var setup = controller.Assembly.GetType("CombatSolver.PlayerTurnSetupCoordinator", false);
        var disabled = controller.GetProperty("SolverDisabled", Static)?.GetMethod;
        var automatic = controller.GetProperty("AutomaticCalculationEnabled", Static)?.GetMethod;
        var fullAuto = controller.GetProperty("FullAutoEnabled", Static)?.GetMethod;
        var request = controller.GetMethod("RequestSearch", Static);
        var deploy = controller.GetMethod("StartDeployment", Static);
        var setupDeploy = controller.GetMethod("StartDeploymentAfterTurnSetup", Static);
        setFullAuto = controller.GetMethod("SetFullAuto", Static);
        var deferred = controller.GetMethod("CancelDeferredSearch", Static, Type.EmptyTypes);
        var search = controller.GetMethod("CancelSearch", Static, Type.EmptyTypes);
        var deployment = controller.GetMethod("CancelDeployment", Static, Type.EmptyTypes);
        var cancel = setup?.GetMethod("CancelForSolverDisabled", Static, Type.EmptyTypes);
        if (new MethodInfo?[] { disabled, automatic, fullAuto, request, deploy, setupDeploy, setFullAuto, deferred, search, deployment, cancel }.Any(m => m == null)
            || request!.ReturnType != typeof(void) || deploy!.ReturnType != typeof(void)
            || setupDeploy!.ReturnType != typeof(void)
            || disabled!.ReturnType != typeof(bool) || automatic!.ReturnType != typeof(bool) || fullAuto!.ReturnType != typeof(bool)
            || setFullAuto!.ReturnType != typeof(void) || setFullAuto.GetParameters().Length != 3
            || setFullAuto.GetParameters()[2].ParameterType != typeof(bool))
        {
            Status = "unsupported:CombatSolver_model_ownership_api";
            throw new InvalidOperationException("当前 CombatSolver 接口需要更新主求解器接管适配。");
        }
        cancelDeferred = deferred!.CreateDelegate<Action>();
        cancelSearch = search!.CreateDelegate<Action>();
        cancelDeployment = deployment!.CreateDelegate<Action>();
        cancelSetup = cancel!.CreateDelegate<Action>();
        Patcher.Patch(disabled, prefix: new HarmonyMethod(typeof(CombatSolverSuppression), nameof(DisabledPrefix)));
        foreach (var getter in new[] { automatic, fullAuto })
            Patcher.Patch(getter, prefix: new HarmonyMethod(typeof(CombatSolverSuppression), nameof(AutomaticPrefix)));
        foreach (var entry in new[] { request, deploy, setupDeploy })
            Patcher.Patch(entry, prefix: new HarmonyMethod(typeof(CombatSolverSuppression), nameof(EntryPrefix)));
        Patcher.Patch(setFullAuto, prefix: new HarmonyMethod(typeof(CombatSolverSuppression), nameof(FullAutoPrefix)));
        installed = true;
        return true;
    }

    private static bool DisabledPrefix(ref bool __result)
    {
        if (!active) return true;
        __result = true;
        return false;
    }
    private static bool AutomaticPrefix(ref bool __result)
    {
        if (!active) return true;
        __result = false;
        return false;
    }
    private static bool EntryPrefix() => !active;
    private static bool FullAutoPrefix(bool __2) => !active || !__2;
    private static object? Invoke(MethodInfo method, object?[] parameters)
    {
        try { return method.Invoke(null, parameters); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
