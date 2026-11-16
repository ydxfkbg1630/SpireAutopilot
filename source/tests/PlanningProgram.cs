using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAutopilot;

var failures = new List<string>(); int passed = 0;
void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
void Test(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
void Reject(Action body) { try { body(); } catch (ModelProtocolException) { return; } throw new Exception("Expected ModelProtocolException"); }
RouteStep[] Full() => new[] { new RouteStep(1, 0), new RouteStep(2, 0), new RouteStep(3, 0) };
JsonObject Card(string id = "CARD.STRIKE") => new() { ["id"] = id, ["name"] = "Strike", ["description"] = "Deal 6 damage", ["cost"] = 1, ["upgradeDescription"] = "Deal 9 damage", ["dynamicVars"] = new JsonObject { ["damage"] = 6 } };
DecisionFrame Frame(string run = "run-one", int act = 0, int hp = 70, int currentRow = 0, bool changed = false)
{
    var map = new JsonArray();
    for (int row = 0; row <= 3; row++) map.Add(new JsonObject { ["row"] = row, ["column"] = 0, ["type"] = row == 3 ? "Boss" : "Monster", ["children"] = row == 3 ? new JsonArray() : new JsonArray(new JsonObject { ["row"] = row + 1, ["column"] = changed && row == 1 ? 1 : 0 }) });
    map.Add(new JsonObject { ["row"] = 1, ["column"] = 1, ["type"] = "Elite", ["children"] = new JsonArray(new JsonObject { ["row"] = 2, ["column"] = 0 }) });
    var state = new JsonObject { ["runKey"] = run, ["CurrentActIndex"] = act, ["currentMapPoint"] = new JsonObject { ["row"] = currentRow, ["column"] = 0 }, ["player"] = new JsonObject { ["hp"] = hp, ["maxHp"] = 100, ["deck"] = new JsonArray(Card(), Card()) }, ["context"] = new JsonObject { ["map"] = map }, ["sceneInstance"] = 999, ["controls"] = new JsonArray() };
    var actions = new List<LegalAction> { new("travel-next", "Travel", "travel", new { row = currentRow + 1, column = 0 }) };
    if (currentRow == 0) actions.Add(new("travel-other", "Other", "travel", new { row = 1, column = 1 }));
    return new DecisionFrame("token-one", "NMapScreen", state, actions);
}
ModelDecision Plan(string id = "travel-next", RouteStep[]? route = null) => new(id, "safe", route ?? Full(), "Reach boss");

Test("complete DAG route accepted and adjacent layer reused", () => { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); Check(p.PlanningRequired, "first map requests plan"); p.Accept(f, Plan()); Check(!p.PlanningRequired && p.Remaining.Count == 3, "complete route accepted"); var next = Frame(currentRow: 1); p.Observe(next, "safe"); Check(!p.PlanningRequired && p.Remaining.Count == 2 && p.PlannedTravel(next)?.Id == "travel-next", "next layer bypasses model"); });
Test("first step must match chosen legal action ID", () => { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); Reject(() => p.Accept(f, Plan("travel-other"))); });
Test("supplied route option resolves complete path without routePlan", () => { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); var option = JsonSerializer.SerializeToNode(p.RouteOptions[0])!; var id = option["id"]!.ToString(); var actionId = option["firstActionId"]!.ToString(); p.Accept(f, new ModelDecision(actionId, "safe", PlanReason: "candidate route", RouteOptionId: id)); Check(!p.PlanningRequired && p.Remaining.Count == 3 && p.Remaining[^1] == new RouteStep(3, 0), "candidate resolves to native full path"); });
Test("unknown route option and mismatched first action are rejected", () => { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); Reject(() => p.Accept(f, new ModelDecision("travel-next", "safe", RouteOptionId: "route_unknown"))); var option = JsonSerializer.SerializeToNode(p.RouteOptions[0])!; var mismatched = option["firstActionId"]!.ToString() == "travel-next" ? "travel-other" : "travel-next"; Reject(() => p.Accept(f, new ModelDecision(mismatched, "safe", RouteOptionId: option["id"]!.ToString()))); Check(p.PlanningRequired, "rejected candidate retains planning requirement"); });
Test("premature nonBoss sink is rejected and excluded from options", () => { var p = new RoutePlanner(); var f = Frame(); var state = (JsonObject)f.State; var map = state["context"]!["map"]!.AsArray(); var sink = map.OfType<JsonObject>().Single(n => n["row"]!.GetValue<int>() == 1 && n["column"]!.GetValue<int>() == 0); sink["children"] = new JsonArray(); p.Observe(f, "safe"); Reject(() => p.Accept(f, Plan(route: new[] { new RouteStep(1, 0) }))); Check(p.RouteOptions.All(o => JsonSerializer.SerializeToNode(o)!["firstActionId"]!.ToString() != "travel-next"), "early sink excluded"); });
Test("disconnected route rejected", () => { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); Reject(() => p.Accept(f, Plan(route: new[] { new RouteStep(1, 0), new RouteStep(3, 0) }))); });
Test("premature nonterminal route rejected", () => { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); Reject(() => p.Accept(f, Plan(route: new[] { new RouteStep(1, 0) }))); });
Test("actual map edge mutation triggers replan", () => { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); p.Accept(f, Plan()); p.Observe(Frame(changed: true), "safe"); Check(p.PlanningRequired, "changed native connection"); });
Test("map room type and quests mutations invalidate prior route semantics", () => { foreach (var field in new[] { "type", "quests" }) { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); p.Accept(f, Plan()); var changed = Frame(); var point = ((JsonObject)changed.State)["context"]!["map"]!.AsArray().OfType<JsonObject>().Single(n => n["row"]!.GetValue<int>() == 2); if (field == "type") point["type"] = "RestSite"; else point["quests"] = new JsonArray("QUEST.CHANGED"); p.Observe(changed, "safe"); Check(p.PlanningRequired, field + " semantic change requests replan"); var actual = JsonSerializer.SerializeToNode(p.MapContext)!.AsArray().OfType<JsonObject>().Single(n => n["row"]!.GetValue<int>() == 2); Check(actual[field]!.ToJsonString() == point[field]!.ToJsonString(), field + " refreshed stable map"); } });
Test("act and strategy changes require new route", () => { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); p.Accept(f, Plan()); p.Observe(Frame(act: 1), "safe"); Check(p.PlanningRequired, "act changed"); p.Accept(Frame(act: 1), Plan()); p.Observe(Frame(act: 1), "aggressive"); Check(p.PlanningRequired, "strategy changed"); });
Test("substantial low HP drop requests exactly one accepted replan", () => { var p = new RoutePlanner(); var f = Frame(); p.Observe(f, "safe"); p.Accept(f, Plan()); var low = Frame(hp: 30); p.Observe(low, "safe"); Check(p.PlanningRequired, "drop triggers"); p.Accept(low, Plan()); p.Observe(low with { Token = "new" }, "safe"); Check(!p.PlanningRequired, "new baseline suppresses repeat"); });
Test("saved route restores on process restart", () => { var path = Path.Combine(Path.GetTempPath(), "route-" + Guid.NewGuid().ToString("N") + ".json"); try { var p = new RoutePlanner(path); var f = Frame(); p.Observe(f, "safe"); p.Accept(f, Plan()); var loaded = new RoutePlanner(path); var next = Frame(currentRow: 1); loaded.Observe(next, "safe"); Check(!loaded.PlanningRequired && loaded.Remaining.Count == 2, "restore valid route"); } finally { if (File.Exists(path)) File.Delete(path); } });
Test("card dictionary deduplicates and preserves native semantics", () => { var f = Frame(); var p = new RoutePlanner(); p.Observe(f, "safe"); var prepared = new DecisionContext().Prepare(f with { Actions = f.Actions.Concat(new[] { new LegalAction("card", "card", "card", Card()) }).ToArray() }, p); var stable = JsonSerializer.SerializeToNode(prepared.StableContext)!; var dictionary = stable["cardDictionary"]!.AsArray(); Check(dictionary.Count == 1, "3 appearances share one definition"); Check(dictionary[0]!["definition"]!["upgradeDescription"]!.ToString() == "Deal 9 damage" && dictionary[0]!["definition"]!["dynamicVars"]!["damage"]!.GetValue<int>() == 6, "semantic fields preserved"); Check(JsonSerializer.Serialize(prepared.State).Contains("card_001"), "compact state references dictionary"); });
Test("volatile token and history stay outside stable context and map only stable", () => { var f = Frame(); var p = new RoutePlanner(); p.Observe(f, "safe"); var context = new DecisionContext(); var a = context.Prepare(f, p); var b = context.Prepare(f with { Token = "token-two" }, p); var stable = JsonSerializer.Serialize(a.StableContext); Check(stable == JsonSerializer.Serialize(b.StableContext), "stable prefix identical"); Check(!stable.Contains("token-one") && !stable.Contains("history-one"), "volatile excluded"); var state = JsonSerializer.SerializeToNode(a.State)!; Check(state["game"]!["context"]!["map"] == null && JsonSerializer.SerializeToNode(a.StableContext)!["actMap"] is JsonArray, "map moved once"); });
Test("new run resets append-only card dictionary", () => { var context = new DecisionContext(); var p = new RoutePlanner(); var a = Frame(); p.Observe(a, "safe"); context.Prepare(a, p); var b = Frame(run: "new-run"); var state = (JsonObject)b.State; state["player"]!["deck"] = new JsonArray(Card("CARD.DEFEND")); p.Observe(b, "safe"); var next = context.Prepare(b, p); var dictionary = JsonSerializer.SerializeToNode(next.StableContext)!["cardDictionary"]!.AsArray(); Check(dictionary.Count == 1 && dictionary[0]!["definition"]!["id"]!.ToString() == "CARD.DEFEND", "run dictionary reset"); });

