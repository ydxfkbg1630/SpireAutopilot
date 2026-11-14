using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Ftue;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace SpireAutopilot;

/// <summary>Real native queued OnPlay/OnUse choices; synthetic cards exist only in an isolated unsaved run.</summary>
public static class CardChoiceProbe
{
    public static async Task RunAsync(AutopilotHost host)
    {
        string? output = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_CARD_CHOICE_PROBE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var timer = Stopwatch.StartNew(); using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var harmony = new Harmony("SpireAutopilot.CardChoiceProbe"); var adapter = new GameAdapter(); var results = new List<object>(); string stage = "startup";
        bool useModel = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_CARD_CHOICE_USE_MODEL") == "1";
        AutopilotConfig? modelConfig = null; using var provider = new ProviderClient(); int apiCalls = 0;
        try
        {
            if (RunManager.Instance.IsInProgress) throw new InvalidOperationException("Choice probe uses an isolated main-menu process.");
            if (useModel) modelConfig = AutopilotConfig.Load(Path.Combine(OS.GetUserDataDir(), "spire_autopilot.json"));
            while (!Walk(host.GetTree().Root).OfType<NMainMenu>().Any(n => n.IsNodeReady())) await Frame();
            SolverModelOwnership.Activate();
            if (DisplayServer.GetName() == "headless") harmony.Patch(AccessTools.Method(typeof(NCombatRulesFtue), nameof(NCombatRulesFtue.Create)), prefix: new HarmonyMethod(typeof(NativeProbe), nameof(NativeProbe.SkipHeadlessCombatTutorial)));
            var starting = NGame.Instance!.StartNewSingleplayerRun(Player.CreateForNewRun(ModelDb.AllCharacters.Single(c => c.Id.Entry == "FOOL"), UnlockState.all, 1UL), false,
                ActModel.GetDefaultList(), Array.Empty<ModifierModel>(), "AUTOPILOTMODELCOMBAT", GameMode.Standard, false, 0);
            string? last = null;
            while (!adapter.InCombat || adapter.CaptureCombatFrame() == null)
            {
                await Frame(); if (starting.IsFaulted) await starting; if (adapter.TryHandleTutorial()) continue;
                if (adapter.InCombat) continue;
                var f = adapter.Capture(); if (f == null || f.Token == last) continue;
                var a = f.Actions.FirstOrDefault(a => a.Kind is "confirm" or "continue" or "skip" or "alternative")
                    ?? f.Actions.FirstOrDefault(a => a.Kind is "event" or "dialogue" or "travel" or "select_card") ?? f.Actions[0];
                await adapter.ExecuteAsync(f.Token, a.Id); last = f.Token; await Delay();
            }
            var combat = CombatManager.Instance.DebugOnlyGetState()!; var player = LocalContext.GetMe(combat)!;
            foreach (var canonical in new CardModel[] { ModelDb.Card<KiChant>(), ModelDb.Card<Prepared>(), ModelDb.Card<Armaments>(), ModelDb.Card<Discovery>() })
            {
                stage = canonical.Id.Entry;
                player.PlayerCombatState!.Energy = 20;
                var card = combat.CreateCard(canonical, player); await CardPileCmd.Add(card, PileType.Hand);
                var before = await Ready();
                var action = before.Actions.First(a => a.Kind == "play_card" && JsonSerializer.Serialize(a.Details).Contains(canonical.Id.ToString(), StringComparison.Ordinal));
                await adapter.ExecuteCombatAsync(before.Token, action.Id);
                await DriveChoice(stage);
            }
            stage = "LIQUID_MEMORIES";
            var discarded = combat.CreateCard(ModelDb.Card<StrikeFool>(), player); await CardPileCmd.Add(discarded, PileType.Discard);
            var discardedOther = combat.CreateCard(ModelDb.Card<DefendFool>(), player); await CardPileCmd.Add(discardedOther, PileType.Discard);
            var potion = ModelDb.Potion<LiquidMemories>().ToMutable(); await PotionCmd.TryToProcure(potion, player);
            var potionFrame = await Ready();
            var potionAction = potionFrame.Actions.First(a => a.Kind == "use_potion" && JsonSerializer.Serialize(a.Details).Contains("LIQUID_MEMORIES", StringComparison.Ordinal));
            await adapter.ExecuteCombatAsync(potionFrame.Token, potionAction.Id); await DriveChoice(stage);
            Write("result.json", new { success = true, providerApiCalls = apiCalls, realQueuedActions = true, results, elapsedSeconds = timer.Elapsed.TotalSeconds });

            async Task DriveChoice(string name)
            {
                var choiceTimer = Stopwatch.StartNew(); bool sawChoice = false; int dispatched = 0; string? previous = null;
                bool modelUsed = false, exercisedDeselect = false;
                while (choiceTimer.Elapsed.TotalSeconds < 12)
                {
                    await Frame(); var screen = ActiveScreenContext.Instance.GetCurrentScreen() as Node;
                    var frame = adapter.CaptureCombatFrame();
                    if (frame is { Scene: not "Combat" } && frame.Actions.Any(a => a.Kind is "select_card" or "select_bundle" or "confirm"))
                    {
                        sawChoice = true;
                        if (frame.Token == previous) continue;
                        Write(name + "-choice.json", frame);
                        var context = new DecisionContext(); var route = new RoutePlanner(); route.Observe(frame, "原生选牌回归");
                        var prepared = context.Prepare(frame, route, Array.Empty<string>());
                        var modelContext = JsonSerializer.SerializeToNode(prepared)!;
                        if (modelContext["FullStableContext"]!["cardDictionary"]!.AsArray().Count == 0
                            || !JsonSerializer.Serialize(prepared.FullState).Contains("observation"))
                            throw new InvalidOperationException("Queued selection model context needs full battle and card definitions.");
                        Write(name + "-prepared-choice.json", prepared);
                        var choice = frame.Actions.FirstOrDefault(a => a.Kind == "confirm") ?? frame.Actions.First(a => a.Kind is "select_card" or "select_bundle");
                        if (useModel && !modelUsed && name is "KI_CHANT" or "PREPARED" && apiCalls < 2)
                        {
                            apiCalls++; modelUsed = true;
                            var decision = await provider.DecideAsync(modelConfig!, prepared, lifetime.Token);
                            choice = frame.Actions.Single(a => a.Id == decision.ActionId);
                            Write(name + "-model-decision.json", decision);
                        }
                        await adapter.ExecuteCombatAsync(frame.Token, choice.Id); previous = frame.Token; dispatched++; await Delay();
                        var updated = adapter.CaptureCombatFrame();
                        if (!useModel && name == "PREPARED" && !exercisedDeselect && choice.Kind == "select_card"
                            && updated?.Actions.FirstOrDefault(a => a.Kind == "deselect_card") is { } deselect)
                        {
                            exercisedDeselect = true;
                            await adapter.ExecuteCombatAsync(updated.Token, deselect.Id); dispatched++; await Delay();
                            var deselected = adapter.CaptureCombatFrame();
                            if (deselected?.Token == updated.Token) throw new InvalidOperationException("Hand deselection should change current selection token.");
                            Write(name + "-deselection.json", new { beforeToken = updated.Token, after = deselected, deselect.Id });
                        }
                        if (updated?.Token != frame.Token)
                        {
                            bool rejected = false;
                            try { await adapter.ExecuteCombatAsync(frame.Token, choice.Id); } catch (InvalidOperationException) { rejected = true; }
                            if (!rejected) throw new InvalidOperationException("Changed native choice should reject stale token.");
                        }
                        continue;
                    }
                    if (sawChoice && frame?.Scene == "Combat")
                    { results.Add(new { card = name, success = true, choicesDispatched = dispatched, actualNativeQueueCompleted = RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null }); return; }
                }
                var active = ActiveScreenContext.Instance.GetCurrentScreen() as Node;
                Write(name + "-blocked.json", new { currentScreen = active?.GetType().FullName, adapter.Status,
                    action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.GetType().FullName,
                    controls = active == null ? Array.Empty<object>() : Walk(active).Select(n => (object)new { type = n.GetType().Name, name = n.Name.ToString(), visible = n is CanvasItem item ? item.IsVisibleInTree() : true }).ToArray() });
                throw new InvalidOperationException("Real queued choice remains pending: " + name);
            }
            async Task<DecisionFrame> Ready() { while (true) { await Frame(); if (adapter.CaptureCombatFrame() is { Scene: "Combat" } frame) return frame; } }
        }
        catch (Exception ex) { Write("result.json", new { success = false, providerApiCalls = apiCalls, stage, results, failure = ex.ToString(), elapsedSeconds = timer.Elapsed.TotalSeconds }); }
        finally { SolverModelOwnership.Release(); harmony.UnpatchAll(harmony.Id); host.GetTree().Quit(); }
        async Task Frame() { lifetime.Token.ThrowIfCancellationRequested(); await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Delay() => await host.ToSignal(host.GetTree().CreateTimer(.3), SceneTreeTimer.SignalName.Timeout);
        void Write(string name, object value)
        {
            string text = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            if (!string.IsNullOrEmpty(modelConfig?.ApiKey)) text = text.Replace(modelConfig.ApiKey, "[REDACTED]", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(output, name), text);
        }
    }
    private static IEnumerable<Node> Walk(Node root) { yield return root; foreach (var child in root.GetChildren()) foreach (var item in Walk(child)) yield return item; }
}
