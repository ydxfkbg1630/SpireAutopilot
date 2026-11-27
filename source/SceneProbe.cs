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
using MegaCrit.Sts2.Core.Models.Potions;
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
        bool transformOnly = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_TRANSFORM_ONLY") == "1";
        bool crystalOnly = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_CRYSTAL_ONLY") == "1";
        bool potionOnly = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_POTION_FIXTURE") == "1";
        bool success = false;
        string? failure = null;
        try
        {
            if (RunManager.Instance.IsInProgress) throw new InvalidOperationException("Scene fixtures require a fresh disposable process.");
            await Wait(() => Walk(host.GetTree().Root).OfType<NMainMenu>().Any(n => n.IsNodeReady()), "main_menu");
            var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == (transformOnly ? "IRONCLAD" : "FOOL"));
            var starting = NGame.Instance!.StartNewSingleplayerRun(Player.CreateForNewRun(character, UnlockState.all, 1UL), false,
                ActModel.GetDefaultList(), Array.Empty<ModifierModel>(), "AUTOPILOTSCENES20261114", GameMode.Standard, false, 0);
            await Wait(() => NRun.Instance != null && RunManager.Instance.IsInProgress, "native_run");
            await DriveUntil(f => f.Scene == "NMapScreen", "starting_event",
                "tutorial", "dialogue", "confirm", "continue", "reward", "event", "relic", "select_card", "alternative");
            await Complete(starting, "start_task");
            Assert("starting_event_and_native_map", observed.Contains("NEventRoom") && observed.Contains("NMapScreen"), new { scenes = observed.ToArray() });
            var run = RunManager.Instance.DebugOnlyGetState()!;
            Player player = LocalContext.GetMe(run)!;
            if (potionOnly)
            {
                await Enter(RoomType.Shop, "potion_reward_fixture_room");
                for (int i = 0; i < player.PotionSlots.Count; i++)
                    await Complete(PotionCmd.TryToProcure<StrengthPotion>(player), "fill_potion_slot");
                Assert("potion_slots_full", !player.HasOpenPotionSlots, new { slots = player.PotionSlots.Count });
                var potionReward = new PotionReward(ModelDb.Potion<GamblersBrew>().ToMutable(), player);
                Task offer = new RewardsSet(player).WithCustomRewards(new List<Reward> { potionReward }).Offer();
                game.AllowNativeUiFallback = true;
                var full = await Frame(f => f.Scene == "NRewardsScreen" && f.Actions.Any(a => a.Kind.StartsWith("discard_potion_", StringComparison.Ordinal)));
                var potionContext = JsonSerializer.SerializeToElement(full.State).GetProperty("context").GetProperty("rewards")[0];
                Assert("full_potion_reward_visible_but_unclaimable",
                    full.Actions.All(a => a.Kind is not ("reward" or "native_ui")) &&
                    potionContext.TryGetProperty("claimable", out var claimable) && !claimable.GetBoolean() &&
                    potionContext.GetProperty("blockedReason").GetString() == "potion_inventory_full",
                    new { full.Actions, potionContext });
                await Execute(full, full.Actions.First(a => a.Kind.StartsWith("discard_potion_", StringComparison.Ordinal)));
                var open = await Frame(f => f.Scene == "NRewardsScreen" && f.Actions.Any(a => a.Kind == "reward"));
                Assert("potion_slot_open_after_native_discard", player.HasOpenPotionSlots && open.Token != full.Token,
                    new { player.HasOpenPotionSlots });
                await Execute(open, open.Actions.Single(a => a.Kind == "reward"));
                await Wait(() => player.PotionSlots.Any(p => p is GamblersBrew), "native_potion_reward_claimed");
                Assert("potion_claim_uses_native_inventory", player.PotionSlots.OfType<GamblersBrew>().Count() == 1,
                    new { slots = player.PotionSlots.Select(p => p?.Id.ToString()).ToArray() });
                if (!offer.IsCompleted)
                    await DriveUntil(f => f.Scene != "NRewardsScreen", "close_potion_rewards", "continue", "confirm");
                await Complete(offer, "potion_reward_offer_completed");
                success = true;
                stage = "potion_reward_completed";
                return;
            }
            if (crystalOnly)
            {
                await VerifyCrystal(player);
                success = true; stage = "crystal_completed"; return;
            }
            if (transformOnly)
            {
                await Enter(RoomType.Shop, "transform_fixture_room");
                await VerifyTransform(player, 1);
                await VerifyTransform(player, 2);
                await VerifyTransform(player, 1, true);
                success = true; stage = "transform_completed"; return;
            }
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

            await VerifyCrystal(player);
            success = true;
            stage = "completed";
        }
        catch (Exception ex) { failure = ex.ToString(); GD.PrintErr("[SpireAutopilot/SceneProbe] " + failure); }
        finally
        {
            Write("result.json", new { success, stage, failure, assertions, scenes = observed.ToArray(), actions = executed.Count, snapshots = snapshot,
                elapsedSeconds = timer.Elapsed.TotalSeconds, shouldSave = false, scripted = true, providerApiCalls = 0,
                scope = potionOnly ? "Native potion reward: full slots, discard, claim and inventory verification." : crystalOnly ? "Native disposable CrystalSphere: three sequential native clicks, observed fragments, compatibility inference, hidden-cell redaction and stale-action rejection." : transformOnly ? "Native disposable Ironclad fixture: fixed single/multiple and partial-range transformation selections, foreground confirmation, preview stability and native completion." : "Native disposable Fool run: initial event/map, merchant card purchase/removal, rest upgrade/confirm, gold/card reward, chest and first CrystalSphere cell. Scripted fixture setup is separate from player gameplay and model decisions." });
            GD.Print($"[SpireAutopilot/SceneProbe] DONE success={success} assertions={assertions.Count}");
            host.GetTree().Quit(success ? 0 : 1);
        }

        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, json));
        void Assert(string name, bool passed, object evidence)
        {
            assertions.Add(new { name, passed, evidence }); Write("assertions.json", assertions);
            if (!passed) throw new InvalidOperationException("Native fixture assertion: " + name);
        }
        async Task VerifyCrystal(Player player)
        {
            await Enter(RoomType.Shop, "crystal_fixture_room");
            var crystal = new CrystalSphereMinigame(player, new Rng(20261114UL), 6);
            Task crystalTask = crystal.PlayMinigame();
            for (int step = 0; step < 3; step++)
            {
                var frame = await Frame(f => f.Scene == "NCrystalSphereScreen" && f.Actions.Any(a => a.Kind == "scry_cell"));
                JsonElement Context(DecisionFrame f) => JsonSerializer.SerializeToElement(f.State).GetProperty("context");
                var context = Context(frame);
                Assert($"crystal_hidden_redaction_{step}", context.GetProperty("cells").EnumerateArray()
                    .Where(c => c.GetProperty("hidden").GetBoolean())
                    .All(c => c.GetProperty("itemId").ValueKind == JsonValueKind.Null && c.GetProperty("visibleItem").ValueKind == JsonValueKind.Null), new { step });
                Assert($"crystal_full_grid_{step}", context.GetProperty("cells").GetArrayLength() == 121, new { cells = context.GetProperty("cells").GetArrayLength() });
                string recommended = context.GetProperty("recommendedActionId").GetString()!;
                var action = frame.Actions.Single(a => a.Id == recommended);
                if (action.Kind == "scry_tool")
                {
                    int beforeTool = crystal.DivinationCount;
                    await Execute(frame, action);
                    frame = await Frame(f => f.Scene == "NCrystalSphereScreen"); context = Context(frame);
                    Assert($"crystal_free_tool_{step}", crystal.DivinationCount == beforeTool, new { beforeTool, after = crystal.DivinationCount });
                    action = frame.Actions.Single(a => a.Id == context.GetProperty("recommendedActionId").GetString());
                }
                int before = crystal.DivinationCount;
                int hidden = crystal.cells.Cast<CrystalSphereCell>().Count(c => c.IsHidden);
                int expected = Details(action).GetProperty("newCells").GetInt32();
                await Execute(frame, action);
                await Wait(() => crystal.DivinationCount == before - 1, "crystal_scry_" + step);
                var after = await Frame(f => f.Scene == "NCrystalSphereScreen" && f.Token != frame.Token);
                int afterHidden = crystal.cells.Cast<CrystalSphereCell>().Count(c => c.IsHidden);
                Assert($"crystal_native_coverage_{step}", hidden-afterHidden == expected, new { before, after = crystal.DivinationCount, expected, actual = hidden-afterHidden });
                bool rejected = false;
                try { await game.ExecuteAsync(frame.Token, action.Id); } catch (InvalidOperationException) { rejected = true; }
                Assert($"crystal_fresh_observation_{step}", rejected && Context(after).GetProperty("cells").EnumerateArray().Count(c => !c.GetProperty("hidden").GetBoolean()) > context.GetProperty("cells").EnumerateArray().Count(c => !c.GetProperty("hidden").GetBoolean()), new { rejected });
                var visible = Context(after).GetProperty("visibleItems");
                if (step == 2)
                {
                    string[] priorPartial = context.GetProperty("visibleItems").EnumerateArray().Where(i => !i.GetProperty("completed").GetBoolean()).Select(i => i.GetProperty("kind").GetString()!).ToArray();
                    Assert("crystal_partial_guides_next_click", priorPartial.Length > 0 && priorPartial.All(kind => visible.EnumerateArray().Any(i => i.GetProperty("kind").GetString() == kind && i.GetProperty("completed").GetBoolean())), new { priorPartial, afterItems = visible });
                }
                Assert($"crystal_partial_item_inference_{step}", visible.GetArrayLength() > 0 && visible.EnumerateArray().All(i => i.GetProperty("compatiblePlacements").GetInt32() > 0), visible);
            }
            var switchFrame = await Frame(f => f.Scene == "NCrystalSphereScreen");
            int beforeSwitch = crystal.DivinationCount;
            await Execute(switchFrame, switchFrame.Actions.Single(a => a.Kind == "scry_tool"));
            var smallFrame = await Frame(f => f.Scene == "NCrystalSphereScreen");
            Assert("crystal_tool_switch_progress_guard", crystal.DivinationCount == beforeSwitch && smallFrame.Actions.All(a => a.Kind != "scry_tool"), new { beforeSwitch, after = crystal.DivinationCount });
            var smallContext = JsonSerializer.SerializeToElement(smallFrame.State).GetProperty("context");
            var safe = smallFrame.Actions.Single(a => a.Id == smallContext.GetProperty("recommendedActionId").GetString());
            Assert("crystal_small_recommendation", safe.Kind == "scry_cell" && Details(safe).GetProperty("newCells").GetInt32() == 1, Details(safe));
            await Execute(smallFrame, safe);
            var progressed = await Frame(f => f.Scene == "NCrystalSphereScreen" && f.Token != smallFrame.Token);
            Assert("crystal_switch_then_cell_native", crystal.DivinationCount == beforeSwitch - 1 && progressed.Actions.Any(a => a.Kind == "scry_tool"), new { beforeSwitch, after = crystal.DivinationCount });
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
        async Task VerifyTransform(Player player, int count, bool partial = false)
        {
            string caseId = partial ? "partial" : count.ToString();
            stage = "transform_" + caseId;
            var originals = player.Deck.Cards.ToArray();
            var prefs = partial
                ? new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, 1, 2) { RequireManualConfirmation = true }
                : new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, count) { RequireManualConfirmation = true };
            var task = CardSelectCmd.FromDeckForTransformation(player, prefs);
            for (int selectedCount = 0; selectedCount < count; selectedCount++)
            {
                var choices = await Frame(f => f.Scene == "NDeckTransformSelectScreen" && f.Actions.Any(a => a.Kind == "select_card"));
                var candidate = choices.Actions.First(a => a.Kind == "select_card" && !Details(a).GetProperty("selected").GetBoolean());
                await Execute(choices, candidate);
            }
            if (partial)
            {
                var selection = await Frame(f => f.Scene == "NDeckTransformSelectScreen" && f.Actions.Any(a => a.Kind == "confirm"));
                await Execute(selection, selection.Actions.Single(a => a.Kind == "confirm"));
            }
            var preview = await Frame(f => f.Scene == "NDeckTransformSelectScreen" && DetailsContext(f).GetProperty("preview").GetBoolean());
            Assert("transform_" + caseId + "_one_foreground_confirm", preview.Actions.Count(a => a.Kind == "confirm") == 1
                && preview.Actions.All(a => a.Kind is "confirm" or "cancel_selection"), preview.Actions);
            string token = preview.Token;
            double previewUntil = timer.Elapsed.TotalSeconds + 2;
            while (timer.Elapsed.TotalSeconds < previewUntil) await Next();
            var settled = game.Capture() ?? throw new InvalidOperationException("Transform preview disappeared");
            if (settled.Token != token) Write("transform-token-difference-" + count + ".json", new { before = preview, after = settled });
            Assert("transform_" + caseId + "_stable_preview", settled.Token == token, new { before = token, after = settled.Token });
            await Execute(settled, settled.Actions.Single(a => a.Kind == "confirm"));
            await Complete(task, "transform_" + caseId + "_complete");
            var chosen = await task;
            Assert("transform_" + caseId + "_native_completion", chosen.Count() == count, new { selected = chosen.Select(c => c.Id.ToString()).ToArray() });
        }
        JsonElement DetailsContext(DecisionFrame f) => JsonSerializer.SerializeToElement(f.State).GetProperty("context");

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
