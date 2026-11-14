using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace SpireAutopilot;

public sealed partial class GameAdapter
{
    private static bool LiveVisible(Node n) => GodotObject.IsInstanceValid(n) && n.IsInsideTree() && !n.IsQueuedForDeletion() && (n is not CanvasItem c || c.IsVisibleInTree());
    private static bool Enabled(NClickableControl n) => LiveVisible(n) && n.IsEnabled;

    private static IEnumerable<Node> Walk(Node root)
    {
        if (!GodotObject.IsInstanceValid(root)) yield break;
        yield return root;
        foreach (var child in root.GetChildren())
            foreach (var descendant in Walk(child)) yield return descendant;
    }

    private static object? Member(object? source, string name)
    {
        if (source == null) return null;
        for (Type? type = source.GetType(); type != null; type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            try
            {
                var p = type.GetProperty(name, flags);
                if (p is { CanRead: true } && p.GetIndexParameters().Length == 0) return p.GetValue(source);
                var f = type.GetField(name, flags);
                if (f != null) return f.GetValue(source);
            }
            catch (TargetInvocationException) { return null; }
        }
        return null;
    }

    private static IEnumerable<object> Items(object? source)
    {
        if (source is IEnumerable collection)
            foreach (object? item in collection)
                if (item != null) yield return item;
    }

