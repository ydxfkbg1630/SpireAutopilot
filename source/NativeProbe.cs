using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Ftue;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace SpireAutopilot;

/// <summary>Opt-in native integration probe in a disposable runtime, with scripted or real model decisions.</summary>
public static class NativeProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task RunAsync(AutopilotHost host)
    {
        string? output = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        bool useModel = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE_USE_MODEL") == "1";
        bool fullRun = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE_FULL_RUN") == "1";
        int limitSeconds = fullRun ? 1800 : useModel ? 180 : 120;
        int actionLimit = fullRun ? 200 : 40;
        int combatLimit = int.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE_COMBAT_LIMIT"), out int parsedLimit) ? Math.Clamp(parsedLimit, 1, 20) : 0;
        var timer = Stopwatch.StartNew();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(limitSeconds));
        using var provider = new ProviderClient();
        AutopilotConfig? modelConfig = null;
        var adapter = new GameAdapter { AllowNativeUiFallback = false };
        var bridge = new CombatBridge();
        var route = new RoutePlanner();
        var decisionContext = new DecisionContext();
        var providerUsage = new List<ProviderUsage?>();
        int plannedMapActions = 0;
        int mechanicalActions = 0;
        bool compatibilityChecked = false;
        bool progressOnly = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE_PROGRESS_ONLY") == "1";
        bool observedSearchProgress = false;
        bool observedDeployProgress = false;
        bool monitorChecked = false;
        bool broadcastChecked = false;
        var actions = new List<object>();
        var scenes = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string stage = "wait_main_menu";
        string seed = "AUTOPILOTNATIVE20261114";
        string? lastDispatchToken = null;
        double nextDispatch = 0;
        int snapshots = 0;
        Task? startTask = null;
        bool success = false;
        string? failure = null;
        bool combatBridgeAccepted = false;
        bool runCompleted = false;
        int providerApiCalls = 0;
        int providerDecisions = 0;
        var protocolFailures = new Dictionary<string, int>();
        var providerErrors = new List<object>();
        double lastProgress = -5;
        bool ftuePatchApplied = false;
        string? outcome = null;
        object? lastRecordedCombat = null;
        int combatsEntered = 0;
        var probeHarmony = new Harmony("SpireAutopilot.NativeProbe");
        try
        {
            if (useModel)
            {
                modelConfig = AutopilotConfig.Load(Path.Combine(OS.GetUserDataDir(), "spire_autopilot.json"));
                if (string.IsNullOrWhiteSpace(modelConfig.Model))
                    throw new InvalidOperationException("Model probe requires a model in its isolated spire_autopilot.json.");
            }
            // Match CombatSolver's UnattendedHeadlessFtuePatch: avoid creating window-only
            // tutorial UI in an explicitly opted-in headless probe. Player settings stay intact.
            if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            {
                probeHarmony.Patch(AccessTools.Method(typeof(NCombatRulesFtue), nameof(NCombatRulesFtue.Create)),
                    prefix: new HarmonyMethod(typeof(NativeProbe), nameof(SkipHeadlessCombatTutorial)));
                ftuePatchApplied = true;
            }
            if (RunManager.Instance.IsInProgress)
                throw new InvalidOperationException("Native probe requires a fresh disposable process at the main menu.");
            while (!Walk(host.GetTree().Root).OfType<NMainMenu>().Any(n => n.IsNodeReady()))
                await NextFrame();

            stage = "create_fool_test_run";
            if (System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE_EARLY_OWNERSHIP") == "1")
                bridge.PrepareOwnership();
            var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == "FOOL");
            startTask = NGame.Instance!.StartNewSingleplayerRun(
                Player.CreateForNewRun(character, UnlockState.all, 1UL),
                shouldSave: false,
                ActModel.GetDefaultList(),
                Array.Empty<ModifierModel>(),
                seed,
                GameMode.Standard,
                isCustomGame: false,
                ascensionLevel: 0);

            while (true)
            {
                await NextFrame();
                if (startTask.IsFaulted) await startTask;
                if (adapter.TryHandleTutorial()) continue;
                if (RunManager.Instance.IsGameOver)
                {
                    runCompleted = true;
                    success = fullRun;
                    var state = RunManager.Instance.DebugOnlyGetState();
                    outcome = state?.Players.FirstOrDefault()?.Creature.CurrentHp > 0 ? "survived_run_end" : "player_died";
                    stage = "completed_native_full_run";
                    break;
                }
                if (CombatManager.Instance.IsInProgress)
                {
                    stage = "combat_bridge";
                    combatBridgeAccepted = bridge.TryEnsureAutomaticCombat(true);
                    if (!combatBridgeAccepted) continue;
                    if (progressOnly)
                    {
                        var liveProgress = bridge.GetProgress();
                        observedSearchProgress |= liveProgress.Mode == "search" && liveProgress.ExpandedNodes > 0;
                        observedDeployProgress |= liveProgress.Mode == "deployment" && liveProgress.CompletedActions > 0;
                        if (observedSearchProgress && observedDeployProgress)
                        {
                            success = true; outcome = "observed_live_search_and_deployment"; stage = "completed_live_progress_probe"; break;
                        }
                    }
                    if (!compatibilityChecked && System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_COMPAT_PROBE") == "1")
                    {
                        Write("compatibility.json", CompatibilityProbe.Verify());
                        compatibilityChecked = true;
                    }
                    var currentCombat = CombatManager.Instance.DebugOnlyGetState();
                    if (!ReferenceEquals(lastRecordedCombat, currentCombat))
                    {
                        lastRecordedCombat = currentCombat;
                        combatsEntered++;
                        Write("combat.json", new
                        {
                            providerIndependent = !useModel, scripted = !useModel,
                            bridge.Status, CombatManager.Instance.IsInProgress,
                            state = currentCombat?.GetType().FullName, combatsEntered,
                            elapsedSeconds = timer.Elapsed.TotalSeconds
                        });
                    }
                    if (fullRun) continue;
                    success = scenes.Contains("NMapScreen")
                        && actions.Any(a => JsonSerializer.Serialize(a).Contains("\"kind\":\"travel\"", StringComparison.Ordinal));
                    if (!success) throw new InvalidOperationException("Combat started before a native map travel action was recorded.");
                    stage = "completed_native_event_map_combat_bridge";
                    break;
                }
                if (combatLimit > 0 && combatsEntered >= combatLimit)
                {
                    success = true; outcome = "completed_bounded_combats"; stage = "completed_combat_limit"; break;
                }
                if (timer.Elapsed.TotalSeconds < nextDispatch) continue;
                var frame = adapter.Capture();
                if (frame == null) continue;
                if (!broadcastChecked && System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_BROADCAST_PROBE") == "1")
                {
                    await BroadcastProbe.RunAsync(host, output);
                    broadcastChecked = true;
                }
                if (!monitorChecked && System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_MONITOR_PROBE") == "1")
                {
                    host.PresentProbeDecision("隔离界面布局验证", "首次地图出现后规划整幕路线。", null);
                    await MonitorProbe.RunAsync(host, output);
                    monitorChecked = true;
                }
                stage = (useModel ? "model_" : "scripted_") + frame.Scene;
                scenes.Add(frame.Scene);
                if (tokens.Add(frame.Token))
                {
                    snapshots++;
                    Write($"snapshot-{snapshots:D3}-{frame.Scene}.json", frame);
                }
                if (frame.Token == lastDispatchToken) continue;
                if (actions.Count >= actionLimit)
                    throw new InvalidOperationException($"Native probe reached its bounded {actionLimit}-action limit.");
                LegalAction chosen;
                string reason;
                if (useModel)
                {
                    route.Observe(frame, modelConfig!.Strategy);
                    if (route.PlannedTravel(frame) is { } planned)
                    {
                        host.PresentProbeDecision("沿整幕规划前进：" + planned.Label, route.Summary, null);
                        await adapter.ExecuteAsync(frame.Token, planned.Id);
                        plannedMapActions++;
                        actions.Add(new { scene=frame.Scene, actionId=planned.Id, kind=planned.Kind, planned.Label,
                            reason="Follow validated full-act route", decisionSource="saved_map_plan", elapsedSeconds=timer.Elapsed.TotalSeconds });
                        Write("actions.json", actions);
                        lastDispatchToken = frame.Token;
                        nextDispatch = timer.Elapsed.TotalSeconds + .75;
                        continue;
                    }
                    if (modelConfig.LocalMechanicalActions && LocalDecisionPolicy.Choose(frame) is { } mechanical)
                    {
                        await adapter.ExecuteAsync(frame.Token, mechanical.Id);
                        mechanicalActions++;
                        host.PresentProbeDecision("本地进度：" + mechanical.Label, route.Summary, null);
                        actions.Add(new { scene=frame.Scene, actionId=mechanical.Id, kind=mechanical.Kind, mechanical.Label,
                            reason="Deterministic native progress", decisionSource="local_mechanical", elapsedSeconds=timer.Elapsed.TotalSeconds });
                        Write("actions.json", actions);
                        lastDispatchToken=frame.Token;
                        nextDispatch=timer.Elapsed.TotalSeconds + .75;
                        continue;
                    }
                    providerApiCalls++;
                    WriteProgress();
                    host.BeginProbeModel(frame.Scene, frame.Token);
                    var requestFrame = decisionContext.Prepare(frame, route, actions.TakeLast(6).Select(a => JsonSerializer.Serialize(a)));
                    ModelDecision decision;
                    bool returned = false;
                    try
                    {
                        decision = await provider.DecideAsync(modelConfig!, requestFrame, lifetime.Token);
                        returned = true;
                        if (adapter.Capture()?.Token != frame.Token)
                        {
                            provider.DiscardLastDecision();
                            continue;
                        }
                        route.Accept(frame, decision);
                        decisionContext.Commit(requestFrame);
                        providerUsage.Add(decision.Usage);
                        Write("provider-usage.json", providerUsage);
                        host.PresentProbeDecision(frame.Scene, route.Summary, decision);
                        if (frame.Scene == "NMapScreen") Write("map-plan.json", new { route.Remaining, decision.RouteOptionId, decision.PlanReason, route.Summary });
                    }
                    catch (Exception ex) when (ex is ModelProtocolException or ProviderTransientException or TimeoutException)
                    {
                        if (returned) provider.DiscardLastDecision();
                        int failures = protocolFailures.GetValueOrDefault(frame.Token) + 1;
                        protocolFailures[frame.Token] = failures;
                        providerErrors.Add(new { frame.Scene, frame.Token, attempt=failures, type=ex.GetType().Name,
                            detail=Redact(ex.Message), elapsedSeconds=timer.Elapsed.TotalSeconds });
                        Write("model-response-errors.json", providerErrors);
                        if (failures > 2) throw;
                        nextDispatch = timer.Elapsed.TotalSeconds + failures;
                        continue;
                    }
                    providerDecisions++;
                    chosen = frame.Actions.Single(a => a.Id == decision.ActionId);
                    reason = Redact(decision.Reason);
                    var fresh = adapter.Capture();
                    if (fresh?.Token != frame.Token)
                    {
                        Write("model-stale-state.json", new { frame.Scene, frame.Token, chosen.Id, reason,
                            elapsedSeconds = timer.Elapsed.TotalSeconds });
                        continue;
                    }
                }
                else
                {
                    chosen = Choose(frame);
                    reason = "Deterministic scripted native test preference.";
                }
                // ExecuteAsync recaptures the native state and rejects stale action tokens.
                await adapter.ExecuteAsync(frame.Token, chosen.Id);
                actions.Add(new { scene = frame.Scene, actionId = chosen.Id, kind = chosen.Kind,
                    chosen.Label, reason, decisionSource = useModel ? "configured_model" : "scripted_test_provider",
                    elapsedSeconds = timer.Elapsed.TotalSeconds });
                Write("actions.json", actions);
                lastDispatchToken = frame.Token;
                nextDispatch = timer.Elapsed.TotalSeconds + 0.75;
                WriteProgress();
            }
        }
        catch (Exception exception)
        {
            failure = Redact(exception.ToString());
            GD.PrintErr("[SpireAutopilot/NativeProbe] " + failure);
        }
        finally
        {
            bridge.PauseOwnedAutomation();
            Write("result.json", new
            {
                success, stage, providerIndependent = !useModel, scripted = !useModel,
                providerApiCalls, providerDecisions, model = modelConfig?.Model,
                plannedMapActions, mechanicalActions, providerUsage, observedSearchProgress, observedDeployProgress,
                fullRun, runCompleted, outcome, limitSeconds, ftuePatchApplied, combatsEntered,
                character = "FOOL", seed, shouldSave = false,
                elapsedSeconds = timer.Elapsed.TotalSeconds, snapshots, scenes = scenes.ToArray(),
                actionCount = actions.Count, combatBridgeAccepted, adapterStatus = adapter.Status,
                failure, scope = progressOnly ? "Native run until observed actual expanded search nodes and a completed deployed action."
                    : combatLimit > 0 ? $"Native run with noncombat decisions and combat solver through {combatLimit} completed battles."
                    : fullRun ? "Native run with noncombat decisions and combat solver until game over."
                    : "Native new run, legal noncombat actions, native map travel, combat solver bridge."
            });
            GD.Print($"[SpireAutopilot/NativeProbe] DONE success={success} snapshots={snapshots} actions={actions.Count}");
            if (ftuePatchApplied) probeHarmony.UnpatchAll(probeHarmony.Id);
            host.GetTree().Quit(success ? 0 : 1);
        }

        async Task NextFrame()
        {
            if (timer.Elapsed.TotalSeconds >= limitSeconds)
                throw new TimeoutException($"Native probe reached {limitSeconds} seconds at stage " + stage
                    + "; adapter=" + adapter.Status + "; bridge=" + bridge.Status);
            if (timer.Elapsed.TotalSeconds - lastProgress >= 5)
            {
                WriteProgress();
                lastProgress = timer.Elapsed.TotalSeconds;
            }
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        void WriteProgress() => Write("progress.json", new
        {
            stage, elapsedSeconds = timer.Elapsed.TotalSeconds, fullRun, useModel,
            model = modelConfig?.Model, providerApiCalls, providerDecisions, snapshots,
            actionCount = actions.Count, scenes = scenes.ToArray(), bridgeStatus = bridge.Status,
            adapterStatus = adapter.Status, combatBridgeAccepted, combatsEntered,
            currentFloor = RunManager.Instance.DebugOnlyGetState()?.TotalFloor
        });
        string Redact(string value) => string.IsNullOrEmpty(modelConfig?.ApiKey)
            ? value : value.Replace(modelConfig.ApiKey, "[REDACTED]", StringComparison.Ordinal);
        void Write(string file, object value)
            => File.WriteAllText(Path.Combine(output, file), Redact(JsonSerializer.Serialize(value, Json)));
    }

    public static bool SkipHeadlessCombatTutorial(ref NCombatRulesFtue? __result)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE"))
            || !DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase)) return true;
        __result = null;
        return false;
    }

    private static LegalAction Choose(DecisionFrame frame)
    {
        // These deterministic preferences are a test provider, not the player's model policy.
        foreach (string kind in new[] { "confirm", "continue", "dialogue", "event", "travel", "reward", "relic",
                     "select_card", "alternative", "rest", "open_shop", "purchase", "close_shop", "open_chest", "skip" })
        {
            var available = frame.Actions.Where(a => a.Kind == kind).ToArray();
            if (kind == "select_card")
                available = available.Where(a => !JsonSerializer.Serialize(a.Details).Contains("\"selected\":true", StringComparison.Ordinal)).ToArray();
            if (available.Length > 0) return available[0];
        }
        throw new InvalidOperationException("Scripted native probe has no matching legal action in " + frame.Scene);
    }

    private static IEnumerable<Node> Walk(Node node)
    {
        yield return node;
        foreach (var child in node.GetChildren())
            foreach (var descendant in Walk(child)) yield return descendant;
    }
}