Test("combat and card-choice context retain effects, piles, position and lessons", () =>
{
    var raw = Frame() with { Scene = "Combat" };
    var state = (JsonObject)raw.State;
    var map = state["context"]!["map"]!.DeepClone();
    state["actMap"] = map;
    state["player"]!["relics"] = new JsonArray(new JsonObject { ["id"] = "RELIC.TEST", ["description"] = "Gain 1 strength each turn", ["counter"] = 2 });
    state["observation"] = new JsonObject { ["hand"] = new JsonArray(Card()), ["drawPile"] = new JsonObject { ["orderKnown"] = false, ["cards"] = new JsonArray(Card("CARD.DEFEND")) }, ["discardPile"] = new JsonArray(Card("CARD.BASH")), ["exhaustPile"] = new JsonArray(Card("CARD.EXHAUST")), ["enemies"] = new JsonArray(new JsonObject { ["hp"] = 20, ["intents"] = new JsonArray(new JsonObject { ["totalDamage"] = 12, ["repeats"] = 2 }) }) };
    var context = new DecisionContext { RunLessons = "Observed: block mitigated a two-hit attack." };
    var route = new RoutePlanner(); route.Observe(raw, "safe");
    var prepared = context.Prepare(raw, route);
    var stable = JsonSerializer.SerializeToNode(prepared.FullStableContext)!;
    var game = JsonSerializer.SerializeToNode(prepared.FullState)!["game"]!;
    Check(JsonNode.DeepEquals(stable["actMap"], map), "actual complete map retained");
    Check(stable["runExperience"]!.ToString().Contains("two-hit"), "lessons survive new-run context reset");
    Check(stable["cardDictionary"]!.AsArray().Count == 4, "all four card-pile definitions preserved");
    Check(game["observation"]!["drawPile"]!["orderKnown"]!.GetValue<bool>() == false && game["currentMapPoint"]!["row"]!.GetValue<int>() == 0, "hidden order and position preserved");
    Check(game["player"]!["relics"]![0]!["description"]!.ToString().Contains("strength") && game["observation"]!["enemies"]![0]!["intents"]![0]!["totalDamage"]!.GetValue<int>() == 12, "effects and actual damage retained");
    context.Commit(prepared);
    var choice = context.Prepare(raw with { Scene = "NSimpleCardSelectScreen" }, route);
    Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(choice.FullState)!["game"], game), "card selection retains complete same battle context");
});
Test("HTTP payload reconstructs latest full state and effects across patch, rebase and retry", () =>
{
    foreach (string protocol in new[] { "Responses", "ChatCompletions" })
    foreach (int maxTurns in new[] { 1, 12 })
    {
        JsonObject? receipt = null;
        using var handler = new ContextHandler(); using var http = new HttpClient(handler); using var client = new ProviderClient(http);
        client.RequestPrepared += r => receipt = r;
        var config = new AutopilotConfig { BaseUrl = "https://example.test/v1", Model = "test", ApiKey = "private-context-key", ApiMode = protocol, SessionMaxTurns = maxTurns };
        var context = new DecisionContext(); var planner = new RoutePlanner();
        var raw = Frame(); planner.Observe(raw, "safe"); planner.Accept(raw, Plan());
        for (int step = 0; step < 3; step++)
        {
            var state = (JsonObject)((JsonObject)raw.State).DeepClone();
            state["player"]!["hp"] = 70 - step;
            var card = Card(); card["description"] = "Current damage " + (6 + step); card["starCost"] = step; card["upgradeStarCost"] = 4;
            state["player"]!["deck"] = new JsonArray(card);
            state["player"]!["relics"] = new JsonArray(new JsonObject { ["id"] = "RELIC.AIM", ["description"] = "Gain aim", ["counter"] = step, ["relatedEffects"] = new JsonArray(new JsonObject { ["description"] = "Aim multiplies arrow damage" }) });
            var actions = new[] { new LegalAction("choose-" + step, "Select card", "card", card.DeepClone()) };
            var prepared = context.Prepare(raw with { Token = "wire-" + step, State = state, Scene = "NSimpleCardSelectScreen", Actions = actions }, planner);
            client.DecideAsync(config, prepared, CancellationToken.None).GetAwaiter().GetResult();
            Check(JsonNode.DeepEquals(receipt!["httpBody"], handler.Body), "receipt is the actual serialized HTTP body");
            Check(!receipt.ToJsonString().Contains(config.ApiKey), "context receipt excludes authorization");
            var rebuilt = ContextHandler.Rebuild(handler.Body!, protocol);
            Check(JsonNode.DeepEquals(rebuilt.Game, JsonSerializer.SerializeToNode(prepared.FullState)!["game"]), "wire history reconstructs the exact latest native DTO");
            Check(JsonNode.DeepEquals(rebuilt.Stable, JsonSerializer.SerializeToNode(prepared.FullStableContext)), "wire history reconstructs the entire map and card dictionary");
            Check(rebuilt.Actions!.Single()!["id"]!.ToString() == "choose-" + step, "wire legal actions match the latest screen");
            Check(rebuilt.Stable!.ToJsonString().Contains("Aim multiplies arrow damage") == false, "live relic data stays in current game state");
            Check(rebuilt.Game!.ToJsonString().Contains("Aim multiplies arrow damage"), "relic terminology reaches the model");
            if (step == 1)
            {
                client.DiscardLastDecision();
                client.DecideAsync(config, prepared, CancellationToken.None).GetAwaiter().GetResult();
                Check(JsonNode.DeepEquals(ContextHandler.Rebuild(handler.Body!, protocol).Game, rebuilt.Game), "retry retains the same current full state");
            }
            context.Commit(prepared);
        }
    }
});
Console.WriteLine($"RESULT passed={passed} failed={failures.Count}");
return failures.Count == 0 ? 0 : 1;

