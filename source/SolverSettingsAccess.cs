using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireAutopilot;

internal sealed record SolverParameter(string Name, string Label, string Group, string Description,
    Type Type, double? Minimum, double? Maximum, double? Step);

/// <summary>Edits the solver's latest immutable configuration through its own validation and save API.</summary>
internal static class SolverSettingsAccess
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type? settings;
    private static Type? controller;
    private static PropertyInfo[] properties = [];
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static IReadOnlyList<SolverParameter> GetFields()
    {
        Bind();
        return properties.Select(p => Describe(p)).OrderBy(p => p.Group).ThenBy(p => p.Label, StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyDictionary<string, object?> ReadValues()
    {
        Bind(); var current = settings!.GetProperty("Current", Static)!.GetValue(null)!;
        return properties.ToDictionary(p => p.Name, p => p.GetValue(current), StringComparer.Ordinal);
    }

    internal static void Save(IReadOnlyDictionary<string, object?> changed)
    {
        if (changed.Count == 0) return;
        Bind();
        var current = settings!.GetProperty("Current", Static)!.GetValue(null)!;
        var next = current.GetType().GetMethod("<Clone>$")!.Invoke(current, null)!;
        if (changed.TryGetValue("PerformancePreset", out var preset) && preset != null)
            next = Invoke(settings.GetMethod("ApplyPerformancePreset", Static)!, [next, preset])!;
        foreach (var (name, value) in changed)
        {
            var property = properties.SingleOrDefault(p => p.Name == name)
                ?? throw new ArgumentException("未知求解器参数：" + name);
            property.SetValue(next, ConvertValue(value, property.PropertyType));
        }
        if (changed.Keys.Any(k => k.StartsWith("Search", StringComparison.Ordinal)
            && k is "SearchTimeLimitSeconds" or "SearchBeamWidth" or "SearchPotionFreeBeamWidth" or "SearchPotionBeamWidth"
                or "SearchMaxExpandedNodes" or "SearchMaxCardBranchesPerNode" or "SearchMaxPileChoiceBranchesPerAction"
                or "SearchMaxHandChoiceBranchesPerAction"))
        {
            var presetProperty = properties.Single(p => p.Name == "PerformancePreset");
            presetProperty.SetValue(next, Enum.Parse(Nullable.GetUnderlyingType(presetProperty.PropertyType)!, "Custom"));
        }
        Invoke(settings.GetMethod("Update", Static)!, [next]);
        Synchronize(current, next);
    }

    internal static void ResetDefaults()
    {
        Bind(); var before = settings!.GetProperty("Current", Static)!.GetValue(null)!;
        Invoke(settings.GetMethod("ResetToDefaults", Static)!, []);
        Synchronize(before, settings.GetProperty("Current", Static)!.GetValue(null)!);
    }

    internal static object? ParseComplex(string name, string value)
    {
        Bind(); var property = properties.Single(p => p.Name == name);
        return JsonSerializer.Deserialize(value, property.PropertyType, Json)
            ?? throw new InvalidDataException("请填写有效的结构化参数。");
    }
    internal static string FormatComplex(object? value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Json);

    private static void Synchronize(object before, object after)
    {
        var snapshot = Invoke(settings!.GetMethod("Capture", Static)!, []);
        Invoke(controller!.GetMethod("ApplyPersistentSettings", Static)!, [snapshot]);
        var disabled = properties.Single(p => p.Name == "SolverDisabled");
        if (!Equals(disabled.GetValue(before), disabled.GetValue(after)))
            Invoke(controller.GetMethod("SetSolverDisabled", Static)!, [disabled.GetValue(after), false]);
        var automatic = properties.Single(p => p.Name == "AutomaticCalculationEnabled");
        if (!Equals(automatic.GetValue(before), automatic.GetValue(after)))
            Invoke(controller.GetMethod("SetAutomaticCalculationEnabled", Static)!, [automatic.GetValue(after), false]);
        var overlay = controller.Assembly.GetType("CombatSolver.SolverOverlay")!;
        foreach (string method in new[] { "ApplyOverlayOpacity", "RefreshControls" })
            if (overlay.GetMethod(method, Static, Type.EmptyTypes) is { } apply) Invoke(apply, []);
        var theme = properties.Single(p => p.Name == "OverlayTheme");
        if (!Equals(theme.GetValue(before), theme.GetValue(after)))
            Invoke(overlay.GetMethod("ApplyConfiguredTheme", Static, Type.EmptyTypes)!, []);
    }

    private static object? ConvertValue(object? value, Type type)
    {
        if (value == null) return null;
        var scalar = Nullable.GetUnderlyingType(type) ?? type;
        if (scalar.IsInstanceOfType(value)) return value;
        return scalar.IsEnum ? Enum.Parse(scalar, value.ToString()!) : Convert.ChangeType(value, scalar, CultureInfo.InvariantCulture);
    }
    private static object? Invoke(MethodInfo method, object?[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void Bind()
    {
        if (settings != null) return;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("CombatSolver.SolverSettings") != null)
            ?? throw new InvalidOperationException("请先启用 CombatSolver 求解器。");
        settings = assembly.GetType("CombatSolver.SolverSettings")!;
        controller = assembly.GetType("CombatSolver.SolverController")!;
        var data = settings.GetProperty("Current", Static)!.PropertyType;
        properties = data.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod != null && !p.Name.StartsWith("Legacy", StringComparison.Ordinal)
                && p.Name != "PerformanceMigrationVersion").ToArray();
    }

    private static SolverParameter Describe(PropertyInfo p)
    {
        var (label, group, help) = Labels.GetValueOrDefault(p.Name, (p.Name, "其他", "沿用求解器原生校验并保存。"));
        var (min, max, step) = p.Name switch
        {
            "MaxCombatSolveSeconds" => (1d, 600d, 1d),
            "SearchTimeLimitSeconds" => (.1, 600d, .1),
            "SearchMaxDegreeOfParallelism" => (1d, 16d, 1d),
            "SearchBeamWidth" => (1d, 512d, 1d),
            "SearchPotionFreeBeamWidth" or "SearchPotionBeamWidth" => (1d, 256d, 1d),
            "SearchMaxExpandedNodes" => (100d, int.MaxValue, 100d),
            "SearchMaxCardBranchesPerNode" or "SearchMaxPileChoiceBranchesPerAction" or "SearchMaxHandChoiceBranchesPerAction" => (1d, 100d, 1d),
            "NoGcRegionBudgetGigabytes" => (1d, 256d, 1d),
            "AcceptableBattleHpLoss" => (0d, 100_000d, 1d),
            "BrightestFlameMaxHpLossLimit" => (0d, 1000d, 1d),
            "DeploymentInterActionDelaySeconds" => (0d, 3d, .05),
            "OverlayPositionX" or "OverlayPositionY" => (-100_000d, 100_000d, 1d),
            "OverlayWidth" => (400d, 100_000d, 1d),
            "OverlayHeight" => (300d, 100_000d, 1d),
            "OverlayOpacity" => (.25, 1d, .05),
            _ => (0d, 100_000d, 1d)
        };
        if (Nullable.GetUnderlyingType(p.PropertyType) != null) help += " “自定义”关闭时沿用求解器预设。";
        if (p.Name is "GrowthBudgets" or "PotionDirectives" or "RelicCounterRules")
            help += "\n结构：" + Structure(p.PropertyType, 0);
        return new(p.Name, label, group, help, p.PropertyType, min, max, step);
    }

    private static string Structure(Type type, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum) return string.Join(" / ", Enum.GetNames(type));
        if (type.IsArray) return "[ " + Structure(type.GetElementType()!, depth + 1) + " ]";
        if (type == typeof(string)) return "文本";
        if (type == typeof(bool)) return "true / false";
        if (type.IsPrimitive || type == typeof(decimal)) return "数值";
        if (depth > 2) return type.Name;
        return "{ " + string.Join(", ", type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod != null).Select(p => p.Name + ": " + Structure(p.PropertyType, depth + 1))) + " }";
    }

    private static readonly Dictionary<string, (string, string, string)> Labels = new(StringComparer.Ordinal)
    {
        ["SolverDisabled"] = ("停用求解器", "战斗与执行", "控制求解器启用状态。"),
        ["AutomaticCalculationEnabled"] = ("自动计算", "战斗与执行", "进入可操作战斗阶段后自动搜索。"),
        ["AutoEnableFullAuto"] = ("自动开启全自动战斗", "战斗与执行", "求解器原生全自动开关；领航开启时也会接管战斗。"),
        ["StopFullAutoOnCombatEnd"] = ("战斗结束后停止全自动", "战斗与执行", "求解器原生自动战斗策略。"),
        ["StopFullAutoOnDeathTurn"] = ("死亡路线时停止全自动", "战斗与执行", "求解器原生路线安全选项。"),
        ["StopFullAutoOnWorseRecalculation"] = ("重算结果变差时停止全自动", "战斗与执行", "求解器原生重算选项。"),
        ["DeploymentFastMode"] = ("出牌速度", "战斗与执行", "跟随游戏、普通、快速或即时。"),
        ["DeploymentInterActionDelaySeconds"] = ("出牌动作间隔（秒）", "战斗与执行", "求解器执行动作之间的等待。"),
        ["PerformancePreset"] = ("性能与搜索预设", "搜索性能", "选择预设会通过原生接口同步时间、宽度、节点和分支。"),
        ["MaxCombatSolveSeconds"] = ("战斗最大求解时长（秒）", "搜索性能", "新版求解器的整场搜索时长上限，范围 1–600 秒。"),
        ["SearchMaxDegreeOfParallelism"] = ("最大工作线程", "搜索性能", "范围 1–16；领航高级页的手动线程上限也参与限制。"),
        ["SearchTimeLimitSeconds"] = ("搜索软时间预算（秒）", "搜索性能", "范围 0.1–600；原子计算及收尾可能超出软预算。"),
        ["SearchBeamWidth"] = ("搜索束宽", "搜索性能", "每层保留的候选宽度；范围 1–512。"),
        ["SearchPotionFreeBeamWidth"] = ("无药路线束宽", "搜索性能", "兼容字段，实际统一搜索宽度由当前求解器决定。"),
        ["SearchPotionBeamWidth"] = ("用药路线束宽", "搜索性能", "兼容字段，实际统一搜索宽度由当前求解器决定。"),
        ["SearchMaxExpandedNodes"] = ("最大展开节点", "搜索性能", "控制搜索工作量，数值由原生求解器校验。"),
        ["SearchMaxCardBranchesPerNode"] = ("每节点出牌分支", "搜索性能", "范围 1–100。"),
        ["SearchMaxPileChoiceBranchesPerAction"] = ("每动作牌堆选择分支", "搜索性能", "范围 1–100。"),
        ["SearchMaxHandChoiceBranchesPerAction"] = ("每动作手牌选择分支", "搜索性能", "范围 1–100。"),
        ["UseBeamWidthPortfolio"] = ("多束宽组合搜索", "搜索性能", "增加不同束宽策略的搜索。"),
        ["UseNoveltyPortfolio"] = ("新颖度组合搜索", "搜索性能", "探索结构不同的路线。"),
        ["UseEarlyTurnExploration"] = ("前期回合扩展探索", "搜索性能", "求解器原生回合探索策略。"),
        ["AutoConfigureServerGc"] = ("配置多核内存回收", "内存管理", "运行时 GC 配置通常随下一次启动生效。"),
        ["EnableNoGcRegion"] = ("暂缓内存回收", "内存管理", "原生设置；当前多核 GC 和领航普通 GC 策略会决定实际模式。"),
        ["NoGcRegionBudgetGigabytes"] = ("暂缓回收预算（GB）", "内存管理", "范围 1–256；领航使用普通 GC，原生内存调度仍依据实际余量运行。"),
        ["PotionPolicy"] = ("总体药水策略", "药水与收益", "关闭、智能或至少用一瓶；该兼容项的持久化以原生版本为准。"),
        ["PotionDirectives"] = ("各槽位药水指令", "药水与收益", "JSON 数组；字段 Slot（从0开始）、PotionId、Directive。枚举参见下方结构说明。"),
        ["PotionHpThresholdOverrides"] = ("药水损血阈值", "药水与收益", "按药水 ID 设置使用阈值。"),
        ["PredictPotionReward"] = ("预测药水奖励", "药水与收益", "原生战斗预测策略。"),
        ["RelicStrategyEnabled"] = ("启用遗物计数策略", "药水与收益", "按遗物计数目标优化路线。"),
        ["RelicCounterRules"] = ("遗物计数规则", "药水与收益", "JSON 数组；按原生 RelicCounterRule 结构编辑。"),
        ["GrowthBudgets"] = ("永久成长收益预算", "药水与收益", "JSON 对象；每个卡牌收益字段表示可接受的收益代价预算。"),
        ["BrightestFlameMaxHpLossLimit"] = ("最亮火焰最大生命损失上限", "药水与收益", "范围 0–1000；留空沿用原生策略。"),
        ["IgnoreLongTermRewards"] = ("忽略局外长期收益", "药水与收益", "优先当前战斗表现；原生终局比较保留。"),
        ["ActTransitionBossHpStrategy"] = ("幕末首领血量策略", "战斗与执行", "推进优先或最小化损血。"),
        ["FinalBossHpStrategy"] = ("最终首领血量策略", "战斗与执行", "推进优先或最小化损血。"),
        ["AcceptableBattleHpLoss"] = ("可接受战斗损血", "战斗与执行", "范围 0–100000；与达标停止选项共同生效。"),
        ["StopAtAcceptableBattleHpLoss"] = ("达到损血目标后结束搜索", "战斗与执行", "由原生求解器决定达标路线。"),
        ["OnlineStatisticsEnabled"] = ("在线统计", "诊断与提示", "求解器原生统计设置。"),
        ["EnableDetailedDiagnosticLogs"] = ("详细诊断日志", "诊断与提示", "用于定位问题；详细采样会增加开销。"),
        ["RetainAllCombatLogs"] = ("保留所有战斗日志", "诊断与提示", "求解器日志保留策略。"),
        ["ShowBattleDamagePerformanceHint"] = ("战损性能提示", "诊断与提示", "显示原生战损性能说明。"),
        ["ShowActTransitionBossHpStrategyHint"] = ("幕末首领策略提示", "诊断与提示", "显示策略说明。"),
        ["ShowFinalBossHpStrategyHint"] = ("最终首领策略提示", "诊断与提示", "显示策略说明。"),
        ["ShowNoveltyPortfolioHint"] = ("新颖度组合提示", "诊断与提示", "显示原生组合说明。"),
        ["ShowSpeedXWarning"] = ("倍速说明提示", "诊断与提示", "显示原生游戏倍速说明。"),
        ["SearchCompletionNotificationsEnabled"] = ("搜索完成通知", "诊断与提示", "领航接管时统一使用当前阶段小窗。"),
        ["SearchCompletionNotificationMode"] = ("搜索通知时机", "诊断与提示", "游戏后台时或始终。"),
        ["OverlayTheme"] = ("原求解器窗口主题", "窗口与外观", "影响原求解器窗口；领航直播窗采用领航配色。"),
        ["OverlayOpacity"] = ("原求解器窗口不透明度", "窗口与外观", "数值范围沿用原生校验。"),
        ["OverlayPositionX"] = ("原求解器窗口 X", "窗口与外观", "X/Y 同时自定义或同时恢复默认。"),
        ["OverlayPositionY"] = ("原求解器窗口 Y", "窗口与外观", "X/Y 同时自定义或同时恢复默认。"),
        ["OverlayWidth"] = ("原求解器窗口宽度", "窗口与外观", "宽高同时自定义或同时恢复默认。"),
        ["OverlayHeight"] = ("原求解器窗口高度", "窗口与外观", "宽高同时自定义或同时恢复默认。"),
        ["ReporterContactQq"] = ("反馈联系 QQ", "诊断与提示", "选填，保存到求解器自己的配置。")
    };
}
