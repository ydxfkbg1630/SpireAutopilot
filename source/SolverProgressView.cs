using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;

namespace SpireAutopilot;

public sealed record SolverProgressView(string Mode, string Phase, double? ElapsedSeconds = null,
    long? ExpandedNodes = null, double? SearchBudgetSeconds = null, int? CompletedActions = null,
    int? TotalActions = null, string? CurrentAction = null, string? Detail = null);

internal static class SolverProgressCapture
{
    private const BindingFlags Members = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<string, PropertyInfo> progressProperties = new();
    private static SolverProgressView current = new("waiting", "等待战斗求解器");
    internal static SolverProgressView Current => current;
    private static readonly string? probePath = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROGRESS_PROBE");
    private static readonly Queue<object> events = new();
    private static readonly Dictionary<string, long> modeCounts = new();
    private static readonly Dictionary<string, long> sourceCounts = new();
    private static long totalEvents;
    private static SolverProgressView? lastDelivered;
    private static FieldInfo? searchSessionField;
    private static PropertyInfo? interactionProperty;
    private static FieldInfo? immutableProgressField;
    private static PropertyInfo? progressDisplayProperty;
    private static PropertyInfo? searchStartedAtProperty;

    internal static void Install(Harmony patcher, Assembly solver)
    {
        var overlay = solver.GetType("CombatSolver.SolverOverlay", true)!;
        var progress = solver.GetType("CombatSolver.SolverProgress", true)!;
        var controller = solver.GetType("CombatSolver.SolverController", true)!;
        searchSessionField = controller.GetField("_search", Members)
            ?? throw new MissingFieldException(controller.FullName, "_search");
        interactionProperty = searchSessionField.FieldType.GetProperty("Interaction", Members)
            ?? throw new MissingMemberException(searchSessionField.FieldType.FullName, "Interaction");
        var interaction = interactionProperty.PropertyType;
        immutableProgressField = interaction.GetField("Progress", Members)
            ?? throw new MissingFieldException(interaction.FullName, "Progress");
        progressDisplayProperty = interaction.GetProperty("ProgressDisplay", Members)
            ?? throw new MissingMemberException(interaction.FullName, "ProgressDisplay");
        searchStartedAtProperty = progressDisplayProperty.PropertyType.GetProperty("StartedAtTick", Members)
            ?? throw new MissingMemberException(progressDisplayProperty.PropertyType.FullName, "StartedAtTick");
        foreach (string name in new[] { "ElapsedMilliseconds", "ExpandedNodes", "RequestBudgetMilliseconds", "Phase" })
            progressProperties[name] = progress.GetProperty(name, Members)
                ?? throw new MissingMemberException(progress.FullName, name);
        Patch("ShowProgress", nameof(ProgressPrefix));
        Patch("ShowSearching", nameof(SearchingPrefix));
        Patch("ShowDeploymentStep", nameof(DeploymentPrefix));
        Patch("ShowEndTurnDeploymentStep", nameof(EndTurnPrefix));
        Patch("ShowDeploymentComplete", nameof(CompletePrefix));

        void Patch(string method, string prefix)
            => patcher.Patch(overlay.GetMethod(method, Members)
                ?? throw new MissingMethodException(overlay.FullName, method),
                prefix: new HarmonyMethod(typeof(SolverProgressCapture), prefix) { priority = Priority.First });
    }

    private static void ProgressPrefix(object __0)
    {
        if (!SolverPresentation.Active) return;
        ReadProgress(__0, "native_ShowProgress");
    }

    private static void ReadProgress(object progress, string source)
    {
        long milliseconds = Convert.ToInt64(progressProperties["ElapsedMilliseconds"].GetValue(progress));
        long expanded = Convert.ToInt64(progressProperties["ExpandedNodes"].GetValue(progress));
        long budget = Convert.ToInt64(progressProperties["RequestBudgetMilliseconds"].GetValue(progress));
        current = new("search", (string?)progressProperties["Phase"].GetValue(progress) ?? "计算路线",
            milliseconds / 1000d, expanded, budget > 0 ? budget / 1000d : null,
            Detail: "求解阶段；时间预算为搜索上限");
        Record(source);
    }

    /// <summary>Called by the 5Hz bridge sampler. Reads immutable worker telemetry without invoking UI rendering.</summary>
    internal static SolverProgressView ReadNativeSearch()
    {
        var session = searchSessionField?.GetValue(null);
        if (session == null) return current.Mode == "search" ? current : new("search", "等待搜索初始化");
        var interaction = interactionProperty!.GetValue(session);
        // Worker publishes a fully constructed immutable SolverProgress with Volatile.Write.
        // Read the reference atomically and apply an acquire barrier before reading its fields.
        var raw = immutableProgressField!.GetValue(interaction);
        System.Threading.Thread.MemoryBarrier();
        if (raw != null)
        {
            ReadProgress(raw, "native_search_progress_read");
            return current;
        }
        var display = progressDisplayProperty!.GetValue(interaction);
        long started = (long)searchStartedAtProperty!.GetValue(display)!;
        current = new("search", "等待首批搜索结果",
            Math.Max(0, System.Environment.TickCount64 - started) / 1000d,
            Detail: "已用时间来自原生搜索会话起点");
        Record("native_search_start_clock_read");
        return current;
    }

    private static void SearchingPrefix()
    {
        if (SolverPresentation.Active) { current = new("search", "开始计算路线", 0, 0); Record("native_ShowSearching"); }
    }

    private static void DeploymentPrefix(int completedActions, int actionCount, string? currentCardTitle)
    {
        if (SolverPresentation.Active)
        {
            current = new("deployment", "自动出牌", CompletedActions: completedActions,
                TotalActions: actionCount, CurrentAction: currentCardTitle);
            Record("native_ShowDeploymentStep");
        }
    }

    private static void EndTurnPrefix()
    {
        if (SolverPresentation.Active) { current = current with { Mode = "deployment", Phase = "结束回合", CurrentAction = "结束回合" }; Record("native_ShowEndTurnDeploymentStep"); }
    }

    private static void CompletePrefix(int actionCount, bool endedTurn)
    {
        if (SolverPresentation.Active)
        {
            current = new("waiting", endedTurn ? "等待下一回合" : "回合执行完成",
                CompletedActions: actionCount, TotalActions: actionCount);
            Record("native_ShowDeploymentComplete");
        }
    }

    internal static void Reset() { current = new("waiting", "等待战斗求解器"); Record("bridge_reset"); }

    internal static void RecordDelivered(SolverProgressView progress)
    {
        if (string.IsNullOrWhiteSpace(probePath)) return;
        if (lastDelivered == progress) return;
        lastDelivered = progress;
        Record("host_GetProgress", progress);
    }

    private sealed record Delivered(string Source, DateTimeOffset Utc, SolverProgressView View);
    private static void Record(string source, SolverProgressView? view = null)
    {
        if (string.IsNullOrWhiteSpace(probePath)) return;
        view ??= current;
        totalEvents++;
        modeCounts[view.Mode] = modeCounts.GetValueOrDefault(view.Mode) + 1;
        sourceCounts[source] = sourceCounts.GetValueOrDefault(source) + 1;
        events.Enqueue(new Delivered(source, DateTimeOffset.UtcNow, view));
        while (events.Count > 128) events.Dequeue();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(probePath))!);
        File.WriteAllText(probePath, JsonSerializer.Serialize(new
        {
            scope = "Actual native callbacks and 5Hz host delivery, no estimated completion percentage.",
            totalEvents, retainedEvents = events.Count, modeCounts, sourceCounts, events = events.ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
