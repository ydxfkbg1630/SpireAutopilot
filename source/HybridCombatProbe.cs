using System.Diagnostics;
using System.Reflection;
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

public static class HybridCombatProbe
{
    private static int slowEntered, slowCancelled;
    public static async Task RunAsync(AutopilotHost host)
    {
        string? output = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_HYBRID_PROBE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        bool slow = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_HYBRID_PROBE_MODE") != "fast";
        var timer = Stopwatch.StartNew(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var assertions = new List<string>(); var timeline = new List<object>();
        var adapter = new GameAdapter(); var bridge = new CombatBridge();
        var harmony = new Harmony("SpireAutopilot.HybridCombatProbe");
        var update = typeof(AutopilotHost).GetMethod("UpdateCombatControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var controls = typeof(AutopilotHost).GetProperty("ModelControlsCombat", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var config = (AutopilotConfig)typeof(AutopilotHost).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        config.CombatMode = "Hybrid"; config.AutoCombat = true;
        bool switched = false, quiescent = false, nativeAction = false, nextBattleReset = false, fastCompleted = false;
        string? firstBattle = null, lastToken = null; double lastLog = -1; int battles = 0; Task? start = null;
        try
        {
            if (RunManager.Instance.IsInProgress) throw new InvalidOperationException("Hybrid probe uses a disposable main-menu process.");
            while (!Walk(host.GetTree().Root).OfType<NMainMenu>().Any(n => n.IsNodeReady())) await Frame();
            if (!SolverSearchStatusCapture.EnsureInstalled()) throw new InvalidOperationException("Native search tracker needs CombatSolver.");
            if (slow)
            {
                var solver = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetType("CombatSolver.SearchGcPolicy", false) != null);
                var enter = solver.GetType("CombatSolver.SearchGcPolicy")!.GetMethod("EnterSearchScope", BindingFlags.Static | BindingFlags.Public)!;
                harmony.Patch(enter, prefix: new HarmonyMethod(typeof(HybridCombatProbe), nameof(DelaySearchUntilCancellation)) { priority = Priority.Last });
            }
            if (DisplayServer.GetName() == "headless") harmony.Patch(AccessTools.Method(typeof(NCombatRulesFtue), nameof(NCombatRulesFtue.Create)), prefix: new HarmonyMethod(typeof(NativeProbe), nameof(NativeProbe.SkipHeadlessCombatTutorial)));
            bridge.PrepareOwnership();
            start = NGame.Instance!.StartNewSingleplayerRun(Player.CreateForNewRun(ModelDb.AllCharacters.Single(c => c.Id.Entry == "FOOL"), UnlockState.all, 1UL), false,
                ActModel.GetDefaultList(), Array.Empty<ModifierModel>(), "AUTOPILOTHYBRID20261115", GameMode.Standard, false, 0);
            while (true)
            {
                await Frame(); if (start.IsFaulted) await start; if (adapter.TryHandleTutorial()) continue;
                update.Invoke(host, null);
                bool model = (bool)controls.GetValue(host)!;
                var status = SolverSearchStatusCapture.Capture();
                if (timer.Elapsed.TotalSeconds - lastLog > .5)
                { lastLog = timer.Elapsed.TotalSeconds; timeline.Add(new { elapsed = lastLog, combat = adapter.CombatIdentity, model, status, slowEntered, slowCancelled }); Write("progress.json", new { slow, switched, quiescent, nativeAction, nextBattleReset, fastCompleted, battles, timeline }); }
                if (adapter.RunFinished) throw new InvalidOperationException("Run ended before hybrid assertions completed.");
                if (adapter.InCombat)
                {
                    string identity = adapter.CombatIdentity!;
                    if (firstBattle == null) { firstBattle = identity; battles = 1; }
                    if (identity != firstBattle)
                    {
                        battles = 2;
                        if (model) throw new InvalidOperationException("Next battle should start with Solver ownership.");
                        nextBattleReset = true; assertions.Add("next_battle_returns_to_solver"); break;
                    }
                    if (!model) bridge.TryEnsureAutomaticCombat(true);
                    else
                    {
                        if (!slow) throw new InvalidOperationException("Fast computation should retain Solver ownership.");
                        if (!switched)
                        {
                            switched = true;
                            if (status.ElapsedSeconds > 0 && status.ElapsedSeconds < CombatHandoffPolicy.NormalizeTimeout(config.HybridSolverTimeoutSeconds) - .2) throw new InvalidOperationException("Handoff happened before 30 seconds of actual computation.");
                            assertions.Add("actual_thirty_second_handoff");
                        }
                        if (!status.IsQuiescent) continue;
                        quiescent = true;
                        var combat = adapter.CaptureCombatFrame();
                        if (combat == null || combat.Token == lastToken) continue;
                        var action = combat.Actions.FirstOrDefault(a => a.Kind == "confirm") ?? combat.Actions.FirstOrDefault(a => a.Kind == "play_card")
                            ?? combat.Actions.FirstOrDefault(a => a.Kind == "select_card") ?? combat.Actions.First(a => a.Kind == "end_turn");
                        await adapter.ExecuteCombatAsync(combat.Token, action.Id);
                        lastToken = combat.Token; nativeAction = true;
                        await host.ToSignal(host.GetTree().CreateTimer(.3), SceneTreeTimer.SignalName.Timeout);
                        if (!assertions.Contains("model_native_action_only_after_quiescence")) assertions.Add("model_native_action_only_after_quiescence");
                    }
                }
                else
                {
                    if (firstBattle != null)
                    {
                        if (slow && (!switched || !quiescent || !nativeAction || slowCancelled == 0)) throw new InvalidOperationException("Slow handoff assertions remain pending.");
                        if (!slow) { fastCompleted = true; assertions.Add("fast_search_keeps_solver_until_battle_complete"); break; }
                    }
                    var frame = adapter.Capture(); if (frame == null || frame.Token == lastToken) continue;
                    var action = frame.Actions.FirstOrDefault(a => a.Kind == "confirm") ?? frame.Actions.FirstOrDefault(a => a.Kind is "continue" or "skip" or "alternative") ?? frame.Actions.FirstOrDefault(a => a.Kind == "travel" && a.Label.Contains("Monster"))
                        ?? frame.Actions.FirstOrDefault(a => a.Kind is "event" or "dialogue" or "travel" or "select_card")
                        ?? frame.Actions.FirstOrDefault(a => a.Kind is "reward" or "continue" or "skip" or "alternative") ?? frame.Actions[0];
                    await adapter.ExecuteAsync(frame.Token, action.Id); lastToken = frame.Token;
                    Write("last-navigation.json", frame);
                    await host.ToSignal(host.GetTree().CreateTimer(.3), SceneTreeTimer.SignalName.Timeout);
                }
            }
            Write("result.json", new { success = true, providerApiCalls = 0, slow, switched, quiescent, nativeAction, nextBattleReset, fastCompleted, battles, slowEntered, slowCancelled, assertions, timeline, elapsedSeconds = timer.Elapsed.TotalSeconds });
        }
        catch (Exception ex) { Write("result.json", new { success = false, providerApiCalls = 0, slow, switched, quiescent, nativeAction, nextBattleReset, fastCompleted, battles, slowEntered, slowCancelled, assertions, timeline, failure = ex.ToString() }); }
        finally { bridge.PauseOwnedAutomation(); SolverModelOwnership.Release(); harmony.UnpatchAll(harmony.Id); host.GetTree().Quit(); }
        async Task Frame() { timeout.Token.ThrowIfCancellationRequested(); await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame); }
        void Write(string file, object data) => File.WriteAllText(Path.Combine(output, file), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void DelaySearchUntilCancellation(CancellationToken __3)
    {
        if (Interlocked.CompareExchange(ref slowEntered, 1, 0) != 0) return;
        if (!__3.WaitHandle.WaitOne(TimeSpan.FromSeconds(70))) throw new TimeoutException("Isolated delayed search expected native cancellation.");
        Interlocked.Increment(ref slowCancelled); __3.ThrowIfCancellationRequested();
    }
    private static IEnumerable<Node> Walk(Node root) { yield return root; foreach (var child in root.GetChildren()) foreach (var descendant in Walk(child)) yield return descendant; }
}
