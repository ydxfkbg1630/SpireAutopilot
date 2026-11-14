using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpireAutopilot;

/// <summary>Pure, native-snapshot route validation. A route contains the entire remaining path, never future UI IDs.</summary>
public sealed class RoutePlanner
{
    private readonly string? savePath;
    private SavedRoute? saved;
    private string key = "";
    private string mapHash = "";
    private JsonArray? map;
    private Dictionary<RouteStep, JsonObject> nodes = new();
    private RouteStep? current;
    private int hp;
    private int maxHp;
    private readonly Dictionary<string, RouteStep[]> options = new(StringComparer.Ordinal);
    public bool PlanningRequired { get; private set; }
    public string PlanningTrigger { get; private set; } = "首次读取整幕地图";
    public string Summary { get; private set; } = "进入地图后规划本幕完整路线。";
    public object? MapContext => map;
    public IReadOnlyList<RouteStep> Remaining { get; private set; } = Array.Empty<RouteStep>();
    public object[] RouteOptions { get; private set; } = Array.Empty<object>();

    public RoutePlanner(string? savePath = null)
    {
        this.savePath = savePath;
        if (savePath == null || !File.Exists(savePath)) return;
        try { saved = JsonSerializer.Deserialize<SavedRoute>(File.ReadAllText(savePath)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public void Observe(DecisionFrame frame, string strategy)
    {
        JsonObject state = JsonSerializer.SerializeToNode(frame.State)!.AsObject();
        string nextKey = (state["runKey"]?.ToString() ?? "legacy") + ":" + state["CurrentActIndex"] + ":" + Hash(strategy);
        if (key != nextKey) { key = nextKey; map = null; mapHash = ""; nodes.Clear(); Remaining = Array.Empty<RouteStep>(); Summary = "进入地图后规划本幕完整路线。"; }
        hp = state["player"]?["hp"]?.GetValue<int>() ?? 0;
        maxHp = state["player"]?["maxHp"]?.GetValue<int>() ?? 1;
        current = ReadStep(state["currentMapPoint"]);
        PlanningRequired = false;
        if (frame.Scene != "NMapScreen" || state["context"]?["map"] is not JsonArray actualMap) return;
        var actualNodes = actualMap.OfType<JsonObject>().ToDictionary(n => ReadStep(n)!, n => n);
        string topology = Hash(JsonSerializer.Serialize(actualNodes.OrderBy(n => n.Key.Row).ThenBy(n => n.Key.Column)
            .Select(n => new { n.Key, children = n.Value["children"], type = n.Value["type"], quests = n.Value["quests"] })));
        if (mapHash != topology)
        {
            mapHash = topology;
            map = (JsonArray)actualMap.DeepClone();
            nodes = actualNodes;
        }
        if (saved?.Key != key || saved.MapHash != mapHash)
        { Require("首次进入本幕或地图连接发生变化", frame); return; }
        int index = current == null ? -1 : Array.FindIndex(saved.Route, p => p == current);
        Remaining = index < 0 ? saved.Route : saved.Route.Skip(index + 1).ToArray();
        if (Remaining.Count == 0 || FindTravel(frame, Remaining[0]) == null)
        { Require("当前位置与原路线衔接发生变化", frame); return; }
        try { ValidatePath(frame, Remaining); }
        catch (ModelProtocolException) { Require("原路线连接发生变化", frame); return; }
        // Replan once per substantial health drop, rather than repeating a request at every map node.
        if (hp <= maxHp * .35 && saved.PlannedHp - hp >= Math.Max(10, maxHp * .2))
        { Require("生命降至35%以下且较规划时明显减少，重新评估休息路线", frame); return; }
        Summary = Format(Remaining, saved.Reason);
    }

    public LegalAction? PlannedTravel(DecisionFrame frame)
        => frame.Scene == "NMapScreen" && !PlanningRequired && Remaining.Count > 0 ? FindTravel(frame, Remaining[0]) : null;

    public void Accept(DecisionFrame frame, ModelDecision decision)
    {
        if (!PlanningRequired || frame.Scene != "NMapScreen") return;
        IReadOnlyList<RouteStep>? route = decision.RoutePlan;
        if (decision.RouteOptionId != null)
        {
            if (!options.TryGetValue(decision.RouteOptionId, out var selected)) throw new ModelProtocolException("routeOptionId应来自当前路线候选。");
            route = selected;
        }
        if (route is not { Count: > 0 })
            throw new ModelProtocolException("地图规划应包含通往幕末Boss的完整routePlan坐标。");
        ValidatePath(frame, route);
        if (FindTravel(frame, route[0])?.Id != decision.ActionId)
            throw new ModelProtocolException("routePlan首个节点应与本次actionId对应。");
        saved = new SavedRoute(key, mapHash, route.ToArray(), decision.PlanReason ?? decision.Reason, hp);
        Remaining = saved.Route;
        Summary = Format(Remaining, saved.Reason);
        PlanningRequired = false;
        if (savePath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(savePath))!);
            string temporary = savePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved));
            File.Move(temporary, savePath, true);
        }
    }

    private void ValidatePath(DecisionFrame frame, IReadOnlyList<RouteStep> route)
    {
        if (route.Count is 0 or > 100 || FindTravel(frame, route[0]) == null)
            throw new ModelProtocolException("路线首步应为当前合法地图节点。");
        var seen = new HashSet<RouteStep>();
        for (int i = 0; i < route.Count; i++)
        {
            if (!nodes.TryGetValue(route[i], out var point) || !seen.Add(route[i]))
                throw new ModelProtocolException("路线应使用地图中的有效且互异的节点。");
            var children = (point["children"] as JsonArray)?.Select(ReadStep).Where(p => p != null).Cast<RouteStep>().ToArray() ?? Array.Empty<RouteStep>();
            if (i + 1 < route.Count && !children.Contains(route[i + 1]))
                throw new ModelProtocolException("路线中相邻节点应有原生地图连线。");
            if (i == route.Count - 1 && children.Length > 0)
                throw new ModelProtocolException("路线应规划至本幕末端，请补全剩余节点。");
            if (i == route.Count - 1 && !point["type"]!.ToString().Contains("Boss", StringComparison.OrdinalIgnoreCase)
                && route[i].Row < nodes.Keys.Max(p => p.Row))
                throw new ModelProtocolException("路线终点应为本幕Boss或最末层，请选择通往幕末的分支。");
        }
    }

    private static LegalAction? FindTravel(DecisionFrame frame, RouteStep step) => frame.Actions.FirstOrDefault(a => a.Kind == "travel" && ReadStep(JsonSerializer.SerializeToNode(a.Details)) == step);
    private static RouteStep? ReadStep(JsonNode? node) => node is JsonObject obj && obj["row"] is JsonValue row && obj["column"] is JsonValue column
        ? new RouteStep(row.GetValue<int>(), column.GetValue<int>()) : null;
    private void Require(string reason, DecisionFrame frame)
    {
        PlanningRequired = true; PlanningTrigger = reason; Remaining = Array.Empty<RouteStep>(); Summary = "正在规划整幕路线：" + reason;
        BuildOptions(frame);
    }
    private void BuildOptions(DecisionFrame frame)
    {
        options.Clear();
        var frontier = frame.Actions.Where(a => a.Kind == "travel").Select(a => ReadStep(JsonSerializer.SerializeToNode(a.Details)))
            .Where(p => p != null).Select(p => new[] { p! }).ToList();
        var completed = new List<RouteStep[]>();
        // Bounded native graph enumeration preserves varied branches without a model inventing edges.
        for (int depth = 0; depth < 100 && frontier.Count > 0; depth++)
        {
            var next = new List<RouteStep[]>();
            foreach (var path in frontier)
            {
                if (!nodes.TryGetValue(path[^1], out var point)) continue;
                var children = (point["children"] as JsonArray)?.Select(ReadStep).Where(p => p != null).Cast<RouteStep>()
                    .OrderBy(p => p.Row).ThenBy(p => p.Column).ToArray() ?? Array.Empty<RouteStep>();
                if (children.Length == 0)
                {
                    if (point["type"]?.ToString().Contains("Boss", StringComparison.OrdinalIgnoreCase) == true || path[^1].Row == nodes.Keys.Max(p => p.Row)) completed.Add(path);
                }
                else foreach (var child in children.Where(p => !path.Contains(p))) next.Add(path.Append(child).ToArray());
            }
            frontier = Spread(next, 128);
        }
        var candidates = Spread(completed, 10);
        var selected = candidates.Concat(completed.OrderBy(p => CountType(p, "Elite")).Take(2))
            .Concat(completed.OrderByDescending(p => CountType(p, "Elite")).Take(2))
            .Concat(completed.OrderByDescending(p => CountType(p, "RestSite")).Take(2))
            .Concat(completed.OrderByDescending(p => CountType(p, "Shop")).Take(2))
            .DistinctBy(p => string.Join(';', p.Select(s => $"{s.Row},{s.Column}"))).Take(18).ToArray();
        RouteOptions = selected.Select((path, i) =>
        {
            string id = "route_" + (i + 1).ToString("D2");
            options[id] = path;
            return (object)new { id, firstActionId = FindTravel(frame, path[0])!.Id, path = path.Select(p => new[] { p.Row, p.Column }).ToArray(),
                elites = CountType(path, "Elite"), restSites = CountType(path, "RestSite"), shops = CountType(path, "Shop") };
        }).ToArray();
    }
    private int CountType(RouteStep[] path, string type) => path.Count(p => nodes[p]["type"]?.ToString().Contains(type, StringComparison.OrdinalIgnoreCase) == true);
    private static List<T> Spread<T>(List<T> input, int maximum) => input.Count <= maximum ? input : Enumerable.Range(0, maximum)
        .Select(i => input[(int)((long)i * (input.Count - 1) / (maximum - 1))]).ToList();
    private string Format(IEnumerable<RouteStep> route, string reason) => string.Join(" → ", route.Select(p => $"{p.Row}层/{p.Column}列 {nodes.GetValueOrDefault(p)?["type"]}")) + "\n规划：" + reason;
    private static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    public sealed record SavedRoute(string Key, string MapHash, RouteStep[] Route, string Reason, int PlannedHp);
}