sealed class ContextHandler : HttpMessageHandler
{
    public JsonObject? Body { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
        bool responses = Body.ContainsKey("input");
        var latest = Rebuild(Body, responses ? "Responses" : "ChatCompletions");
        string id = latest.Actions!.Single()!["id"]!.ToString();
        string text = JsonSerializer.Serialize(new { actionId = id, reason = "current state" });
        object result = responses ? new { output_text = text } : (object)new { choices = new[] { new { message = new { role = "assistant", content = text } } } };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result)) };
    }
    public static (JsonNode? Game, JsonNode? Stable, JsonArray? Actions) Rebuild(JsonObject body, string protocol)
    {
        JsonNode? game = null, stable = null; JsonArray? actions = null;
        foreach (var item in (JsonArray)body[protocol == "Responses" ? "input" : "messages"]!)
        {
            if (item?["role"]?.ToString() != "user") continue;
            var envelope = JsonNode.Parse(item["content"]!.ToString())!;
            if (envelope["stableContext"] != null) stable = envelope["stableContext"]!.DeepClone();
            if (envelope["contextUpdate"] is JsonObject updates)
                foreach (var pair in updates)
                    if (pair.Key == "newCardDefinitions") foreach (var def in (JsonArray)pair.Value!) ((JsonArray)stable!["cardDictionary"]!).Add(def!.DeepClone());
                    else stable![pair.Key] = pair.Value?.DeepClone();
            if (envelope["state"] is not JsonObject state) continue;
            actions = envelope["actions"]!.DeepClone().AsArray();
            if (state["updateType"]?.ToString() == "snapshot") game = state["game"]?.DeepClone();
            else foreach (var patch in (JsonArray)state["gamePatch"]!)
            {
                var path = patch!["path"]!.ToString();
                if (path == "") { game = patch["value"]?.DeepClone(); continue; }
                var parts = path[1..].Split('/').Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToArray();
                JsonNode parent = game!;
                foreach (var name in parts[..^1])
                    parent = (parent is JsonArray array ? array[int.Parse(name, System.Globalization.CultureInfo.InvariantCulture)] : parent[name])!;
                string operation = patch["op"]!.ToString();
                if (parent is JsonArray target)
                {
                    if (operation != "replace") throw new InvalidDataException("Array patches replace existing indices; length changes replace the array.");
                    target[int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture)] = patch["value"]?.DeepClone();
                }
                else if (operation == "remove") parent.AsObject().Remove(parts[^1]);
                else if (operation == "replace") parent[parts[^1]] = patch["value"]?.DeepClone();
                else throw new InvalidDataException("Unsupported patch operation: " + operation);
            }
        }
        return (game, stable, actions);
    }
}