    private static string Text(object? value)
    {
        if (value == null) return "";
        if (value is string s) return s;
        var method = value.GetType().GetMethod("GetFormattedText", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (method != null)
        {
            try { return method.Invoke(value, null)?.ToString() ?? ""; }
            catch (TargetInvocationException) { }
        }
        return value.ToString() ?? "";
    }

    private static string[] VisibleTexts(Node root) => Walk(root).Where(LiveVisible)
        .Select(n => n is Label l ? l.Text : n is RichTextLabel r ? r.Text : n is Button b ? b.Text : "")
        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Take(100).ToArray();

    private static string ButtonText(Node node, string fallback)
    {
        string text = string.Join(" ", VisibleTexts(node));
        return text.Length == 0 ? fallback : text;
    }

    private static bool IsBlocked(string name)
    {
        string[] fragments = { "Abandon", "Settings", "Quit", "ExitGame", "MainMenu", "Profile", "DeleteSave", "RunHistory", "DeckView", "CardLibrary", "RelicCollection", "Inspect", "Feedback", "BugReport", "ErrorPopup", "SpireAutopilot", "放弃", "设置", "退出", "主菜单", "删除存档" };
        return fragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static object MapInfo(MapPoint p) => new
    {
        row = p.coord.row, column = p.coord.col, type = p.PointType.ToString(),
        children = p.Children.OrderBy(c => c.coord.row).ThenBy(c => c.coord.col).Select(c => new { row = c.coord.row, column = c.coord.col }).ToArray(),
        quests = p.Quests.Select(q => new { id = q.Id.ToString(), name = ModelTitle(q) }).ToArray()
    };

    private static object SnapshotPlayer(Player player) => new
    {
        character = SnapshotModel(player.Character), hp = player.Creature.CurrentHp, maxHp = player.Creature.MaxHp, player.Gold,
        block = player.Creature.Block, powers = player.Creature.Powers.Select(SnapshotModel).ToArray(),
        energy = player.PlayerCombatState?.Energy, stars = player.PlayerCombatState?.Stars,
        turn = player.PlayerCombatState?.TurnNumber, phase = player.PlayerCombatState?.Phase.ToString(),
        deck = player.Deck.Cards.Select(SnapshotCard).ToArray(), relics = player.Relics.Select(SnapshotModel).ToArray(),
        potions = player.PotionSlots.Select(SnapshotModel).ToArray(), player.MaxPotionCount, player.MaxEnergy
    };

    private static string ModelTitle(object? model) => model is CardModel card ? card.Title : Text(Member(model, "Title")) is { Length: > 0 } title ? title : Text(Member(model, "Id"));

    private static object? SnapshotModel(object? model)
    {
        if (model == null) return null;
        if (model is CardModel card) return SnapshotCard(card);
        return new
        {
            type = model.GetType().Name, id = Text(Member(model, "Id")), name = ModelTitle(model),
            description = model is RelicModel relic ? relic.HoverTip.Description : model is PotionModel potion ? potion.HoverTip.Description
                : model is PowerModel power ? power.GetDumbHoverTip().Description : Text(Member(model, "DynamicDescription") ?? Member(model, "Description")), rarity = Text(Member(model, "Rarity")),
            amount = Member(model, "Amount")?.ToString(), counter = Member(model, "DisplayAmount")?.ToString(),
            // Dynamic amounts support relic counters and mod model data without serializing native object graphs.
            dynamicVars = SnapshotDynamicVars(Member(model, "DynamicVars"))
        };
    }

    private static object SnapshotCard(CardModel card)
    {
        string description;
        try { description = card.GetDescriptionForPile(PileType.Deck); }
        catch { description = Text(card.Description); }
        string? upgrade = null;
        int? upgradeCost = null;
        string? upgradeName = null;
        string? upgradePreviewError = null;
        if (card.IsUpgradable)
        {
            try
            {
                // GetDescriptionForUpgradePreview only changes formatting; OnUpgrade supplies the new values.
                // Use a detached native clone, unlike CardScope.CloneCard/CreateClone, which register in a run/combat.
                CardModel preview = card.IsMutable ? (CardModel)card.ClonePreservingMutability() : card.ToMutable();
                preview.UpgradeInternal();
                upgrade = preview.GetDescriptionForPile(PileType.Deck);
                upgradeCost = preview.EnergyCost.GetResolved();
                upgradeName = preview.Title;
            }
            catch (Exception ex) { upgradePreviewError = ex.GetType().Name; }
        }
        return new
        {
            id = card.Id.ToString(), name = card.Title, description, upgradeDescription = upgrade,
            upgradeCost, upgradeName, canUpgrade = card.IsUpgradable, upgradePreviewError,
            type = card.Type.ToString(), rarity = card.Rarity.ToString(), card.IsUpgraded,
            upgradeLevel = card.CurrentUpgradeLevel, cost = card.EnergyCost.GetResolved(), costsX = card.EnergyCost.CostsX,
            keywords = Items(Member(card, "Keywords")).Select(Text).ToArray(), dynamicVars = SnapshotDynamicVars(card.DynamicVars),
            relatedEffects = Items(Member(card, "HoverTips")).Select(t => new { name = Text(Member(t, "Title")), description = Text(Member(t, "Description")) }).ToArray(),
            enchantment = SnapshotSimpleModel(Member(card, "Enchantment")), affliction = SnapshotSimpleModel(Member(card, "Affliction"))
        };
    }

    private static object? SnapshotSimpleModel(object? model) => model == null ? null : new { id = Text(Member(model, "Id")), amount = Text(Member(model, "Amount")), name = ModelTitle(model) };

    private static object? MerchantModel(object entry) => Member(Member(entry, "CreationResult"), "Card") ?? Member(entry, "Model") ?? Member(entry, "Card") ?? Member(entry, "Relic") ?? Member(entry, "Potion") ?? (Member(entry, "Title") != null ? entry : null);

    private static object? SnapshotReward(object? reward) => reward == null ? null : new
    {
        type = reward.GetType().Name, description = Text(Member(reward, "Description")),
        item = SnapshotModel(Member(reward, "Relic") ?? Member(reward, "Potion") ?? Member(reward, "Card")),
        cards = Items(Member(reward, "Cards")).OfType<CardModel>().Select(SnapshotCard).ToArray(),
        amount = Member(reward, "Amount") is int amount ? (int?)amount : null
    };

    private static object[] SnapshotDynamicVars(object? vars) => Items(vars).Select(v => Member(v, "Value") ?? v).Select(v => (object)new
    {
        name = Text(Member(v, "Name")), value = Text(Member(v, "BaseValue")), current = Text(Member(v, "Value")),
        upgraded = Text(Member(v, "UpgradeValue"))
    }).ToArray();

    private static object[] SnapshotActMap(MegaCrit.Sts2.Core.Runs.IRunState run)
        => run.Map.GetAllMapPoints().Concat(new[] { run.Map.StartingMapPoint, run.Map.BossMapPoint, run.Map.SecondBossMapPoint }
            .OfType<MapPoint>()).Distinct().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).Select(MapInfo).ToArray();
}
