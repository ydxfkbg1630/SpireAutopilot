using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;

namespace SpireAutopilot;

/// <summary>All entry points run on the Godot main thread. Reads live native models and dispatches native UI semantics.</summary>
public sealed partial class GameAdapter
{
    private readonly Dictionary<string, Action> dispatch = new(StringComparer.Ordinal);
    private readonly List<LegalAction> actions = new();
    private readonly List<Task> nativeTasks = new();
    private string? nativeFailure;
    private bool selectionWaiting;
    public bool AllowNativeUiFallback { get; set; }
    public string Status { get; private set; } = "等待游戏场景。";
    public bool InCombat => CombatManager.Instance.IsInProgress;
    public bool RunActive => RunManager.Instance.IsInProgress;
    public bool RunFinished => RunManager.Instance.IsGameOver || RunManager.Instance.HasRunEnded;
    public string? RunIdentity => RunManager.Instance.DebugOnlyGetState() is { } run
        ? NativeRunIdentity(run) : null;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, RunInstanceIdentity> UnsavedRunIds = new();
    private sealed class RunInstanceIdentity { public string Value { get; } = Guid.NewGuid().ToString("N"); }
    private static string NativeRunIdentity(MegaCrit.Sts2.Core.Runs.IRunState run)
    {
        // Native RunManager persists _startTime as SerializableRun.StartTime and restores it on continuation.
        // Scope includes character/seed for older saves and a per-object fallback for isolated unsaved bootstraps.
        string start = Member(RunManager.Instance, "_startTime") is long timestamp && timestamp > 0
            ? timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "session-" + UnsavedRunIds.GetValue(run, static _ => new RunInstanceIdentity()).Value;
        return "run-v2:" + start + ":" + run.Rng.StringSeed + ":" + run.Players.FirstOrDefault()?.Character.Id + ":" + run.AscensionLevel;
    }
    public object? CaptureRunObservation()
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        var player = run == null ? null : LocalContext.GetMe(run);
        return run == null || player == null ? null : new
        {
            runKey = RunIdentity, run.CurrentActIndex, run.ActFloor, run.TotalFloor,
            player = SnapshotPlayer(player), currentMapPoint = run.CurrentMapCoord is { } point ? new { row = point.row, column = point.col } : null,
            room = run.CurrentRoom?.GetType().Name
        };
    }

    public bool TryHandleTutorial()
    {
        if (NModalContainer.Instance?.OpenModal is not Node modal || !LiveVisible(modal)
            || !modal.GetType().Name.Contains("Ftue", StringComparison.Ordinal)) return false;
        var confirm = Walk(modal).OfType<NClickableControl>().FirstOrDefault(n => Enabled(n)
            && n.Name.ToString().Contains("Confirm", StringComparison.Ordinal));
        if (confirm == null) return false;
        Click(confirm);
        return true;
    }

    public DecisionFrame? Capture()
    {
        dispatch.Clear();
        actions.Clear();
        selectionWaiting = false;
        foreach (var task in nativeTasks.Where(t => t.IsCompleted).ToArray())
        {
            if (task.IsFaulted) nativeFailure = task.Exception?.GetBaseException().Message;
            nativeTasks.Remove(task);
        }
        if (nativeFailure != null)
        {
            Status = "原生动作反馈：" + nativeFailure;
            nativeFailure = null;
            throw new InvalidOperationException(Status);
        }
        if (!RunActive || NRun.Instance == null) { Status = "等待进入一局游戏。"; return null; }
        if (RunFinished) { Status = "本局已结束。"; return null; }
        if (InCombat) { Status = "战斗交由 CombatSolver 执行。"; return null; }
        if (RunManager.Instance.IsChangingRooms || RunManager.Instance.IsPaused)
        { Status = "等待场景转换或继续游戏。"; return null; }
        var run = RunManager.Instance.DebugOnlyGetState();
        if (run == null) return null;
        if (run.Players.Count != 1)
        { Status = "当前领航支持单人游戏。"; return null; }
        var player = LocalContext.GetMe(run);
        if (player == null) { Status = "等待本地角色初始化。"; return null; }
        Node? scene = ActiveScreenContext.Instance.GetCurrentScreen() as Node;
        if (scene == null || !LiveVisible(scene)) { Status = "等待可操作场景。"; return null; }
        // Menu/settings/deck inspection screens intentionally have no gameplay actions.
        string sceneType = scene.GetType().Name;
        if (IsBlocked(sceneType)) { Status = "等待返回游戏决策界面。"; return null; }
        object extra;
        if (NModalContainer.Instance?.OpenModal is Node modal && ReferenceEquals(modal, scene))
        {
            if (sceneType.Contains("Ftue", StringComparison.Ordinal))
                foreach (var confirm in Walk(scene).OfType<NClickableControl>().Where(n => Enabled(n) && n.Name.ToString().Contains("Confirm", StringComparison.Ordinal)))
                    Add(confirm, "确认教学提示", "tutorial", null, () => Click(confirm));
            extra = new { text = VisibleTexts(scene), modal = true };
        }
        else if (scene is NMapScreen mapScreen)
        {
            extra = CaptureMap(mapScreen);
        }
        else if (scene is NCrystalSphereScreen)
        {
            extra = CaptureCrystalSphere(scene);
        }
        else if (scene is ICardSelectionScreen or NCardRewardSelectionScreen or NCardGridSelectionScreen or NChooseABundleSelectionScreen)
        {
            extra = CaptureCards(scene);
        }
        else if (scene is NRewardsScreen)
        {
            foreach (var reward in Walk(scene).OfType<NRewardButton>().Where(Enabled))
            {
                var local = reward;
                Add(local, "领取奖励：" + Text(Member(local.Reward, "Description")), "reward", SnapshotReward(local.Reward), () => Click(local));
            }
            if (Walk(scene).OfType<NRewardButton>().Any(b => b.Reward?.GetType().Name == "PotionReward")) AddPotionDiscardActions(scene, player);
            AddProgressButtons(scene);
            extra = new { rewards = Walk(scene).OfType<NRewardButton>().Select(n => new { node = n.GetInstanceId(), enabled = Enabled(n), reward = SnapshotReward(n.Reward) }).ToArray() };
        }
        else if (scene is NChooseARelicSelection || sceneType == "NTreasureRoom")
        {
            foreach (var node in Walk(scene).OfType<NClickableControl>().Where(Enabled))
            {
                if (node.GetType().Name is "NRelicBasicHolder" or "NTreasureRoomRelicHolder")
                {
                    var relic = Member(Member(node, "Relic"), "Model") ?? Member(node, "RelicModel");
                    Add(node, "选择遗物：" + ModelTitle(relic), "relic", SnapshotModel(relic), () => Click(node));
                }
                else if (node.Name.ToString() == "Chest" || node.GetType().Name == "NTreasureButton")
                    Add(node, "打开宝箱", "open_chest", null, () => Click(node));
            }
            AddProgressButtons(scene);
            extra = new { text = VisibleTexts(scene) };
        }
        else if (Walk(scene).OfType<NMerchantInventory>().FirstOrDefault(i => i.IsOpen) is { } inventory)
        {
            extra = CaptureShop(scene, inventory, player);
        }
        else if (Walk(scene).OfType<NEventOptionButton>().Any() || sceneType == "NEventRoom")
        {
            extra = CaptureEvent(scene);
        }
        else if (sceneType == "NRestSiteRoom")
        {
            foreach (var option in Walk(scene).OfType<NRestSiteButton>().Where(b => Enabled(b) && b.Option.IsEnabled))
                Add(option, Text(option.Option.Title), "rest", new { option = option.Option.OptionId, description = Text(option.Option.Description) }, () => Click(option));
            AddProgressButtons(scene);
            extra = new { text = VisibleTexts(scene) };
        }
        else
        {
            foreach (var merchant in Walk(scene).OfType<NClickableControl>().Where(b => Enabled(b) && b.GetType().Name == "NMerchantButton"))
                Add(merchant, "查看商店商品", "open_shop", null, () => Click(merchant));
            AddProgressButtons(scene);
            if (AllowNativeUiFallback) AddNativeFallback(scene);
            extra = new { text = VisibleTexts(scene) };
        }
        if (selectionWaiting) return null;
        if (actions.Count == 0)
        {
            if (AllowNativeUiFallback) AddNativeFallback(scene);
            if (actions.Count == 0) { Status = "等待原生可用动作：" + sceneType; return null; }
        }
        var state = new
        {
            runKey = NativeRunIdentity(run),
            currentMapPoint = run.CurrentMapCoord is { } coordinate ? new { row = coordinate.row, column = coordinate.col } : null,
            sceneType, sceneInstance = scene.GetInstanceId(), run.CurrentActIndex, run.ActFloor,
            run.TotalFloor, run.AscensionLevel, room = run.CurrentRoom?.GetType().Name,
            actMap = SnapshotActMap(run),
            player = SnapshotPlayer(player), context = extra,
            // Visible controls include enabled/selection state, but exclude position, animation, frame clock and VFX.
            controls = Walk(CardSelectionScope(scene)).Where(n => n is NClickableControl || n is NCardHolder).Where(LiveVisible)
                .Select(n => new { id = n.GetInstanceId(), type = n.GetType().Name, enabled = n is NClickableControl b ? b.IsEnabled : Member(n, "_isClickable") }).ToArray()
        };
        var stable = JsonSerializer.Serialize(new { state, actions });
        string token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable)));
        Status = "可决策：" + sceneType;
        return new DecisionFrame(token, sceneType, state, actions.ToArray());
    }

    public Task ExecuteAsync(string token, string actionId)
    {
        if (!TryExecuteCurrent(token, actionId))
            throw new InvalidOperationException("游戏状态已更新，请使用新场景重新决策。");
        return Task.CompletedTask;
    }

    internal bool TryExecuteCurrent(string token, string actionId)
    {
        var fresh = Capture();
        if (fresh == null || !StringComparer.Ordinal.Equals(token, fresh.Token)) return false;
        if (!dispatch.TryGetValue(actionId, out var execute))
            throw new InvalidOperationException("动作需要来自当前场景的合法动作列表。");
        // A native purchase/event may await another card-selection screen. Dispatch without awaiting that nested choice.
        execute();
        Status = "已提交原生动作：" + actionId;
        return true;
    }

    private object CaptureMap(NMapScreen screen)
    {
        var points = Walk(screen).OfType<NMapPoint>().Where(n => n.Point != null).OrderBy(n => n.Point.coord.row).ThenBy(n => n.Point.coord.col).ToArray();
        if (screen.IsTravelEnabled && !screen.IsTraveling)
            foreach (var point in points.Where(p => p.IsEnabled && p.State == MapPointState.Travelable))
                Add(point, $"前往第 {point.Point.coord.row} 层第 {point.Point.coord.col} 列：{point.Point.PointType}", "travel",
                    MapInfo(point.Point), () => screen.OnMapPointSelectedLocally(point));
        return new { screen.IsTravelEnabled, screen.IsTraveling, map = points.Select(n => MapInfo(n.Point)).ToArray() };
    }

    private object CaptureEvent(Node scene)
    {
        foreach (var option in Walk(scene).OfType<NEventOptionButton>().Where(b => Enabled(b) && !b.Option.IsLocked))
        {
            var o = option.Option;
            bool lethal = o.WillKillPlayer != null && option.Event.Owner != null && o.WillKillPlayer(option.Event.Owner);
            Add(option, Text(o.Title), o.IsProceed ? "continue" : "event", new { description = Text(o.Description), lethal, relic = SnapshotModel(o.Relic) }, () => Click(option));
        }
        if (actions.Count == 0)
        {
            foreach (var dialogue in Walk(scene).OfType<NClickableControl>().Where(n => Enabled(n) && n.Name.ToString() == "DialogueHitbox"))
                Add(dialogue, "继续古神对话", "dialogue", null, () => Click(dialogue));
            AddProgressButtons(scene);
        }
        return new { text = VisibleTexts(scene), options = Walk(scene).OfType<NEventOptionButton>().Select(n => new { title = Text(n.Option.Title), description = Text(n.Option.Description), n.Option.IsLocked }).ToArray() };
    }

    private object CaptureShop(Node scene, NMerchantInventory inventory, Player player)
    {
        var slots = Walk(inventory).OfType<NMerchantSlot>().Where(LiveVisible).Where(s => s.Entry != null).ToArray();
        bool blocked = Member(inventory, "_isInputBlocked") is true;
        if (!blocked)
        {
            if (slots.Any(s => s.Entry.IsStocked && s.Entry.EnoughGold && s.Entry.GetType().Name.Contains("Potion"))) AddPotionDiscardActions(scene, player);
            foreach (var slot in slots.Where(s => s.Entry.IsStocked && s.Entry.EnoughGold))
            {
                var entry = slot.Entry;
                object? model = MerchantModel(entry);
                if (entry.GetType().Name.Contains("Potion") && !player.HasOpenPotionSlots) continue;
                string title = entry.GetType().Name.Contains("Removal") ? "删除一张卡牌" : ModelTitle(model);
                Add(slot, $"购买：{title}（{entry.Cost} 金币）", entry.GetType().Name.Contains("Removal") ? "remove_card" : "purchase",
                    new { cost = entry.Cost, item = SnapshotModel(model), entryType = entry.GetType().Name },
                    () => nativeTasks.Add(RunManager.Instance.MerchantSynchronizer.PurchaseLocalEntry(entry)));
            }
            foreach (var back in Walk(inventory).OfType<NClickableControl>().Where(n => Enabled(n) && n.GetType().Name == "NBackButton"))
                Add(back, "关闭商店商品", "close_shop", null, () => Click(back));
        }
        return new { inventory.IsOpen, blocked, stock = slots.Select(s => new { node = s.GetInstanceId(), s.Entry.Cost, s.Entry.IsStocked, s.Entry.EnoughGold, item = SnapshotModel(MerchantModel(s.Entry)), type = s.Entry.GetType().Name }).ToArray() };
    }

    private static Node CardSelectionScope(Node scene)
    {
        if (scene is not ICardSelectionScreen) return scene;
        foreach (string name in new[] { "_previewContainer", "_upgradeSinglePreviewContainer", "_upgradeMultiPreviewContainer", "_enchantSinglePreviewContainer", "_enchantMultiPreviewContainer", "_bundlePreviewContainer" })
            if (Member(scene, name) is Control preview && LiveVisible(preview)) return preview;
        return scene;
    }

    private object CaptureCards(Node scene)
    {
        object? completion = Member(scene, "_completionSource");
        if (Member(completion, "Task") is Task { IsCompleted: true }
            || Member(scene, "_screenComplete") is true)
        { selectionWaiting = true; Status = "等待选牌结果进入原生动作队列。"; return new { selectionCompleted = true }; }
        if (scene is NChooseACardSelectionScreen && Member(scene, "_openedTicks") is ulong opened
            && Time.GetTicksMsec() - opened <= 350)
        { selectionWaiting = true; Status = "等待选牌界面入场完成。"; return new { selectionEntering = true }; }
        var selected = Items(Member(scene, "_selectedCards")).OfType<CardModel>().ToArray();
        var prefs = Member(scene, "_prefs");
        Node selectionScope = CardSelectionScope(scene);
        bool preview = !ReferenceEquals(selectionScope, scene);
        var grid = Walk(scene).OfType<NCardGrid>().FirstOrDefault();
        var holders = Walk(scene).OfType<NCardHolder>().Where(LiveVisible).Where(h => h.CardModel != null && h.GetType().Name != "NPreviewCardHolder").ToArray();
        if (!preview)
        {
            foreach (var bundle in Walk(scene).OfType<NCardBundle>().Where(LiveVisible).Where(b => Enabled(b.Hitbox)))
                Add(bundle, "选择卡牌组合：" + string.Join("、", bundle.Bundle.Select(c => c.Title)), "select_bundle",
                    new { cards = bundle.Bundle.Select(SnapshotCard).ToArray() }, () => bundle.EmitSignal(NCardBundle.SignalName.Clicked, bundle));
            foreach (var holder in holders.Where(h => scene is not NChooseABundleSelectionScreen && Member(h, "_isClickable") is not false && h.Hitbox.IsEnabled))
            {
                var card = holder.CardModel!;
                bool isSelected = selected.Contains(card);
                Add(holder, (isSelected ? "取消选择：" : "选择卡牌：") + card.Title, "select_card", new { card = SnapshotCard(card), selected = isSelected }, () =>
                {
                    if (scene is NCardGridSelectionScreen && grid != null) grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
                    else holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                });
            }
        }
        foreach (var button in Walk(selectionScope).OfType<NClickableControl>().Where(Enabled))
        {
            string type = button.GetType().Name;
            if (type is "NConfirmButton" or "NChoiceSelectionSkipButton" or "NCardRewardAlternativeButton")
                Add(button, type == "NConfirmButton" ? (preview ? "确认当前选牌结果" : "确认已选卡牌") : ButtonText(button, type == "NChoiceSelectionSkipButton" ? "跳过选牌" : "选择替代奖励"), type == "NConfirmButton" ? "confirm" : "alternative", new { text = VisibleTexts(button), alternative = SnapshotModel(Member(button, "_alternative")) }, () => Click(button));
            else if (type == "NBackButton")
                Add(button, preview ? "返回选牌" : "取消本次选牌", "cancel_selection", null, () => Click(button));
        }
        return new
        {
            selectionScreen = scene.GetType().Name, selectionInstance = scene.GetInstanceId(),
            selectionSession = completion == null ? (int?)null : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(completion),
            prompt = Text(Member(prefs, "Prompt")), minSelect = Member(prefs, "MinSelect"), maxSelect = Member(prefs, "MaxSelect"),
            cancelable = Member(prefs, "Cancelable"), manualConfirmation = Member(prefs, "RequireManualConfirmation"), preview,
            selected = selected.Select(SnapshotCard).ToArray(),
            transformPreview = preview && scene is NDeckTransformSelectScreen ? "确认执行随机变化；轮播卡牌为动画示意" : null,
            text = preview && scene is NDeckTransformSelectScreen ? Array.Empty<string>() : VisibleTexts(selectionScope)
        };
    }

    private void AddProgressButtons(Node scene)
    {
        foreach (var button in Walk(scene).OfType<NClickableControl>().Where(Enabled))
            if (button.GetType().Name is "NProceedButton" or "NChoiceSelectionSkipButton")
                Add(button, ButtonText(button, "继续前进"), button.GetType().Name == "NProceedButton" ? "continue" : "skip", null, () => Click(button));
    }

    private string? crystalObservationKey;
    private ScryAnalysis? crystalAnalysis;
    private string? crystalToolSwitchKey;

    private object CaptureCrystalSphere(Node scene)
    {
        var entity = Member(scene, "_entity");
        bool finished = Member(entity, "IsFinished") is true;
        int remaining = Member(entity, "DivinationCount") is int count ? count : 0;
        string current = Text(Member(entity, "CrystalSphereTool"));
        var cells = Walk(scene).OfType<NCrystalSphereCell>().Where(LiveVisible).OrderBy(c => c.Entity.Y).ThenBy(c => c.Entity.X).ToArray();
        // Read Item only after its cell is exposed. Never read item.Position or the hidden cells' Item.
        // Intrinsic icon size distinguishes small/large gold and common/rare potion artwork.
        var visibleIds = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        var observation = cells.Select(c =>
        {
            if (c.Entity.IsHidden) return new ScryCell(c.Entity.X, c.Entity.Y, true, null, null);
            var item = c.Entity.Item;
            if (item == null) return new ScryCell(c.Entity.X, c.Entity.Y, false, null, null);
            if (!visibleIds.TryGetValue(item, out string? id))
                visibleIds[item] = id = "visible-" + c.Entity.X + "-" + c.Entity.Y;
            string kind = item.GetType().Name;
            if (kind == "CrystalSphereGold" && item.Size.X == 2) kind += "Large";
            if (kind == "CrystalSpherePotion" && item.Size.X == 2) kind += "Rare";
            return new ScryCell(c.Entity.X, c.Entity.Y, false, id, kind);
        }).ToArray();
        string observationKey = scene.GetInstanceId() + ":" + remaining + ":" + JsonSerializer.Serialize(observation);
        if (crystalAnalysis == null || crystalObservationKey != observationKey)
        {
            crystalAnalysis = CrystalSpherePlanner.Analyze(observation,
                cells.Length == 0 ? 0 : cells.Max(c => c.Entity.X) + 1,
                cells.Length == 0 ? 0 : cells.Max(c => c.Entity.Y) + 1, remaining);
            crystalObservationKey = observationKey;
        }
        var analysis = crystalAnalysis;
        var candidates = analysis.candidates.ToDictionary(c => (c.x, c.y, c.tool));
        var legalCells = cells.Where(c => Enabled(c) && c.Entity.IsHidden).ToArray();
        if (!finished)
        {
            foreach (var cell in legalCells)
            {
                candidates.TryGetValue((cell.Entity.X, cell.Entity.Y, current), out var evaluation);
                Add(cell, $"水晶球占卜格子（{cell.Entity.X}, {cell.Entity.Y}）", "scry_cell",
                    new { x = cell.Entity.X, y = cell.Entity.Y, newCells = evaluation?.newCells }, () => Click(cell));
            }
            foreach (var tool in Walk(scene).OfType<NClickableControl>().Where(n => Enabled(n) && n.GetType().Name == "NDivinationButton" && crystalToolSwitchKey != observationKey))
            {
                bool big = tool.Name.ToString().Contains("Big");
                if ((big && current == "Big") || (!big && current == "Small")) continue;
                Add(tool, big ? "切换大范围占卜工具" : "切换单格占卜工具", "scry_tool", new { tool = big ? "Big" : "Small", cost = 0 }, () => { Click(tool); crystalToolSwitchKey = observationKey; });
            }
        }
        AddProgressButtons(scene);
        var legalCoordinates = legalCells.Select(c => (c.Entity.X, c.Entity.Y)).ToHashSet();
        var best = finished ? null : analysis.candidates.FirstOrDefault(c => legalCoordinates.Contains((c.x, c.y)) && (c.tool == current || crystalToolSwitchKey != observationKey));
        string? recommendation = null;
        if (best != null)
        {
            if (best.tool == current)
                recommendation = "scry_cell:" + legalCells.First(c => c.Entity.X == best.x && c.Entity.Y == best.y).GetInstanceId();
            else
                recommendation = actions.FirstOrDefault(a => a.Kind == "scry_tool"
                    && JsonSerializer.SerializeToElement(a.Details).GetProperty("tool").GetString() == best.tool)?.Id;
        }
        return new
        {
            text = VisibleTexts(scene), remaining, tool = current, finished,
            rules = "每次点击消耗 1 次占卜。Big 揭开中心及相邻的 3×3 格；Small 揭开 1 格；切换免费。物品占满固定矩形，全部揭开才领取；诅咒 2×2 全揭开获得疑虑。每次揭开后依据新的局部图案重新选择。候选概率是与已露格兼容的位置均匀估计；未知格保留探索风险。",
            cells = observation, visibleItems = analysis.items,
            recommendedActionId = recommendation,
            recommendationIsCertain = best?.certain == true && recommendation != null,
            recommendation = best,
            alternatives = analysis.candidates.Take(8).ToArray()
        };
    }

    private void AddPotionDiscardActions(Node scene, Player player)
    {
        if (player.HasOpenPotionSlots) return;
        for (int i = 0; i < player.PotionSlots.Count; i++)
        {
            var potion = player.PotionSlots[i];
            if (potion == null) continue;
            Add(scene, "腾出药水栏：丢弃 " + ModelTitle(potion), "discard_potion_" + i, SnapshotModel(potion), () => nativeTasks.Add(PotionCmd.Discard(potion)));
        }
    }

    private void AddNativeFallback(Node scene)
    {
        foreach (var button in Walk(scene).OfType<NClickableControl>().Where(Enabled))
        {
            if (IsBlocked(button.GetType().Name) || IsBlocked(button.Name.ToString())) continue;
            string nodeSuffix = ":" + button.GetInstanceId();
            if (dispatch.Keys.Any(id => id.EndsWith(nodeSuffix, StringComparison.Ordinal))) continue;
            var text = ButtonText(button, "");
            if (text.Length == 0 || text.Length > 1200 || IsBlocked(text)) continue;
            // Only the active gameplay subtree is traversed. Top-bar menus and the autopilot panel are outside this subtree.
            Add(button, text, "native_ui", new { nativeType = button.GetType().Name, text = VisibleTexts(button) }, () => Click(button));
        }
    }

    private void Add(Node node, string label, string kind, object? details, Action execute)
    {
        string id = kind + ":" + node.GetInstanceId();
        if (dispatch.ContainsKey(id)) return;
        actions.Add(new LegalAction(id, label, kind, details));
        dispatch[id] = execute;
    }

    private static void Click(NClickableControl button)
    {
        if (!Enabled(button)) throw new InvalidOperationException("请等待按钮进入可操作状态。");
        // Native controller semantic input invokes both virtual OnRelease and Released signals, exactly once.
        using var press = new InputEventAction { Action = MegaInput.select, Pressed = true };
        using var release = new InputEventAction { Action = MegaInput.select, Pressed = false };
        button._GuiInput(press);
        button._GuiInput(release);
    }
}
