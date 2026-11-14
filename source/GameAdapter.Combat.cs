using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;

namespace SpireAutopilot;

public sealed partial class GameAdapter
{
    public string? CombatIdentity
    {
        get
        {
            var state = CombatManager.Instance.DebugOnlyGetState();
            return state == null ? null : "combat:" + RuntimeHelpers.GetHashCode(state) + ":" + state.RunState.CurrentActIndex + ":" + state.RunState.TotalFloor;
        }
    }

    public object? CaptureCombatObservation()
    {
        var combat = CombatManager.Instance.DebugOnlyGetState();
        var player = combat == null ? null : LocalContext.GetMe(combat);
        if (combat == null || player?.PlayerCombatState is not { } pcs) return null;
        return new
        {
            side = combat.CurrentSide.ToString(), turn = pcs.TurnNumber, phase = pcs.Phase.ToString(), energy = pcs.Energy, stars = pcs.Stars,
            creature = CombatCreature(player.Creature),
            hand = pcs.Hand.Cards.Select(ObservationCard).ToArray(),
            drawPile = new { orderKnown = false, cards = pcs.DrawPile.Cards.Select(ObservationCard).OrderBy(c => JsonSerializer.Serialize(c), StringComparer.Ordinal).ToArray() },
            discardPile = pcs.DiscardPile.Cards.Select(ObservationCard).ToArray(), exhaustPile = pcs.ExhaustPile.Cards.Select(ObservationCard).ToArray(),
            relics = player.Relics.Select(SnapshotModel).ToArray(),
            enemies = combat.Enemies.Select(enemy => new { creature = CombatCreature(enemy), intents = enemy.Monster?.NextMove.Intents.Select(intent => CombatIntent(intent, enemy, player)).ToArray() }).ToArray()
        };
    }

