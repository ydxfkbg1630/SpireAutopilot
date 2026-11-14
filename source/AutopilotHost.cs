using Godot;

namespace SpireAutopilot;

public partial class AutopilotHost : Node
{
    private readonly ProviderClient provider = new();
    private readonly ProviderClient reflectionProvider = new();
    private BattleExperienceStore experience = null!;
    private string? observedBattle;
    private int reflectionFailures;
    private readonly CombatBridge combat = new();
    private readonly CombatHandoffPolicy handoff = new();
    private readonly GameAdapter game = new();
    private readonly DecisionContext decisionContext = new();
    private RoutePlanner route = null!;
    private long countedPromptTokens;
    private long countedCachedTokens;
    private int countedRequests;
    private int totalRequests;
    private int savedModelCalls;
    private AutopilotPanel panel = null!;
    private AutopilotConfig config = null!;
    private CancellationTokenSource? pending;
    private bool enabled;
    private bool busy;
    private bool exiting;
    private long generation;
    private double clock;
    private double nextPoll;
    private double nextPresentation;
    private double nextCombatControl;
    private double modelStartedAt;
    private bool modelThinking;
    private bool isolatedProbe;
    private string presentationOwner = "";
    private string presentationKey = "";
    private string? lastActionToken;
    private double lastActionTime;
    private string? retryToken;
    private int retryCount;
    private readonly Queue<string> history = new();
    private string ConfigPath => Path.Combine(OS.GetUserDataDir(), "spire_autopilot.json");
    public override void _Ready()
    {
        try { config = AutopilotConfig.Load(ConfigPath); }
        catch (Exception ex) { config = new(); GD.PrintErr($"[SpireAutopilot] Config: {ex.GetType().Name}"); }
        route = new RoutePlanner(Path.Combine(OS.GetUserDataDir(), "spire_autopilot_route.json"));
        experience = new BattleExperienceStore(Path.Combine(OS.GetUserDataDir(), "spire_autopilot_experience"));
        panel = new AutopilotPanel();
        AddChild(panel);
        panel.Initialize(config, SaveConfig, Toggle, TestConnection);
        isolatedProbe = !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE"));
        panel.SetStatus("准备就绪：填写接口，F9 开始领航。", false);
        if (System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_SETTINGS_PROBE") is { Length: > 0 } settingsProbe)
            _ = SolverSettingsProbe.RunAsync(this, settingsProbe);
        else if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_MODEL_COMBAT_PROBE")))
            _ = ModelCombatProbe.RunAsync(this);
        else if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_HYBRID_PROBE")))
            _ = HybridCombatProbe.RunAsync(this);
        else if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_CARD_CHOICE_PROBE")))
            _ = CardChoiceProbe.RunAsync(this);
        else if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_SCENE_PROBE")))
            _ = SceneProbe.RunAsync(this);
        else if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE")))
            _ = NativeProbe.RunAsync(this);
    }
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.F8) { panel.ToggleVisible(); GetViewport().SetInputAsHandled(); }
        if (key.Keycode == Key.F9) { Toggle(); GetViewport().SetInputAsHandled(); }
        if (key.Keycode == Key.F10) { panel.ToggleMonitor(); GetViewport().SetInputAsHandled(); }
    }
    public override void _Process(double delta)
    {
        clock += delta;
        if (enabled && !exiting && clock >= nextCombatControl)
        {
            nextCombatControl = clock + .1;
            try { UpdateCombatControl(); }
            catch (Exception ex) { Pause("战斗接管需要处理：" + ex.Message); }
        }
        if ((enabled || isolatedProbe) && !exiting && clock >= nextPresentation)
        {
            nextPresentation = clock + .2;
            UpdatePresentation();
        }
        if (!enabled || busy || clock < nextPoll) return;
        nextPoll = clock + .5;
        _ = TickAsync();
    }
    private void SaveConfig(AutopilotConfig value)
    {
        Pause("配置已保存，F9 开始领航。");
        provider.ResetSession();
        reflectionProvider.ResetSession();
        decisionContext.Reset();
        handoff.Reset();
        config = value;
        config.Save(ConfigPath);
    }
    private void Toggle()
    {
        if (enabled) { Pause("已暂停，可手动操作；F9 继续。"); return; }
        try { config = panel.ReadConfig(); config.Save(ConfigPath); }
        catch (Exception ex) { SetStatus("配置保存失败：" + ex.Message); return; }
        if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.Model))
        { SetStatus("请填写接口 URL 和模型名。"); panel.ShowPanel(); return; }
        enabled = true;
        reflectionFailures = 0;
        SolverSearchStatusCapture.EnsureInstalled();
        UpdateCombatControl();
        SolverPresentation.WorkerLimit = config.SolverWorkerLimit;
        SolverPresentation.MaxSpeed = config.SolverMaxSpeed;
        SolverPresentation.LightweightDiagnostics = config.LightweightSolverDiagnostics;
        if (config.AutoCombat && ModelControlsCombat) { combat.PrepareOwnership(); SolverModelOwnership.Activate(); }
        else if (config.AutoCombat) combat.PrepareOwnership();
        panel.ShowMonitor();
        generation++;
        lastActionToken = null;
        retryToken = null;
        retryCount = 0;
        SetStatus("领航已启动，等待稳定场景。");
        BeginCurrent("领航", "准备就绪", "start:" + generation);
    }
    private void Pause(string reason)
    {
        enabled = false;
        generation++;
        pending?.Cancel();
        combat.PauseOwnedAutomation();
        SolverModelOwnership.Release();
        modelThinking = false;
        BeginCurrent("暂停", "领航已暂停", "pause:" + generation);
        panel.SetCurrentDetails(Redact(reason));
        SetStatus(reason);
    }
    private void SetStatus(string text)
    {
        if (!exiting && GodotObject.IsInstanceValid(panel)) panel.SetStatus(Redact(text), enabled);
    }
    private string Redact(string text) => string.IsNullOrEmpty(config?.ApiKey) ? text : text.Replace(config.ApiKey, "[密钥已隐藏]", StringComparison.Ordinal);
    private void BeginCurrent(string owner, string phase, string key)
    {
        presentationOwner = owner;
        presentationKey = key;
        panel.BeginCurrent(owner, phase, key);
    }
    private bool ModelControlsCombat => config.CombatMode == "Model" || config.CombatMode == "Hybrid" && handoff.UsesModel;

    private void UpdateCombatControl()
    {
        if (!config.AutoCombat) { handoff.Reset(); return; }
        string? battleId = game.InCombat ? game.CombatIdentity : null;
        bool ownedByModel = handoff.UsesModel;
        var search = SolverSearchStatusCapture.Capture();
        bool switched = handoff.Observe(battleId, config.CombatMode, search.IsRunning,
            search.ElapsedSeconds, search.ComputationCompleted, search.IsDeploying);
        if (config.CombatMode == "Hybrid" && ownedByModel && !handoff.UsesModel)
            SolverModelOwnership.Release();
        if (!switched) return;
        combat.PrepareOwnership();
        SolverModelOwnership.Activate();
        lastActionToken = null;
        retryToken = null;
        retryCount = 0;
        BeginCurrent("模型", "求解满 20 秒，模型接管", "handoff:" + battleId);
        panel.SetCurrentDetails("正在结束本次求解，随后评估当前手牌、药水和敌人意图。");
        Record("本次求解达到 20 秒，本场后续由模型出牌并评估药水；下一场重新使用求解器。");
    }
    private void UpdatePresentation()
    {
        if (game.InCombat && !ModelControlsCombat)
        {
            modelThinking = false;
            presentationOwner = "战斗";
            var progress = combat.GetProgress();
            if (config.CombatMode == "Hybrid" && progress.Mode == "search")
            {
                var search = SolverSearchStatusCapture.Capture();
                if (search.IsRunning) progress = progress with
                { ElapsedSeconds = search.ElapsedSeconds, Detail = "本次求解满 20 秒时交给模型。" };
            }
            panel.SetSolverProgress(progress);
            return;
        }
        if (presentationOwner == "战斗") BeginCurrent("领航", "等待下一个场景", "combat-ended");
        if (modelThinking && presentationOwner == "模型")
            panel.SetCurrentMetrics($"当前请求  {Math.Max(0, clock - modelStartedAt):F1}s");
    }
    private static string SceneTitle(string scene) => scene switch
    {
        "NMapScreen" => "规划本幕路线",
        "Combat" => "评估本次出牌",
        "BattleReflection" => "整理本场战斗经验",
        "NEventRoom" => "评估事件选项",
        "NRewardsScreen" => "选择战斗奖励",
        "NCardRewardSelectionScreen" => "选择新卡牌",
        "NRestSiteRoom" => "选择休息安排",
        "NStandardMerchantRoom" or "NMerchantInventory" => "评估商店商品",
        "NDeckUpgradeSelectScreen" => "选择升级卡牌",
        "NDeckCardSelectScreen" or "NSimpleCardSelectScreen" or "NCombatPileCardSelectScreen" or "HandCardSelect" => "评估当前选牌",
        "NTreasureRoom" => "领取宝箱奖励",
        _ => "评估当前局面"
    };
    private void Record(string text)
    {
        text = Redact(text);
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        history.Enqueue(line);
        while (history.Count > 30) history.Dequeue();
        if (!exiting && GodotObject.IsInstanceValid(panel)) panel.SetHistory(string.Join('\n', history));
        // Only scene/action labels are logged. Provider request headers and credentials stay private.
        GD.Print("[SpireAutopilot] " + text);
    }
    private async Task TestConnection()
    {
        if (busy) return;
        busy = true;
        long testGeneration = generation;
        using var cts = new CancellationTokenSource();
        pending = cts;
        try
        {
            var candidate = panel.ReadConfig();
            BeginCurrent("连接", "正在测试模型", "connection:" + clock);
            SetStatus("正在测试模型连接…");
            string result = await provider.TestAsync(candidate, cts.Token);
            if (!exiting && generation == testGeneration)
            {
                string safeResult = string.IsNullOrEmpty(candidate.ApiKey) ? result : result.Replace(candidate.ApiKey, "[密钥已隐藏]");
                SetStatus(safeResult);
                BeginCurrent("连接", "连接成功", "connection-result:" + clock);
                panel.SetCurrentDetails(safeResult);
            }
        }
        catch (OperationCanceledException)
        {
            if (!exiting && generation == testGeneration) { BeginCurrent("连接", "连接测试已取消", "connection-cancelled"); SetStatus("连接测试已取消。"); }
        }
        catch (Exception ex)
        {
            if (!exiting && generation == testGeneration) { BeginCurrent("连接", "请检查连接设置", "connection-error:" + clock); panel.SetCurrentDetails(Redact(ex.Message)); SetStatus("连接测试：" + ex.Message); }
        }
        finally { pending = null; busy = false; }
    }
    private async Task TickAsync()
    {
        busy = true;
        long requestGeneration = generation;
        string? requestToken = null;
        bool awaitingModel = false;
        bool modelDecisionReturned = false;
        using var cts = new CancellationTokenSource();
        pending = cts;
        try
        {
            UpdateCombatControl();
            if (config.ReflectAfterCombat && game.InCombat && game.CaptureCombatObservation() is { } observation)
            {
                string id = game.CombatIdentity ?? "combat";
                if (observedBattle != id)
                {
                    observedBattle = id;
                    reflectionFailures = 0;
                    experience.BeginBattle(game.RunIdentity ?? "run", id, new { combat = ExperienceObservation(observation), run = game.CaptureRunObservation() });
                }
                experience.Observe(ExperienceObservation(observation));
            }
            else if (!game.InCombat && observedBattle != null)
            {
                experience.CreateReflectionFrame(new { state = game.CaptureRunObservation(), runFinished = game.RunFinished });
                observedBattle = null;
            }
            if (!game.InCombat && config.ReflectAfterCombat && experience.HasPendingReflection && reflectionFailures < 2)
            {
                var reflectionFrame = experience.CreateReflectionFrame(null)!;
                BeginProbeModel("BattleReflection", reflectionFrame.Token);
                bool reflectionReturned = false;
                bool reflectionAccepted = false;
                try
                {
                    var reflected = await reflectionProvider.DecideAsync(config, reflectionFrame, cts.Token);
                    reflectionReturned = true;
                    cts.Token.ThrowIfCancellationRequested();
                    if (!enabled || exiting || generation != requestGeneration) return;
                    if (experience.CommitReflection(reflectionFrame.Token, reflected, config.ApiKey))
                    {
                        reflectionAccepted = true;
                        reflectionFailures = 0;
                        RecordUsage(reflected.Usage);
                        panel.SetCurrentDetails("本场出牌与怪物应对经验已保存。");
                        Record("本场经验已写入本地爬塔经验文档。");
                    }
                    else { reflectionFailures++; Record("战后经验内容待重新获取。"); }
                }
                catch (Exception ex) when (ex is ModelProtocolException or ProviderTransientException or TimeoutException)
                { reflectionFailures++; Record("战后经验请求待重试：" + ex.GetType().Name); }
                finally { if (reflectionReturned && !reflectionAccepted) reflectionProvider.DiscardLastDecision(); }
                return;
            }
            if (game.RunFinished) { Pause("本局结束，领航已停止。"); return; }
            if (game.TryHandleTutorial()) { SetStatus("已确认教学提示。"); return; }
            if (game.InCombat)
            {
                SolverPresentation.WorkerLimit = config.SolverWorkerLimit;
                SolverPresentation.MaxSpeed = config.SolverMaxSpeed;
                SolverPresentation.LightweightDiagnostics = config.LightweightSolverDiagnostics;
                if (config.AutoCombat && ModelControlsCombat)
                {
                    combat.PrepareOwnership();
                    SolverModelOwnership.Activate();
                    if (!SolverSearchStatusCapture.Capture().IsQuiescent)
                    { SetStatus("模型接管中：等待求解器完成原生任务交接。"); return; }
                }
                else if (config.AutoCombat)
                    SetStatus(combat.TryEnsureAutomaticCombat(true) ? "战斗由 CombatSolver 自动执行。" : combat.Status);
                else SetStatus("战斗阶段：等待手动或求解器执行。");
                if (!config.AutoCombat || !ModelControlsCombat) return;
            }
            game.AllowNativeUiFallback = config.AllowNativeUiFallback;
            bool combatDecision = game.InCombat && ModelControlsCombat;
            DecisionFrame? frame = combatDecision ? game.CaptureCombatFrame() : game.Capture();
            if (frame is null)
            {
                BeginCurrent("领航", "等待场景就绪", "waiting:" + game.Status);
                SetStatus(game.Status);
                return;
            }
            requestToken = frame.Token;
            if (lastActionToken == frame.Token)
            {
                if (clock - lastActionTime > 15) Pause("上一步仍待场景确认，已暂停。F9 可重新读取。");
                return;
            }
            route.Observe(frame, config.Strategy);
            panel.SetPlan(Redact(route.Summary));
            if (route.PlannedTravel(frame) is { } planned)
            {
                savedModelCalls++;
                Record($"沿本幕规划前进 → {planned.Label}");
                BeginCurrent("地图", "沿路线前进", frame.Token);
                panel.SetDecision("执行已规划的地图路线：" + planned.Label);
                await game.ExecuteAsync(frame.Token, planned.Id);
                lastActionToken = frame.Token;
                lastActionTime = clock;
                nextPoll = clock + Math.Max(.3, config.DecisionDelaySeconds);
                SetStatus("沿规划路线前进。");
                return;
            }
            if (config.LocalMechanicalActions && LocalDecisionPolicy.Choose(frame) is { } mechanical)
            {
                savedModelCalls++;
                Record("本地执行：" + mechanical.Label + "（已省一次模型请求）");
                BeginCurrent("执行", "完成当前操作", frame.Token);
                panel.SetDecision("本次为明确的原生进度操作，直接执行：" + mechanical.Label);
                if (combatDecision) await game.ExecuteCombatAsync(frame.Token, mechanical.Id);
                else await game.ExecuteAsync(frame.Token, mechanical.Id);
                lastActionToken = frame.Token;
                lastActionTime = clock;
                nextPoll = clock + Math.Max(.3, config.DecisionDelaySeconds);
                SetStatus("本地操作完成，累计节省 " + savedModelCalls + " 次模型请求。");
                return;
            }
            SetStatus($"{frame.Scene}：模型决策中（{frame.Actions.Count} 个合法动作）…");
            BeginProbeModel(frame.Scene, frame.Token);
            decisionContext.BattleLessons = config.UseBattleExperience ? experience.ReadLessons() : "";
            var requestFrame = decisionContext.Prepare(frame, route, history.TakeLast(6));
            awaitingModel = true;
            ModelDecision decision = await provider.DecideAsync(config, requestFrame, cts.Token);
            modelDecisionReturned = true;
            cts.Token.ThrowIfCancellationRequested();
            if (!enabled || exiting || requestGeneration != generation) { provider.DiscardLastDecision(); return; }
            RecordUsage(decision.Usage, false);
            DecisionFrame? fresh = combatDecision ? game.CaptureCombatFrame() : game.Capture();
            if (fresh?.Token != frame.Token)
            { provider.DiscardLastDecision(); Record("场景已更新，重新读取局面。"); nextPoll = clock + .5; return; }
            route.Accept(frame, decision);
            panel.SetPlan(Redact(route.Summary));
            modelThinking = false;
            panel.SetCurrentDetails(Redact(decision.ReasoningSummary is { Length: > 0 } summary ? summary : decision.Reason));
            SetCurrentUsage(decision.Usage);
            LegalAction action = frame.Actions.Single(item => item.Id == decision.ActionId);
            Record($"{frame.Scene} → {action.Label}；{decision.Reason}");
            if (combatDecision) await game.ExecuteCombatAsync(frame.Token, decision.ActionId);
            else await game.ExecuteAsync(frame.Token, decision.ActionId);
            decisionContext.Commit(requestFrame);
            awaitingModel = false;
            if (combatDecision && config.ReflectAfterCombat) experience.RecordAction(action.Label, decision.Reason);
            retryToken = null;
            retryCount = 0;
            lastActionToken = frame.Token;
            lastActionTime = clock;
            nextPoll = clock + Math.Max(.3, config.DecisionDelaySeconds);
            SetStatus("已执行：" + action.Label);
        }
        catch (OperationCanceledException) { if (modelDecisionReturned) provider.DiscardLastDecision(); if (!exiting && enabled && requestGeneration == generation) Pause("请求超时或取消，F9 重新尝试。"); }
        catch (Exception ex) when (awaitingModel && (ex is ModelProtocolException or ProviderTransientException or TimeoutException))
        {
            if (modelDecisionReturned) provider.DiscardLastDecision();
            if (exiting || !enabled || requestGeneration != generation) return;
            if (retryToken != requestToken) { retryToken = requestToken; retryCount = 0; }
            if (requestToken is not null && ++retryCount <= 2)
            {
                Record($"模型回复需要重新获取，重试 {retryCount}/2（{ex.GetType().Name}）。");
                SetStatus("正在重新读取局面并获取模型决策…");
                nextPoll = clock + retryCount;
            }
            else Pause("模型连续请求异常，已暂停。F9 可重新尝试。");
        }
        catch (Exception ex)
        {
            if (awaitingModel && modelDecisionReturned) provider.DiscardLastDecision();
            if (!exiting && requestGeneration == generation) Pause("领航暂停：" + ex.Message);
        }
        finally { modelThinking = false; pending = null; busy = false; }
    }
    private void RecordUsage(ProviderUsage? usage, bool showCurrent = true)
    {
        totalRequests++;
        if (usage is { PromptTokens: > 0, CachedTokens: >= 0 })
        {
            countedPromptTokens += usage.PromptTokens.Value;
            countedCachedTokens += usage.CachedTokens.Value;
            countedRequests++;
        }
        string last = usage is { PromptTokens: > 0, CachedTokens: >= 0 }
            ? $"本次缓存：{usage.CachedTokens}/{usage.PromptTokens} tokens（{100d * usage.CachedTokens / usage.PromptTokens:F1}%）"
            : "本次缓存：服务端待提供命中token统计";
        string aggregate = countedPromptTokens > 0 ? $"累计：{100d * countedCachedTokens / countedPromptTokens:F1}%（{countedRequests}/{totalRequests}次含统计）" : "累计缓存：等待服务端统计";
        string misses = usage is { PromptTokens: not null, CachedTokens: not null } ? $"\n本次新增未缓存：{usage.PromptTokens.Value - usage.CachedTokens.Value} tokens" : "";
        string session = usage?.Session is { } info ? $"\n会话：{SessionMode(info.Mode)}，第{info.TurnCount}轮，输入{info.InputBytes / 1024d:F1}KiB" + (info.RebaseReason == "bounded_history_rebase" ? "；已按预算整理" : "") : "";
        panel.SetUsage(last + "\n" + aggregate + misses + (usage == null ? "" : $"\n请求：{usage.ElapsedMilliseconds / 1000d:F2}秒；输出：{usage.CompletionTokens?.ToString() ?? "待统计"} tokens") + session + $"\n本地进度/路线已省：{savedModelCalls}次模型请求");
        if (showCurrent) SetCurrentUsage(usage);
    }
    private void SetCurrentUsage(ProviderUsage? usage)
    {
        if ((!game.InCombat || ModelControlsCombat) && usage != null)
        {
            string cache = usage is { PromptTokens: > 0, CachedTokens: >= 0 } ? $"  ·  缓存 {100d * usage.CachedTokens / usage.PromptTokens:F0}%" : "";
            panel.SetCurrentMetrics($"本次 {usage.ElapsedMilliseconds / 1000d:F1}s{cache}");
        }
    }
    private static string SessionMode(string mode) => mode switch
    {
        "responses_replay" => "Responses持续会话",
        "responses_server" => "Responses服务端会话",
        "chat_replay" => "Chat兼容持续会话",
        _ => "独立局面"
    };
    internal void PresentProbeDecision(string scene, string plan, ModelDecision? decision)
    {
        panel.ShowMonitor();
        if (decision == null) BeginCurrent("执行", "完成当前操作", "probe-local:" + scene);
        else if (presentationOwner != "模型") BeginCurrent("模型", SceneTitle(scene), "probe-result:" + scene);
        panel.SetPlan(Redact(plan));
        if (decision == null) { panel.SetCurrentDetails(Redact(scene)); return; }
        modelThinking = false;
        panel.SetCurrentDetails(Redact(decision.ReasoningSummary is { Length: > 0 } summary ? summary : decision.Reason));
        RecordUsage(decision.Usage);
    }
    internal void BeginProbeModel(string scene, string token)
    {
        panel.ShowMonitor();
        BeginCurrent("模型", SceneTitle(scene), token);
        modelThinking = true;
        modelStartedAt = clock;
        panel.SetCurrentDetails("正在结合当前牌组、生命与选项做出选择…");
    }
    public override void _ExitTree()
    {
        exiting = true;
        generation++;
        pending?.Cancel();
        if (GodotObject.IsInstanceValid(MegaCrit.Sts2.Core.Nodes.NGame.Instance)) combat.PauseOwnedAutomation();
        provider.Dispose();
        reflectionProvider.Dispose();
        SolverModelOwnership.Release();
    }

    private static object ExperienceObservation(object snapshot)
    {
        var state = System.Text.Json.JsonSerializer.SerializeToNode(snapshot);
        return new
        {
            turn = state?["turn"]?.DeepClone(), side = state?["side"]?.DeepClone(),
            player = state?["creature"]?.DeepClone(), energy = state?["energy"]?.DeepClone(), stars = state?["stars"]?.DeepClone(),
            enemies = state?["enemies"]?.DeepClone(), relics = state?["relics"]?.DeepClone(),
            hand = state?["hand"]?.DeepClone(), drawPile = state?["drawPile"]?.DeepClone(),
            discardPile = state?["discardPile"]?.DeepClone(), exhaustPile = state?["exhaustPile"]?.DeepClone()
        };
    }
}
