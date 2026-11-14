using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Ftue;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace SpireAutopilot;

/// <summary>Explicit disposable-process native import/search/deployment fixture; never invokes a model.</summary>
public static class StorytellerSolverProbe
{
    private static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static async Task RunAsync(AutopilotHost host)
    {
        string? output = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_STORYTELLER_PROBE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        bool baseline = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_STORYTELLER_EXPECT_UNSUPPORTED") == "1";
        bool replaceStarter = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_STORYTELLER_REPLACE_STARTER") == "1";
        bool restoreRelicSet = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_STORYTELLER_USER_RELICS") == "1";
        var timer = Stopwatch.StartNew();
        var bridge = new Sts2SolverBridge();
        var adapter = new GameAdapter();
        var events = new List<object>();
        var patcher = new Harmony("SpireAutopilot.StorytellerFixture");
        bool injected = false, entered = false, search = false, deployment = false, completed = false, success = false;
        int initialHand = -1, minimumHand = int.MaxValue, completedActions = 0, maximumCombatTurn = 0;
        string stage = "main_menu", errorCode = "", errorReason = "", error = "", failure = "";
        long sequence = -1;
        string? lastToken = null;
        double nextAction = 0;
        Task? startup = null;
        try
        {
            if (RunManager.Instance.IsInProgress) throw new InvalidOperationException("Probe requires a fresh isolated process.");
            patcher.Patch(AccessTools.Method(typeof(NCombatRulesFtue), nameof(NCombatRulesFtue.Create)),
                prefix: new HarmonyMethod(typeof(StorytellerSolverProbe), nameof(SkipTutorial)));
            while (!Walk(host.GetTree().Root).OfType<NMainMenu>().Any(n => n.IsNodeReady())) await Frame();
            CombatSolverSuppression.Activate();
            if (!bridge.EnsureInstalled()) throw new InvalidOperationException("STS2 Solver fixture dependency missing.");
            var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == "FOOL");
            startup = NGame.Instance!.StartNewSingleplayerRun(Player.CreateForNewRun(character, UnlockState.all, 1UL), false,
                ActModel.GetDefaultList(), Array.Empty<ModifierModel>(), "STORYTELLERFIXTURE20261115", GameMode.Standard, false, 0);
            while (true)
            {
                await Frame();
                if (startup.IsFaulted) await startup;
                var run = RunManager.Instance.DebugOnlyGetState();
                var player = run?.Players.FirstOrDefault();
                if (!injected && player != null && !adapter.InCombat && startup.IsCompletedSuccessfully)
                {
                    stage = "obtain_native_storyteller";
                    var storyteller = ModelDb.AllRelics.Single(r => r.Id.Entry == "STORYTELLER").ToMutable();
                    if (replaceStarter)
                        await RelicCmd.Replace(player.Relics.Single(r => r.Id.Entry == "SONG_BOW"), storyteller);
                    else await RelicCmd.Obtain(storyteller, player);
                    if (restoreRelicSet)
                        foreach (string relicId in new[] { "PRECARIOUS_SHEARS", "LANTERN", "MEAL_TICKET", "WHITE_STAR", "TOUCH_OF_OROBAS" })
                        {
                            var restored = ModelDb.AllRelics.Single(r => r.Id.Entry == relicId).ToMutable();
                            if (relicId == "TOUCH_OF_OROBAS")
                                restored.GetType().GetMethod("SetupForTests")!.Invoke(restored, new object[]
                                { ModelDb.AllRelics.Single(r => r.Id.Entry == "SONG_BOW").Id, storyteller.Id });
                            // Save/load-style restoration deliberately omits acquisition effects (deck removal/starter replacement).
                            player.AddRelicInternal(restored);
                        }
                    injected = player.Relics.Any(r => r.Id.Entry == "STORYTELLER");
                    Write("fixture.json", new { injected, relics = player.Relics.Select(r => r.Id.ToString()).ToArray(),
                        scope = replaceStarter ? "Native RelicCmd.Replace upgrades Song Bow to Storyteller before combat; fresh first-battle fixture, not the user save."
                            : "Native RelicCmd.Obtain Storyteller before combat; original starter relic retained as an explicit two-relic fixture.",
                        restoreRelicSet, relicRestoration = restoreRelicSet ? "Five additional user relic IDs restored through native AddRelicInternal, without acquisition effects; fresh first battle, not user second-act save." : null,
                        providerApiCalls = 0 });
                    bridge.PrepareOwnership();
                    SolverPresentation.ActivateForExternalSolver();
                }
                if (adapter.InCombat)
                {
                    entered = true; stage = "primary_native_combat";
                    bridge.EnsureAutomaticCombat();
                    var status = bridge.Capture(); search |= status.IsSearching; deployment |= status.IsDeploying;
                    var state = CombatManager.Instance.DebugOnlyGetState();
                    int hand = state?.Players.FirstOrDefault()?.PlayerCombatState?.Hand.Cards.Count ?? 0;
                    if (initialHand < 0 && hand > 0) initialHand = hand;
                    if (initialHand >= 0) minimumHand = Math.Min(minimumHand, hand);
                    object snapshot = Snapshot();
                    completedActions = Math.Max(completedActions, Convert.ToInt32(ReadNullable(Read(snapshot, "Route"), "CompletedActions") ?? 0));
                    maximumCombatTurn = Math.Max(maximumCombatTurn, Convert.ToInt32(Read(snapshot, "Turn") ?? 0));
                    errorCode = Read(snapshot, "ErrorCode")?.ToString() ?? "";
                    errorReason = Read(snapshot, "ErrorDetail")?.ToString() ?? Read(snapshot, "ErrorReason")?.ToString() ?? "";
                    error = Read(snapshot, "Error")?.ToString() ?? "";
                    long currentSequence = Convert.ToInt64(Read(snapshot, "Sequence") ?? -1L);
                    if (currentSequence != sequence)
                    {
                        sequence = currentSequence;
                        events.Add(new { elapsed = timer.Elapsed.TotalSeconds, status.IsSearching, status.IsDeploying,
                            status.ComputationCompleted, errorCode, errorReason, error, hand,
                            phase = Read(snapshot, "Phase")?.ToString(), completedActions, maximumCombatTurn,
                            routeReady = Read(snapshot, "CurrentTurnRouteReady") });
                        Write("events.json", events); Write("raw-snapshot.json", snapshot);
                        Write("overlay-visibility.json", new { solverPresentationActive = SolverPresentation.Active,
                            combatSolver = OriginalOverlayVisibility(), primarySolver = PrimaryOverlayVisibility(host.GetTree().Root) });
                    }
                    if (baseline && (errorCode.Contains("unsupported-relic", StringComparison.OrdinalIgnoreCase)
                        || errorReason.Contains("STORYTELLER", StringComparison.OrdinalIgnoreCase) && errorReason.Contains("unsupported-relic", StringComparison.OrdinalIgnoreCase)))
                    { success = true; stage = "confirmed_native_unsupported_storyteller"; break; }
                    if (!baseline && errorCode.Length > 0 && errorCode != "-")
                        throw new InvalidOperationException("Primary native error: " + errorCode + "; " + errorReason + "; " + error);
                    continue;
                }
                if (entered)
                {
                    completed = !RunManager.Instance.IsGameOver && player?.Creature.CurrentHp > 0;
                    success = !baseline && injected && search && deployment && completed && completedActions > 0;
                    stage = "native_first_battle_ended"; break;
                }
                if (adapter.TryHandleTutorial() || !injected || timer.Elapsed.TotalSeconds < nextAction) continue;
                var frame = adapter.Capture();
                if (frame == null || frame.Token == lastToken) continue;
                var action = Choose(frame);
                await adapter.ExecuteAsync(frame.Token, action.Id);
                events.Add(new { elapsed = timer.Elapsed.TotalSeconds, scene = frame.Scene, action = action.Label, action.Kind });
                lastToken = frame.Token; nextAction = timer.Elapsed.TotalSeconds + .4;
            }
        }
        catch (Exception ex) { failure = ex.ToString(); GD.PrintErr("[StorytellerProbe] " + failure); }
        finally
        {
            try { await bridge.CancelAndDisableAutomation("storyteller_probe_exit"); } catch (Exception ex) { failure += "\nCleanup: " + ex.GetType().Name; }
            CombatSolverSuppression.Release(); SolverPresentation.Restore(); patcher.UnpatchAll(patcher.Id);
            Write("result.json", new { success, baseline, stage, injected, entered, observedSearch = search,
                observedDeployment = deployment, completedOneBattle = completed, initialHand, minimumHand, completedActions, maximumCombatTurn,
                errorCode, errorReason, error, failure, providerApiCalls = 0, elapsedSeconds = timer.Elapsed.TotalSeconds,
                replaceStarter, restoreRelicSet, scope = "Isolated native Fool first-battle Storyteller fixture, not the user save; 120-second probe boundary." });
            host.GetTree().Quit(success ? 0 : 1);
        }
        async Task Frame()
        {
            if (timer.Elapsed.TotalSeconds > 120) throw new TimeoutException("Storyteller native fixture exceeded 120 seconds at " + stage);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions { WriteIndented = true }));
    }
    private static object Snapshot() => AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetType("Sts2Solver.CombatUiStore") != null)
        .GetType("Sts2Solver.CombatUiStore")!.GetProperty("Snapshot", Flags)!.GetValue(null)!;
    private static object? Read(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
    private static object? ReadNullable(object? value, string name) => value == null ? null : Read(value, name);
    private static object OriginalOverlayVisibility()
    {
        var overlay = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CombatSolver.SolverOverlay", false)).FirstOrDefault(t => t != null);
        var layer = overlay?.GetField("_layer", Flags)?.GetValue(null) as CanvasLayer;
        return new { state = layer == null ? "uncreated" : "created", visible = layer == null ? (bool?)null : layer.Visible };
    }
    private static object PrimaryOverlayVisibility(Node root)
    {
        var layer = Walk(root).OfType<CanvasLayer>().FirstOrDefault(n => n.GetType().FullName == "Sts2Solver.SolverOverlay");
        return new { state = layer == null ? "uncreated" : "created", visible = layer == null ? (bool?)null : layer.Visible };
    }
    private static LegalAction Choose(DecisionFrame frame)
    {
            foreach (string kind in new[] { "confirm", "continue", "dialogue", "event", "travel", "reward", "relic", "select_bundle", "select_card", "alternative", "rest", "open_chest", "skip" })
            if (frame.Actions.FirstOrDefault(a => a.Kind == kind && !(kind == "select_card" && JsonSerializer.Serialize(a.Details).Contains("\"selected\":true", StringComparison.Ordinal))) is { } chosen) return chosen;
        throw new InvalidOperationException("Unscripted fixture scene: " + frame.Scene);
    }
    private static IEnumerable<Node> Walk(Node node) { yield return node; foreach (var child in node.GetChildren()) foreach (var descendant in Walk(child)) yield return descendant; }
    public static bool SkipTutorial(ref NCombatRulesFtue? __result) { __result = null; return false; }
}