    public DecisionFrame? CaptureCombatFrame()
    {
        dispatch.Clear(); actions.Clear();
        foreach (var task in nativeTasks.Where(t => t.IsCompleted).ToArray())
        {
            nativeTasks.Remove(task);
            if (task.IsFaulted) throw new InvalidOperationException("原生战斗动作反馈：" + task.Exception!.GetBaseException().Message);
        }
        var combat = CombatManager.Instance.DebugOnlyGetState();
        if (!InCombat || combat == null || combat.Players.Count != 1 || RunManager.Instance.IsPaused || CombatManager.Instance.IsPaused)
        { Status = "等待单人战斗操作阶段。"; return null; }
        var player = LocalContext.GetMe(combat);
        if (player?.PlayerCombatState == null || player.Creature.IsDead) { Status = "等待角色战斗状态。"; return null; }
        Node? screen = ActiveScreenContext.Instance.GetCurrentScreen() as Node;
        // Selection screens deliberately precede executor-idle checks: the running action awaits this choice.
        string screenType = screen?.GetType().Name ?? "";
        bool selection = screenType is "NCardRewardSelectionScreen" or "NCardGridSelectionScreen" or "NChooseACardSelectionScreen" or "NChooseABundleSelectionScreen" or "NDeckCardSelectScreen" or "NSimpleCardSelectScreen" or "NDeckUpgradeSelectScreen";
        object? choice = null;
        if (selection && screen != null && LiveVisible(screen)) choice = CaptureCards(screen);
        else
        {
            if (screen != null && (IsBlocked(screenType) || screenType.Contains("Selection", StringComparison.Ordinal) || screenType != "NCombatRoom"))
            { Status = "等待返回战斗或原生选牌界面。"; return null; }
            var executor = RunManager.Instance.ActionExecutor;
            if (player.PlayerCombatState.Phase != PlayerTurnPhase.Play || combat.CurrentSide != CombatSide.Player
                || CombatManager.Instance.PlayerActionsDisabled || executor.CurrentlyRunningAction != null || !executor.FinishedExecutingActions().IsCompleted)
            { Status = "等待战斗动画、队列或敌方回合完成。"; return null; }
            var targets = combat.Enemies.Cast<Creature>().Concat(new[] { player.Creature }).Where(c => c.IsAlive).ToArray();
            var hand = player.PlayerCombatState.Hand.Cards.ToArray();
            for (int index = 0; index < hand.Length; index++)
            {
                var card = hand[index];
                foreach (var target in new Creature?[] { null }.Concat(targets))
                {
                    if (!card.CanPlayTargeting(target)) continue;
                    var localTarget = target;
                    string id = "play:" + index + ":" + TargetId(target);
                    CombatAdd(id, "打出" + card.Title + TargetLabel(target), "play_card", new { handIndex = index, card = CombatCard(card), targetId = TargetId(target) }, () =>
                    {
                        if (!card.TryManualPlay(localTarget)) throw new InvalidOperationException("卡牌当前合法状态已变化，请重新读取战斗。");
                    });
                }
            }
            if (!RunManager.Instance.IsPotionUseLocked)
            for (int slot = 0; slot < player.PotionSlots.Count; slot++)
            {
                var potion = player.PotionSlots[slot];
                if (potion == null || potion.IsQueued || potion.HasBeenRemovedFromState || potion.Usage.ToString() is "Automatic" or "None") continue;
                var potionTargets = potion.TargetType.ToString() == "Self" ? new Creature?[] { player.Creature } : new Creature?[] { null }.Concat(targets);
                foreach (var target in potionTargets)
                {
                    if (!potion.IsValidTarget(target)) continue;
                    var localTarget = target;
                    CombatAdd("potion:" + slot + ":" + TargetId(target), "使用" + ModelTitle(potion) + TargetLabel(target), "use_potion", new { slot, potion = SnapshotModel(potion), targetId = TargetId(target) }, () => potion.EnqueueManualUse(localTarget));
                }
            }
            CombatAdd("end_turn", "结束当前回合", "end_turn", new { turn = player.PlayerCombatState.TurnNumber }, () =>
            {
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, player.PlayerCombatState.TurnNumber));
            });
        }
        if (actions.Count == 0) { Status = "等待原生可用战斗动作。"; return null; }
        object state = new { observation = CombatSnapshot(combat, player), choice, combatIdentity = CombatIdentity,
            runKey = CombatRunKey(combat), combat.RunState.CurrentActIndex, combat.RunState.TotalFloor,
            player = SnapshotPlayer(player), currentMapPoint = combat.RunState.CurrentMapCoord is { } coordinate ? new { row = coordinate.row, column = coordinate.col } : null,
            actMap = SnapshotActMap(combat.RunState), context = new { combat = true } };
        var stable = JsonSerializer.Serialize(new { state, actions });
        string token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable)));
        Status = "可决策：" + (selection ? screenType : "Combat");
        return new DecisionFrame(token, selection ? screenType : "Combat", state, actions.ToArray());
    }

    public Task ExecuteCombatAsync(string token, string actionId)
    {
        var fresh = CaptureCombatFrame();
        if (fresh == null || fresh.Token != token || !dispatch.TryGetValue(actionId, out var execute))
            throw new InvalidOperationException("战斗局面已更新，请依据当前合法动作重新决策。");
        execute(); return Task.CompletedTask;
    }

    private void CombatAdd(string id, string label, string kind, object? details, Action action)
    { if (dispatch.ContainsKey(id)) return; actions.Add(new LegalAction(id, label, kind, details)); dispatch.Add(id, action); }
    private static string TargetId(Creature? creature) => creature?.CombatId?.ToString() ?? "none";
    private static string TargetLabel(Creature? creature) => creature == null ? "" : " → " + creature.Name;
    private static string CombatRunKey(CombatState state) => state.RunState.Rng.StringSeed + ":" + LocalContext.GetMe(state)?.Character.Id + ":" + state.RunState.AscensionLevel;
    private static object CombatCard(CardModel card) => new
    {
        definition = SnapshotCard(card), combatCardId = Text(Member(card, "CombatCardId")),
        description = card.GetDescriptionForPile(PileType.Hand), energyToSpend = card.EnergyCost.GetAmountToSpend(), starCost = card.GetStarCostWithModifiers(),
        targetType = card.TargetType.ToString(), canPlay = card.CanPlay(out var reason, out var preventer), unplayableReason = reason.ToString(), preventedBy = preventer?.Id.ToString(),
        isAutoplay = Member(card, "IsAutoplay"), currentDynamicVars = SnapshotDynamicVars(card.DynamicVars)
    };
    private static object ObservationCard(CardModel card) => new { id = card.Id.ToString(), name = card.Title, cost = card.EnergyCost.GetResolved(), description = card.GetDescriptionForPile(PileType.Hand), type = card.Type.ToString(), dynamicVars = SnapshotDynamicVars(card.DynamicVars) };
    private static object CombatSnapshot(CombatState combat, Player player)
    {
        var pcs = player.PlayerCombatState!;
        return new
        {
            side = combat.CurrentSide.ToString(), turn = pcs.TurnNumber, phase = pcs.Phase.ToString(), energy = pcs.Energy, stars = pcs.Stars,
            player = SnapshotPlayer(player), creature = CombatCreature(player.Creature),
            hand = pcs.Hand.Cards.Select(CombatCard).ToArray(),
            drawPile = new { orderKnown = false, information = "显示牌堆成员，按卡牌内容整理；实际抽取顺序保持隐藏。", cards = pcs.DrawPile.Cards.Select(CombatCard).OrderBy(c => JsonSerializer.Serialize(c), StringComparer.Ordinal).ToArray() },
            discardPile = pcs.DiscardPile.Cards.Select(CombatCard).ToArray(), exhaustPile = pcs.ExhaustPile.Cards.Select(CombatCard).ToArray(),
            orbs = Items(Member(pcs.OrbQueue, "Orbs")).Select(SnapshotModel).ToArray(), orbCapacity = Member(pcs.OrbQueue, "Capacity"),
            enemies = combat.Enemies.Select(enemy => new { creature = CombatCreature(enemy), nextMove = Text(Member(enemy.Monster, "NextMove") is { } move ? Member(move, "Id") : null), intents = enemy.Monster?.NextMove.Intents.Select(intent => CombatIntent(intent, enemy, player)).ToArray() }).ToArray()
        };
    }
    private static object CombatCreature(Creature creature) => new { id = creature.CombatId, name = creature.Name, hp = creature.CurrentHp, maxHp = creature.MaxHp, block = Member(creature, "Block"), creature.IsAlive, powers = Items(Member(creature, "Powers")).Select(SnapshotModel).ToArray() };
    private static object CombatIntent(AbstractIntent intent, Creature enemy, Player player)
    {
        var targets = new[] { player.Creature };
        return new { type = intent.IntentType.ToString(), label = Text(intent.GetIntentLabel(targets, enemy)),
            damagePerHit = intent is AttackIntent attack ? (int?)attack.GetSingleDamage(targets, enemy) : null,
            totalDamage = intent is AttackIntent attackTotal ? (int?)attackTotal.GetTotalDamage(targets, enemy) : null,
            repeats = intent is AttackIntent repeated ? (int?)repeated.Repeats : null };
    }
}
