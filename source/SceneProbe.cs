using System.Diagnostics;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace SpireAutopilot;

/// <summary>Explicit opt-in native scene fixtures, isolated profile, no provider API and no persisted run.</summary>
public static class SceneProbe
{
    public static async Task RunAsync(AutopilotHost host)
    {
        string? output = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_SCENE_PROBE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var json = new JsonSerializerOptions { WriteIndented = true };
        var timer = Stopwatch.StartNew();
        var game = new GameAdapter();
        var assertions = new List<object>();
        var executed = new List<object>();
        var observed = new HashSet<string>();
        var captured = new HashSet<string>();
        string stage = "startup";
        int snapshot = 0;
        bool success = false;
        string? failure = null;
        try
        {
            if (RunManager.Instance.IsInProgress) throw new InvalidOperationException("Scene fixtures require a fresh disposable process.");
            await Wait(() => Walk(host.GetTree().Root).OfType<NMainMenu>().Any(n => n.IsNodeReady()), "main_menu");
            var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == "FOOL");
            var starting = NGame.Instance!.StartNewSingleplayerRun(Player.CreateForNewRun(character, UnlockState.all, 1UL), false,
                ActModel.GetDefaultList(), Array.Empty<ModifierModel>(), "AUTOPILOTSCENES20261114", GameMode.Standard, false, 0);
            await Wait(() => NRun.Instance != null && RunManager.Instance.IsInProgress, "native_run");
            await DriveUntil(f => f.Scene == "NMapScreen", "starting_event",
                "tutorial", "dialogue", "confirm", "continue", "reward", "event", "relic", "select_card", "alternative");
            await Complete(starting, "start_task");
            Assert("starting_event_and_native_map", observed.Contains("NEventRoom") && observed.Contains("NMapScreen"), new { scenes = observed.ToArray() });
            var run = RunManager.Instance.DebugOnlyGetState()!;
            Player player = LocalContext.GetMe(run)!;
            player.Gold = 2000;
            Write("fixture-setup.json", new { shouldSave = false, nativeFixtures = true, scripted = true, providerApiCalls = 0, playerGold = 2000, injectedStatePurpose = "exercise merchant purchase and deletion deterministically" });

            await Enter(RoomType.Shop, "shop");
            var shop = await Frame(f => f.Actions.Any(a => a.Kind == "purchase") || f.Actions.Any(a => a.Kind == "open_shop"));
            if (shop.Actions.Any(a => a.Kind == "open_shop")) await Execute(shop, shop.Actions.First(a => a.Kind == "open_shop"));
            shop = await Frame(f => f.Actions.Any(a => a.Kind == "purchase" && Details(a).GetProperty("entryType").GetString() == "MerchantCardEntry"));
            var buy = shop.Actions.First(a => a.Kind == "purchase" && Details(a).GetProperty("entryType").GetString() == "MerchantCardEntry");
            var item = Details(buy).GetProperty("item");
            Assert("shop_card_effect_semantics", item.GetProperty("description").GetString()?.Length > 0 && item.GetProperty("name").GetString()?.Length > 0, item);
            int gold = player.Gold, deck = player.Deck.Cards.Count;
            int expectedPrice = Details(buy).GetProperty("cost").GetInt32();
            await Execute(shop, buy);
            await Wait(() => player.Deck.Cards.Count == deck + 1 && player.Gold == gold - expectedPrice, "purchase_effect");
            Assert("shop_purchase_deck_and_gold", true, new { beforeGold = gold, afterGold = player.Gold, expectedPrice, beforeDeck = deck, afterDeck = player.Deck.Cards.Count });
            bool staleRejected = false;
            try { await game.ExecuteAsync(shop.Token, buy.Id); } catch (InvalidOperationException) { staleRejected = true; }
            Assert("stale_purchase_rejected", staleRejected, new { beforeToken = shop.Token, currentToken = game.Capture()?.Token });

            shop = await Frame(f => f.Actions.Any(a => a.Kind == "remove_card"));
            gold = player.Gold; deck = player.Deck.Cards.Count;
            await Execute(shop, shop.Actions.First(a => a.Kind == "remove_card"));
            await SelectAndConfirm("merchant_remove", () => player.Deck.Cards.Count == deck - 1);
            Assert("shop_remove_card_and_gold", player.Deck.Cards.Count == deck - 1 && player.Gold < gold,
                new { beforeGold = gold, afterGold = player.Gold, beforeDeck = deck, afterDeck = player.Deck.Cards.Count });

            await Enter(RoomType.RestSite, "rest_site");
            var rest = await Frame(f => f.Actions.Any(a => a.Kind == "rest" && JsonSerializer.Serialize(a.Details).Contains("SMITH", StringComparison.OrdinalIgnoreCase)));
            int upgradeBefore = player.Deck.Cards.Sum(c => c.CurrentUpgradeLevel);
            await Execute(rest, rest.Actions.First(a => a.Kind == "rest" && JsonSerializer.Serialize(a.Details).Contains("SMITH", StringComparison.OrdinalIgnoreCase)));
            await SelectAndConfirm("rest_upgrade", () => player.Deck.Cards.Sum(c => c.CurrentUpgradeLevel) > upgradeBefore);
            Assert("rest_upgrade_effect", player.Deck.Cards.Sum(c => c.CurrentUpgradeLevel) > upgradeBefore,
                new { before = upgradeBefore, after = player.Deck.Cards.Sum(c => c.CurrentUpgradeLevel) });
            await DriveUntil(f => f.Scene == "NMapScreen", "rest_proceed", "tutorial", "confirm", "continue");

            await Enter(RoomType.Shop, "reward_fixture_room");
            var options = new CardCreationOptions(Array.Empty<CardPoolModel>(), CardCreationSource.Other, CardRarityOddsType.Uniform);
            // Fixed native fixture makes the upgraded effect assertion independent of generated shop/deck RNG.
            CardModel kiWhip = run.CreateCard(ModelDb.Card<KiWhip>(), player);
            var offeredCards = new[] { kiWhip }.Concat(player.Deck.Cards.Take(2).Select(run.CloneCard)).ToArray();
            var cardReward = new CardReward(offeredCards, CardCreationSource.Other, player, options);
            var rewardSet = new RewardsSet(player).WithCustomRewards(new List<Reward> { new GoldReward(31, player), cardReward });
            Task offering = rewardSet.Offer();
            int goldBeforeReward = player.Gold;
            var rewards = await Frame(f => f.Scene == "NRewardsScreen" && f.Actions.Any(a => a.Kind == "reward" && JsonSerializer.Serialize(a.Details).Contains("GoldReward")));
            await Execute(rewards, rewards.Actions.First(a => a.Kind == "reward" && JsonSerializer.Serialize(a.Details).Contains("GoldReward")));
            await Wait(() => player.Gold == goldBeforeReward + 31, "gold_reward_effect");
            Assert("gold_reward_amount", player.Gold == goldBeforeReward + 31, new { before = goldBeforeReward, after = player.Gold, expectedGain = 31 });
            rewards = await Frame(f => f.Scene == "NRewardsScreen" && f.Actions.Any(a => a.Kind == "reward" && JsonSerializer.Serialize(a.Details).Contains("CardReward")));
            deck = player.Deck.Cards.Count;
            await Execute(rewards, rewards.Actions.First(a => a.Kind == "reward" && JsonSerializer.Serialize(a.Details).Contains("CardReward")));
            var kiFrame = await Frame(f => f.Scene == "NCardRewardSelectionScreen" && f.Actions.Any(IsKiWhip));
            JsonElement kiSnapshot = Details(kiFrame.Actions.First(IsKiWhip)).GetProperty("card");
            CardModel independentlyUpgraded = (CardModel)kiWhip.ClonePreservingMutability();
            independentlyUpgraded.UpgradeInternal();
            string nativeBaseDescription = kiWhip.GetDescriptionForPile(PileType.Deck);
            string nativeUpgradeDescription = independentlyUpgraded.GetDescriptionForPile(PileType.Deck);
            Assert("ki_whip_native_upgrade_snapshot_5_to_6_damage_2_to_3_next_turn_energy",
                player.Deck.Cards.Count == deck && kiWhip.CurrentUpgradeLevel == 0 && kiWhip.DynamicVars.Damage.BaseValue == 5m && kiWhip.DynamicVars.Energy.BaseValue == 2m
                && independentlyUpgraded.DynamicVars.Damage.BaseValue == 6m && independentlyUpgraded.DynamicVars.Energy.BaseValue == 3m
                && kiSnapshot.GetProperty("description").GetString() == nativeBaseDescription
                && kiSnapshot.GetProperty("upgradeDescription").GetString() == nativeUpgradeDescription
                && nativeBaseDescription != nativeUpgradeDescription
                && kiSnapshot.GetProperty("upgradeCost").GetInt32() == independentlyUpgraded.EnergyCost.GetResolved()
                && kiSnapshot.GetProperty("canUpgrade").GetBoolean()
                && kiSnapshot.GetProperty("upgradePreviewError").ValueKind == JsonValueKind.Null,
                new { card = kiWhip.Id.ToString(), baseDamage = kiWhip.DynamicVars.Damage.BaseValue,
                    baseNextTurnEnergy = kiWhip.DynamicVars.Energy.BaseValue,
                    upgradedDamage = independentlyUpgraded.DynamicVars.Damage.BaseValue,
                    upgradedNextTurnEnergy = independentlyUpgraded.DynamicVars.Energy.BaseValue,
                    nativeBaseDescription, nativeUpgradeDescription, adapterSnapshot = kiSnapshot,
                    originalUpgradeLevelAfterCapture = kiWhip.CurrentUpgradeLevel,
                    originalDeckCountAfterCapture = player.Deck.Cards.Count, expectedDeckCount = deck });
            await Execute(kiFrame, kiFrame.Actions.First(IsKiWhip));
            await SelectAndConfirm("card_reward", () => player.Deck.Cards.Count == deck + 1);
            Assert("card_reward_deck_effect", player.Deck.Cards.Count == deck + 1, new { before = deck, after = player.Deck.Cards.Count });
            await DriveUntil(f => f.Scene != "NRewardsScreen" && f.Scene != "NCardRewardSelectionScreen", "close_rewards", "confirm", "continue");
            await Complete(offering, "reward_offer_completed");

            var startField = typeof(RunManager).GetField("_startTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            long nativeStart = (long)startField.GetValue(RunManager.Instance)!;
            string originalRunIdentity = game.RunIdentity!;
            try
            {
                startField.SetValue(RunManager.Instance, nativeStart + 1);
                Assert("same_seed_distinct_native_start_identity", game.RunIdentity != originalRunIdentity,
                    new { nativeStart, originalRunIdentity, newIdentity = game.RunIdentity, sameSeed = run.Rng.StringSeed });
            }
            finally { startField.SetValue(RunManager.Instance, nativeStart); }
            Assert("restored_native_start_identity_stable", game.RunIdentity == originalRunIdentity, new { originalRunIdentity, restored = game.RunIdentity });

            Task<IEnumerable<CardModel>> transforming = CardSelectCmd.FromDeckForTransformation(player,
                new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, 1) { RequireManualConfirmation = true });
            var transformFrame = await Frame(f => f.Actions.Any(a => a.Kind == "select_card"));
            Assert("transform_choice_full_context", JsonSerializer.Serialize(transformFrame.State).Contains("deck")
                && Details(transformFrame.Actions.First(a => a.Kind == "select_card")).GetProperty("card").GetProperty("description").GetString() is { Length: > 0 },
                new { transformFrame.Scene, transformFrame.State });
            await SelectAndConfirm("transformation_selection", () => transforming.IsCompleted);
            await Complete(transforming, "transformation_choice_completed");

            IReadOnlyList<IReadOnlyList<CardModel>> bundles = new IReadOnlyList<CardModel>[]
            { player.Deck.Cards.Take(2).Select(run.CloneCard).ToArray(), player.Deck.Cards.Skip(2).Take(2).Select(run.CloneCard).ToArray() };
            Task<IEnumerable<CardModel>> bundleTask = CardSelectCmd.FromChooseABundleScreen(player, bundles);
            var bundleFrame = await Frame(f => f.Scene == "NChooseABundleSelectionScreen" && f.Actions.Any(a => a.Kind == "select_bundle"));
            int legacyHolderCount = Walk(MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext.Instance.GetCurrentScreen() as Node ?? throw new InvalidOperationException("Bundle screen expected"))
                .OfType<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder>().Count(h => h.CardModel != null && h.IsVisibleInTree() && h.Hitbox.IsEnabled && h.GetType().Name != "NPreviewCardHolder");
            Assert("bundle_native_semantics_only", bundleFrame.Actions.Count(a => a.Kind == "select_bundle") == 2
                && bundleFrame.Actions.All(a => a.Kind != "select_card"), new { legacyHolderCount, scope = "Native bundle selection and confirmation; individual card inspection is outside the legal action list", actions = bundleFrame.Actions });
            await Execute(bundleFrame, bundleFrame.Actions.First(a => a.Kind == "select_bundle"));
            var bundleConfirm = await Frame(f => f.Actions.Any(a => a.Kind == "confirm"));
            await Execute(bundleConfirm, bundleConfirm.Actions.First(a => a.Kind == "confirm"));
            await Complete(bundleTask, "native_bundle_complete");
            game.AllowNativeUiFallback = true;
            var afterSelection = game.Capture();
            Assert("completed_selection_waits_even_with_fallback", afterSelection == null || afterSelection.Scene != "NChooseABundleSelectionScreen",
                new { frame = afterSelection, game.Status });
            game.AllowNativeUiFallback = false;

            await Enter(RoomType.Treasure, "treasure");
            int relics = player.Relics.Count, chestGold = player.Gold;
            var chest = await Frame(f => f.Actions.Any(a => a.Kind == "open_chest"));
            await Execute(chest, chest.Actions.First(a => a.Kind == "open_chest"));
            await DriveEffect(() => player.Relics.Count > relics || player.Gold > chestGold, "treasure_effect", "tutorial", "reward", "relic", "confirm", "continue");
            Assert("treasure_native_effect", player.Relics.Count > relics || player.Gold > chestGold,
                new { beforeRelics = relics, afterRelics = player.Relics.Count, beforeGold = chestGold, afterGold = player.Gold });

            await Enter(RoomType.Shop, "crystal_fixture_room");
            var crystal = new CrystalSphereMinigame(player, new Rng(20261114UL), 3);
            Task crystalTask = crystal.PlayMinigame();
            int hidden = crystal.cells.Cast<MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereCell>().Count(c => c.IsHidden);
            var sphere = await Frame(f => f.Scene == "NCrystalSphereScreen" && f.Actions.Any(a => a.Kind == "scry_cell"));
            await Execute(sphere, sphere.Actions.First(a => a.Kind == "scry_cell"));
            await Wait(() => crystal.DivinationCount == 2, "crystal_first_scry");
            Assert("crystal_scry_native_effect", crystal.DivinationCount == 2 && crystal.cells.Cast<MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereCell>().Count(c => c.IsHidden) < hidden,
                new { beforeDivinations = 3, afterDivinations = crystal.DivinationCount, beforeHiddenCells = hidden,
                    afterHiddenCells = crystal.cells.Cast<MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereCell>().Count(c => c.IsHidden), pendingTask = crystalTask.Status.ToString() });
            success = true;
            stage = "completed";
        }
        catch (Exception ex) { failure = ex.ToString(); GD.PrintErr("[SpireAutopilot/SceneProbe] " + failure); }
        finally
        {
            Write("result.json", new { success, stage, failure, assertions, scenes = observed.ToArray(), actions = executed.Count, snapshots = snapshot,
                elapsedSeconds = timer.Elapsed.TotalSeconds, shouldSave = false, scripted = true, providerApiCalls = 0,
                scope = "Native disposable Fool run: initial event/map, merchant card purchase/removal, rest upgrade/confirm, gold/card reward, chest and first CrystalSphere cell. Scripted fixture setup is separate from player gameplay and model decisions." });
            GD.Print($"[SpireAutopilot/SceneProbe] DONE success={success} assertions={assertions.Count}");
            host.GetTree().Quit(success ? 0 : 1);
        }

        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, json));
        void Assert(string name, bool passed, object evidence)
        {
            assertions.Add(new { name, passed, evidence }); Write("assertions.json", assertions);
            if (!passed) throw new InvalidOperationException("Native fixture assertion: " + name);
        }
        async Task Next()
        {
            if (timer.Elapsed.TotalSeconds >= 180) throw new TimeoutException("Scene probe 180-second deadline at " + stage + "; " + game.Status);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        async Task Wait(Func<bool> ready, string name)
        {
            stage = name; double until = Math.Min(180, timer.Elapsed.TotalSeconds + 25);
            while (!ready()) { if (timer.Elapsed.TotalSeconds >= until) throw new TimeoutException("Scene fixture timeout: " + name); await Next(); }
        }
        async Task Complete(Task task, string name) { await Wait(() => task.IsCompleted, name); await task; }
        async Task Enter(RoomType type, string name)
        {
            stage = name;
            await Complete(RunManager.Instance.EnterRoomDebug(type, showTransition: false), name + "_enter");
        }
        DecisionFrame? Capture()
        {
            var frame = game.Capture();
            if (frame != null)
            {
                observed.Add(frame.Scene);
                if (captured.Add(frame.Token)) Write($"frame-{++snapshot:D3}-{frame.Scene}.json", frame);
            }
            return frame;
        }
        async Task<DecisionFrame> Frame(Func<DecisionFrame, bool> predicate)
        {
            double until = Math.Min(180, timer.Elapsed.TotalSeconds + 25);
            while (true)
            {
                await Next(); var f = Capture();
                if (f != null && predicate(f)) return f;
                if (f?.Actions.FirstOrDefault(a => a.Kind == "tutorial") is { } tutorial) await Execute(f, tutorial);
                if (timer.Elapsed.TotalSeconds >= until) throw new TimeoutException("Waiting for scene at " + stage + "; " + game.Status);
            }
        }
        async Task Execute(DecisionFrame frame, LegalAction action)
        {
            await game.ExecuteAsync(frame.Token, action.Id);
            executed.Add(new { stage, scene = frame.Scene, action.Id, action.Kind, action.Label, elapsedSeconds = timer.Elapsed.TotalSeconds }); Write("actions.json", executed);
            double settle = timer.Elapsed.TotalSeconds + .3;
            while (timer.Elapsed.TotalSeconds < settle) await Next();
        }
        async Task DriveUntil(Func<DecisionFrame, bool> done, string name, params string[] preference)
        {
            stage = name;
            for (int i = 0; i < 35; i++)
            {
                var f = await Frame(_ => true); if (done(f)) return;
                var action = preference.SelectMany(k => f.Actions.Where(a => a.Kind == k)).FirstOrDefault();
                if (action == null) throw new InvalidOperationException("Fixture " + name + " has no scripted action in " + f.Scene);
                await Execute(f, action);
            }
            throw new InvalidOperationException("Fixture action bound: " + name);
        }
        async Task DriveEffect(Func<bool> done, string name, params string[] preference)
        {
            stage = name;
            for (int i = 0; i < 25 && !done(); i++)
            {
                var f = await Frame(_ => true); if (done()) return;
                var action = preference.SelectMany(k => f.Actions.Where(a => a.Kind == k)).FirstOrDefault();
                if (action != null) await Execute(f, action); else await Next();
            }
            await Wait(done, name + "_effect");
        }
        async Task SelectAndConfirm(string name, Func<bool> effect)
        {
            stage = name;
            await DriveEffect(effect, name, "tutorial", "confirm", "select_card");
        }
    }

    private static JsonElement Details(LegalAction action) => JsonSerializer.SerializeToElement(action.Details);
    private static bool IsKiWhip(LegalAction action) => action.Kind == "select_card"
        && Details(action).GetProperty("card").GetProperty("id").GetString()?.Contains("KI_WHIP", StringComparison.Ordinal) == true;
    private static IEnumerable<Node> Walk(Node node)
    {
        yield return node;
        foreach (var child in node.GetChildren()) foreach (var descendant in Walk(child)) yield return descendant;
    }
}
