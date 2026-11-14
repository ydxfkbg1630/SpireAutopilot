using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Ftue;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace SpireAutopilot;

/// <summary>Explicit isolated one-battle native command probe, scripted by default and API opt-in separately.</summary>
public static class ModelCombatProbe
{
    public static async Task RunAsync(AutopilotHost host)
    {
        string? output = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_MODEL_COMBAT_PROBE");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE"))) return;
        Directory.CreateDirectory(output);
        bool useModel = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_MODEL_COMBAT_USE_MODEL") == "1";
        using var provider = new ProviderClient();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var timer = Stopwatch.StartNew(); var adapter = new GameAdapter(); var trace = new List<object>(); var checks = new List<string>();
        var seen = new HashSet<string>(); Task? start = null; Task? selectionTask = null; bool entered = false, endedTurn = false, played = false, stale = false; int calls = 0;
        var context = new DecisionContext(); var route = new RoutePlanner(); AutopilotConfig? config = null;
        var experience = new BattleExperienceStore(Path.Combine(output, "experience"));
        var harmony = new Harmony("SpireAutopilot.ModelCombatProbe");
        try
        {
            if (RunManager.Instance.IsInProgress) throw new InvalidOperationException("Probe starts in a disposable main-menu process.");
            if (useModel) config = AutopilotConfig.Load(Path.Combine(OS.GetUserDataDir(), "spire_autopilot.json"));
            if (DisplayServer.GetName() == "headless") harmony.Patch(AccessTools.Method(typeof(NCombatRulesFtue), nameof(NCombatRulesFtue.Create)), prefix: new HarmonyMethod(typeof(NativeProbe), nameof(NativeProbe.SkipHeadlessCombatTutorial)));
            while (!Walk(host.GetTree().Root).OfType<NMainMenu>().Any(n => n.IsNodeReady())) await NextFrame();
            SolverModelOwnership.Activate();
            var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == "FOOL");
            start = NGame.Instance!.StartNewSingleplayerRun(Player.CreateForNewRun(character, UnlockState.all, 1UL), false, ActModel.GetDefaultList(), Array.Empty<ModifierModel>(), "AUTOPILOTMODELCOMBAT", GameMode.Standard, false, 0);
            while (true)
            {
                await NextFrame(); if (start.IsFaulted) await start; if (adapter.TryHandleTutorial()) continue;
                if (RunManager.Instance.IsGameOver) break;
                if (entered && !adapter.InCombat)
                {
                    checks.Add("one_battle_completed");
                    if (useModel)
                    {
                        var reflection = experience.CreateReflectionFrame(new { status = "native_battle_completed", player = RunManager.Instance.DebugOnlyGetState()?.Players.FirstOrDefault()?.Creature.CurrentHp })!;
                        calls++;
                        using var reflectedProvider = new ProviderClient();
                        var reflected = await reflectedProvider.DecideAsync(config!, reflection, lifetime.Token);
                        if (!experience.CommitReflection(reflection.Token, reflected, config!.ApiKey)) throw new InvalidOperationException("Reflection note was not committed.");
                        Write("reflection-decision.json", reflected);
                        checks.Add("real_model_postbattle_reflection_saved");
                    }
                    break;
                }
                var frame = adapter.InCombat ? adapter.CaptureCombatFrame() : adapter.Capture();
                if (frame == null || !seen.Add(frame.Token)) continue;
                if (adapter.InCombat)
                {
                    entered = true;
                    if (!checks.Contains("combat_context_complete"))
                    {
                        var json = JsonSerializer.Serialize(frame.State);
                        foreach (string key in new[] { "hand", "drawPile", "discardPile", "exhaustPile", "relics", "intents", "currentMapPoint", "actMap" })
                            if (!json.Contains('"' + key + '"')) throw new InvalidOperationException("Missing combat context: " + key);
                        checks.Add("combat_context_complete");
                        Write("first-combat-frame.json", frame);
                        experience.BeginBattle(adapter.RunIdentity ?? "probe-run", adapter.CombatIdentity!, adapter.CaptureCombatObservation()!);
                        route.Observe(frame, "原生测试");
                        var preparedContext = context.Prepare(frame, route, Array.Empty<string>());
                        var preparedJson = JsonSerializer.SerializeToNode(preparedContext)!;
                        var dictionary = preparedJson["FullStableContext"]!["cardDictionary"]!.AsArray();
                        if (dictionary.Count == 0 || preparedJson["FullStableContext"]!["actMap"]!.AsArray().Count == 0
                            || !JsonSerializer.Serialize(preparedContext.FullState).Contains("relics")
                            || dictionary.Any(d => string.IsNullOrWhiteSpace(d!["definition"]!["description"]?.ToString())))
                            throw new InvalidOperationException("Prepared model context needs full native card effects, relics and map.");
                        Write("first-prepared-model-frame.json", preparedContext);
                        checks.Add("prepared_model_context_preserves_native_effects_and_map");
                        var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
                        var selection = NSimpleCardSelectScreen.Create(player.PlayerCombatState!.Hand.Cards.Take(2).ToArray(),
                            new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 1) { RequireManualConfirmation = true });
                        NOverlayStack.Instance!.Push(selection);
                        selectionTask = selection!.CardsSelected();
                        checks.Add("isolated_native_selection_fixture_opened");
                        seen.Remove(frame.Token);
                        continue;
                    }
                    if (selectionTask is { IsCompletedSuccessfully: true } && !checks.Contains("native_combat_card_selection_completed")) checks.Add("native_combat_card_selection_completed");
                }
                ModelDecision? decision = null;
                LegalAction action;
                if (adapter.InCombat && !endedTurn && frame.Actions.Any(a => a.Kind == "end_turn")) action = frame.Actions.First(a => a.Kind == "end_turn");
                else if (adapter.InCombat && useModel && calls == 0 && frame.Scene == "Combat")
                {
                    if (++calls > 20) throw new InvalidOperationException("Reached the explicit 20-call model budget.");
                    route.Observe(frame, config!.Strategy); var prepared = context.Prepare(frame, route, Array.Empty<string>());
                    decision = await provider.DecideAsync(config, prepared, lifetime.Token);
                    var fresh = adapter.CaptureCombatFrame();
                    if (fresh?.Token != frame.Token) { provider.DiscardLastDecision(); continue; }
                    context.Commit(prepared); action = frame.Actions.Single(a => a.Id == decision.ActionId);
                }
                else action = frame.Actions.FirstOrDefault(a => a.Kind == "confirm") ?? frame.Actions.FirstOrDefault(a => a.Kind == "play_card")
                    ?? frame.Actions.FirstOrDefault(a => a.Kind is "event" or "dialogue" or "travel" or "select_card" or "confirm" or "end_turn")
                    ?? frame.Actions.FirstOrDefault(a => a.Kind is "reward" or "continue" or "skip" or "alternative") ?? frame.Actions[0];
                bool inCombat = adapter.InCombat;
                if (inCombat) await adapter.ExecuteCombatAsync(frame.Token, action.Id); else await adapter.ExecuteAsync(frame.Token, action.Id);
                endedTurn |= action.Kind == "end_turn"; played |= action.Kind == "play_card";
                if (inCombat && !stale)
                {
                    await NextFrame();
                    try { await adapter.ExecuteCombatAsync("STALE:" + frame.Token, action.Id); throw new InvalidOperationException("Stale token was accepted."); }
                    catch (InvalidOperationException ex) when (!ex.Message.Contains("was accepted")) { stale = true; checks.Add("stale_token_rejected"); }
                }
                trace.Add(new { elapsedSeconds = timer.Elapsed.TotalSeconds, frame.Scene, action.Id, action.Kind, action.Label, decision, observation = adapter.CaptureCombatObservation() });
                if (inCombat) { experience.RecordAction(action.Label, decision?.Reason ?? "原生测试脚本动作"); if (adapter.CaptureCombatObservation() is { } observed) experience.Observe(observed); }
                Write("progress.json", new { providerApiCalls = calls, entered, endedTurn, played, stale, trace });
                await host.ToSignal(host.GetTree().CreateTimer(.25), SceneTreeTimer.SignalName.Timeout);
            }
            if (!entered || !endedTurn || !played || !stale || !checks.Contains("native_combat_card_selection_completed")) throw new InvalidOperationException("Probe requires entry, native end-turn, native card play, native selection and stale-token rejection.");
            Write("result.json", new { success = true, providerApiCalls = calls, useModel, entered, endedTurn, played, stale, checks, trace, elapsedSeconds = timer.Elapsed.TotalSeconds });
        }
        catch (Exception ex) { Write("result.json", new { success = false, providerApiCalls = calls, entered, endedTurn, played, stale, checks, trace, failure = ex.ToString() }); }
        finally { SolverModelOwnership.Release(); harmony.UnpatchAll(harmony.Id); host.GetTree().Quit(); }
        async Task NextFrame() { lifetime.Token.ThrowIfCancellationRequested(); await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame); }
        void Write(string name, object value)
        {
            string text = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            if (!string.IsNullOrEmpty(config?.ApiKey)) text = text.Replace(config.ApiKey, "[REDACTED]", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(output, name), text);
        }
    }
    private static IEnumerable<Node> Walk(Node root) { yield return root; foreach (var child in root.GetChildren()) foreach (var item in Walk(child)) yield return item; }
}
