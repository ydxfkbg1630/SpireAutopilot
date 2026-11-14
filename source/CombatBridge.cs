using System;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace SpireAutopilot;

/// <summary>Game-thread bridge to CombatSolver's native full-auto controller.</summary>
public sealed class CombatBridge
{
    private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private Type? _controller;
    private MethodInfo? _setFullAuto;
    private MethodInfo? _canSolve;
    private MethodInfo? _canTakeOverTurnSetup;
    private object? _combat;
    private bool _ownsFullAuto;
    private bool _engagedThisCombat;
    private readonly Dictionary<string, Func<bool>> _booleanGetters = new(StringComparer.Ordinal);
    private long _nextProgressAt;
    private SolverProgressView _progress = new("waiting", "等待战斗求解器");

    public string Status { get; private set; } = "等待战斗";

    public SolverProgressView GetProgress()
    {
        var view = GetProgressCore();
        SolverProgressCapture.RecordDelivered(view);
        return view;
    }

    private SolverProgressView GetProgressCore()
    {
        long now = System.Environment.TickCount64;
        if (now < _nextProgressAt) return _progress;
        _nextProgressAt = now + 200;
        if (!CombatManager.Instance.IsInProgress)
            return _progress = new("waiting", Status);
        if (_controller == null && !BindController())
            return _progress = SolverProgressCapture.Current;
        bool search = ReadBoolean("IsSearching"), deploy = ReadBoolean("IsDeploying");
        var captured = SolverProgressCapture.Current;
        if (deploy && search)
            return _progress = new("transition", "切换求解与执行阶段", Detail: "等待求解器原生阶段同步");
        if (search) return _progress = SolverProgressCapture.ReadNativeSearch();
        if (deploy) return _progress = captured.Mode == "deployment" ? captured : new("deployment", "自动出牌");
        return _progress = captured.Mode == "waiting" ? captured : new("waiting", Status);
    }

    /// <summary>
    /// Returns true while the solver controls the native active combat. Call only on the
    /// Godot main thread. A solver safety stop stays paused until explicit disable/enable.
    /// No solver setting or persisted performance preference is modified.
    /// </summary>
    public bool TryEnsureAutomaticCombat(bool enabled)
    {
        if (!enabled)
        {
            PauseOwnedAutomation();
            return false;
        }

        var manager = CombatManager.Instance;
        var state = manager.DebugOnlyGetState();
        var host = NGame.Instance;
        if (!manager.IsInProgress || state == null || host == null)
        {
            ResetCombat();
            Status = "等待战斗";
            return false;
        }

        if (!ReferenceEquals(_combat, state))
        {
            ResetCombat();
            _combat = state;
        }

        if (!BindController())
            return false;

        if (ReadBoolean("IsMultiplayerSession"))
        {
            Status = "战斗桥接适用于单人游戏";
            return false;
        }
        if (ReadBoolean("SolverDisabled"))
        {
            Status = "请在战斗求解器设置中启用求解器";
            return false;
        }

        SolverPresentation.Activate(_controller!);
        if (SolverMemoryGuard.Poll())
        {
            Status = "内存保护已暂停求解；释放系统内存后按 F9 继续";
            _engagedThisCombat = true;
            return false;
        }

        bool active = ReadBoolean("FullAutoEnabled");
        if (active)
        {
            // The user explicitly started this pilot. Its pause button must also stop
            // a solver that was already automatic when the pilot took control.
            _ownsFullAuto = true;
            _engagedThisCombat = true;
            Status = ReadBoolean("IsDeploying") ? "求解器自动出牌" :
                ReadBoolean("IsSearching") ? "求解器计算路线" : "求解器接管战斗";
            return true;
        }
        if (_engagedThisCombat)
        {
            Status = "求解器已暂停；切换自动驾驶开关可重新接管";
            return false;
        }

        object?[] arguments = { state, string.Empty };
        bool playable = (bool)Invoke(_canSolve!, arguments)!;
        bool turnSetup = _canTakeOverTurnSetup != null
            && (bool)Invoke(_canTakeOverTurnSetup, new object?[] { state })!;
        if (!playable && !turnSetup)
        {
            Status = "等待战斗操作阶段：" + arguments[1];
            return false;
        }

        // SetFullAuto requests its own initial calculation and retains cross-turn routes.
        // Calling it once prevents repeated searches, route resets, and forced risk retries.
        Invoke(_setFullAuto!, new object?[] { host, state, true });
        active = ReadBoolean("FullAutoEnabled");
        _ownsFullAuto = active;
        _engagedThisCombat = active;
        Status = active ? "求解器接管战斗" : "等待求解器接受战斗";
        return active;
    }

    /// <summary>Releases only full-auto enabled by this bridge, through the solver's own API.</summary>
    public void PauseOwnedAutomation()
    {
        SolverPresentation.Restore();
        var state = CombatManager.Instance.DebugOnlyGetState();
        var host = NGame.Instance;
        if (_ownsFullAuto && _controller != null && state != null && host != null
            && ReferenceEquals(_combat, state) && CombatManager.Instance.IsInProgress)
        {
            // The solver completes its current atomic action according to its own lifecycle.
            Invoke(_setFullAuto!, new object?[] { host, state, false });
        }
        ResetCombat();
        Status = "自动驾驶已暂停";
    }

    private bool BindController()
    {
        if (_controller != null)
            return true;
        Type? controller = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("CombatSolver.SolverController", false))
            .FirstOrDefault(t => t != null);
        if (controller == null)
        {
            Status = "等待 CombatSolver 模组加载";
            return false;
        }
        _setFullAuto = controller.GetMethod("SetFullAuto", StaticMembers);
        _canSolve = controller.GetMethod("CanSolve", StaticMembers);
        if (_setFullAuto == null || _canSolve == null)
        {
            Status = "CombatSolver 接口版本需要 SetFullAuto 与 CanSolve";
            return false;
        }
        _canTakeOverTurnSetup = controller.Assembly.GetType("CombatSolver.PlayerTurnSetupCoordinator")
            ?.GetMethod("CanTakeOverTurnSetup", StaticMembers);
        foreach (string name in new[] { "FullAutoEnabled", "SolverDisabled", "IsMultiplayerSession", "IsSearching", "IsDeploying" })
        {
            if (controller.GetProperty(name, StaticMembers) == null)
            {
                Status = "CombatSolver 接口缺少属性：" + name;
                return false;
            }
            _booleanGetters[name] = controller.GetProperty(name, StaticMembers)!.GetMethod!.CreateDelegate<Func<bool>>();
        }
        _controller = controller;
        return true;
    }

    private bool ReadBoolean(string name)
        => _booleanGetters[name]();

    private object? Invoke(MethodInfo method, object?[] arguments)
    {
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            Status = "CombatSolver." + method.Name + "：" + exception.InnerException.GetType().Name
                + "：" + exception.InnerException.Message;
            throw new InvalidOperationException(Status, exception.InnerException);
        }
    }

    private void ResetCombat()
    {
        _combat = null;
        _ownsFullAuto = false;
        _engagedThisCombat = false;
        _nextProgressAt = 0;
        SolverProgressCapture.Reset();
    }
}
