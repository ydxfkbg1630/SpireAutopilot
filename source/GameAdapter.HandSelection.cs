using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace SpireAutopilot;

public sealed partial class GameAdapter
{
    /// <summary>The native hand remains inside NCombatRoom while a card action awaits its selection.</summary>
    private object CaptureHandSelection(NPlayerHand hand)
    {
        object? pending = Member(hand, "_selectionCompletionSource");
        object? prefs = Member(hand, "_prefs");
        var selected = Items(Member(hand, "_selectedCards")).OfType<CardModel>().ToArray();
        var filter = Member(hand, "_currentSelectionFilter") as Func<CardModel, bool>;
        bool peeking = hand.PeekButton?.IsPeeking == true;
        bool pendingCompleted = Member(pending, "Task") is Task task && task.IsCompleted;
        bool interactive = hand.IsInCardSelection && pending != null && !pendingCompleted && !peeking;
        var candidates = hand.ActiveHolders.Where(LiveVisible)
            .Where(h => h.CardModel != null && (filter?.Invoke(h.CardModel) ?? true))
            .Where(h => Member(h, "_isClickable") is not false && h.Hitbox.IsEnabled).ToArray();
        if (interactive)
        {
            foreach (var holder in candidates)
            {
                var local = holder;
                Add(local, "选择手牌：" + local.CardModel!.Title, "select_card", new
                {
                    card = HandChoiceCard(local.CardModel), selected = false,
                    purpose = hand.CurrentMode.ToString(),
                }, () => local.EmitSignal(NCardHolder.SignalName.Pressed, local));
            }
            // Selected holders have their own native deselect callback. It removes the old
            // selected node and restores a hand holder, preserving native layout and indexes.
            if (hand.CurrentMode == NPlayerHand.Mode.SimpleSelect
                && Member(hand, "_selectedHandCardContainer") is NSelectedHandCardContainer container)
            {
                foreach (var holder in container.Holders.Where(LiveVisible)
                    .Where(h => h.CardModel != null && selected.Contains(h.CardModel)))
                {
                    var local = holder;
                    Add(local, "取消已选手牌：" + local.CardModel!.Title, "deselect_card", new
                    { card = HandChoiceCard(local.CardModel), selected = true },
                        () => local.EmitSignal(NCardHolder.SignalName.Pressed, local));
                }
            }
            if (Member(hand, "_selectModeConfirmButton") is NClickableControl confirm && Enabled(confirm))
                Add(confirm, "确认已选手牌", "confirm", new { selectedCount = selected.Length }, () => Click(confirm));
        }
        else if (peeking) Status = "等待收起牌堆窥视后继续选择手牌。";
        else Status = "等待手牌选择进入操作阶段。";

        var currentAction = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        return new
        {
            handNode = hand.GetInstanceId(),
            selectionSession = pending == null ? (int?)null : RuntimeHelpers.GetHashCode(pending),
            mode = hand.CurrentMode.ToString(), isSelecting = hand.IsInCardSelection,
            peeking, pendingCompleted,
            prompt = Text(Member(prefs, "Prompt")),
            minSelect = Member(prefs, "MinSelect"), maxSelect = Member(prefs, "MaxSelect"),
            manualConfirmation = Member(prefs, "RequireManualConfirmation"), cancelable = Member(prefs, "Cancelable"),
            selected = selected.Select(HandChoiceCard).ToArray(),
            candidates = candidates.Select(h => HandChoiceCard(h.CardModel!)).ToArray(),
            sourceAction = currentAction == null ? null : new
            {
                type = currentAction.GetType().Name,
                card = SnapshotModel(Member(currentAction, "Card") ?? Member(currentAction, "CardModel")),
                source = SnapshotModel(Member(currentAction, "Source")),
            },
        };
    }

    private static object HandChoiceCard(CardModel card) => new
    {
        combatCardId = Text(Member(card, "CombatCardId")),
        cardIdentity = RuntimeHelpers.GetHashCode(card),
        card = SnapshotCard(card),
    };
}
