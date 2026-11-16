using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAutopilot;

public static class SessionEfficiencyProgram
{
    public static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("SessionEfficiencyTests framesFolder [report.json]"); return 2; }
        var checks = new List<object>();
        bool success = false;
        try
        {
            var frames = SessionFixture.Load(args[0]);
            var context = new DecisionContext(); var route = new RoutePlanner(); JsonNode? reconstructed = null;
            var ledger = new List<object>();
            JsonArray? previousDictionary = null;
            string? previousStable = null;
            long fullBytes = 0, deltaBytes = 0;
            foreach (var (path, raw) in frames)
            {
                route.Observe(raw, "survival");
                var prepared = context.Prepare(raw, route);
                var state = SessionFixture.Node(prepared.State);
                var full = SessionFixture.Node(prepared.FullState);
                reconstructed = SessionFixture.Apply(reconstructed, state);
                Check(JsonNode.DeepEquals(reconstructed, full["game"]), "native_delta_reconstructs_" + Path.GetFileName(path));
                Check(JsonNode.DeepEquals(state["gameIdentity"], full["gameIdentity"]), "scope_identity_full_and_delta_" + ledger.Count);
                Check(!SessionFixture.Node(prepared.FullState).ToJsonString().Contains("\"controls\"", StringComparison.Ordinal)
                    && !SessionFixture.Node(prepared.FullState).ToJsonString().Contains("\"sceneInstance\"", StringComparison.Ordinal), "volatile_controls_removed_" + ledger.Count);
                Check(!JsonSerializer.Serialize(new { prepared.State, prepared.StableContext }).Contains(raw.Token, StringComparison.Ordinal), "execution_token_outside_context_" + ledger.Count);
                var dictionary = SessionFixture.Node(prepared.FullStableContext)["cardDictionary"]!.AsArray();
                if (previousDictionary != null)
                    Check(dictionary.Count >= previousDictionary.Count && previousDictionary.Select((d, i) => JsonNode.DeepEquals(d, dictionary[i])).All(v => v), "card_dictionary_append_prefix_" + ledger.Count);
                previousDictionary = (JsonArray)dictionary.DeepClone();
                long fbytes = SessionFixture.Bytes(new { raw.Scene, State = prepared.FullState, StableContext = prepared.FullStableContext, prepared.Actions });
                string serializedStable = JsonSerializer.Serialize(prepared.FullStableContext);
                long introducedStableBytes = serializedStable == previousStable ? 0 : Encoding.UTF8.GetByteCount(serializedStable);
                previousStable = serializedStable;
                long dbytes = SessionFixture.Bytes(new { raw.Scene, prepared.State, prepared.Actions }) + introducedStableBytes;
                fullBytes += fbytes; deltaBytes += dbytes;
                ledger.Add(new { file = Path.GetFileName(path), raw.Scene, update = state["updateType"]?.ToString(), fullBytes = fbytes, deltaBytes = dbytes, introducedStableBytes, dictionaryEntries = dictionary.Count });
                SessionFixture.AcceptFirstRoute(raw, route);
                context.Commit(prepared);
            }
            Check(deltaBytes < fullBytes, "native_sequence_payload_bytes_reduced");

            var sample = frames.Last().Frame;
            var retryContext = new DecisionContext(); var retryRoute = new RoutePlanner(); retryRoute.Observe(sample, "survival");
            var firstAttempt = retryContext.Prepare(sample, retryRoute);
            var secondAttempt = retryContext.Prepare(sample, retryRoute);
            Check(JsonNode.DeepEquals(SessionFixture.Node(firstAttempt.State), SessionFixture.Node(secondAttempt.State)), "uncommitted_retry_preserves_snapshot");

            var appendedState = SessionFixture.Node(sample.State);
            var deck = appendedState["player"]!["deck"]!.AsArray();
            JsonObject newCard = deck.OfType<JsonObject>().First(c => c["id"] != null).DeepClone().AsObject();
            newCard["id"] = "CARD.SESSION_TEST_APPEND"; newCard["name"] = "Test append";
            deck.Add(newCard);
            route.Observe(sample with { State = appendedState }, "survival");
            var appended = context.Prepare(sample with { State = appendedState }, route);
            var appendedDictionary = SessionFixture.Node(appended.FullStableContext)["cardDictionary"]!.AsArray();
            Check(appendedDictionary.Count == previousDictionary!.Count + 1 && previousDictionary.Select((d, i) => JsonNode.DeepEquals(d, appendedDictionary[i])).All(v => v), "new_card_preserves_existing_dictionary_prefix");
            context.Commit(appended);

            var newRun = SessionFixture.Node(sample.State); newRun["runKey"] = "SESSION_TEST_NEW_RUN";
            newRun["player"]!["deck"] = new JsonArray(newCard.DeepClone());
            var nextRaw = sample with { State = newRun };
            route.Observe(nextRaw, "survival");
            var next = context.Prepare(nextRaw, route);
            var nextState = SessionFixture.Node(next.State); var nextDict = SessionFixture.Node(next.FullStableContext)["cardDictionary"]!.AsArray();
            Check(nextState["updateType"]!.ToString() == "snapshot", "new_run_rebases_full_snapshot");
            Check(nextDict.Count == 1 && nextDict[0]!["definition"]!["id"]!.ToString() == "CARD.SESSION_TEST_APPEND", "new_run_resets_dictionary");

            // Independent RFC6901 reader exercises escaping, removal and whole-array replacements.
            var synthetic = JsonNode.Parse("{\"a/b\":{\"x~y\":1,\"remove\":true},\"cards\":[1,2]}")!;
            var patched = SessionFixture.Apply(synthetic, JsonNode.Parse("{\"updateType\":\"patch\",\"gamePatch\":[{\"op\":\"replace\",\"path\":\"/a~1b/x~0y\",\"value\":2},{\"op\":\"remove\",\"path\":\"/a~1b/remove\"},{\"op\":\"replace\",\"path\":\"/cards\",\"value\":[3]}]}")!);
            Check(patched["a/b"]!["x~y"]!.GetValue<int>() == 2 && patched["a/b"]!["remove"] == null && patched["cards"]!.AsArray().Count == 1, "independent_patch_reader_rfc6901");
            foreach (string kind in new[] { "tutorial", "continue", "confirm", "open_shop", "close_shop", "open_chest" })
                Check(LocalDecisionPolicy.Choose(new DecisionFrame("test", "NFixture", new { }, new[] { new LegalAction("a", "advance", kind) }))?.Id == "a", "mechanical_single_" + kind);
            foreach (string kind in new[] { "event", "select_card", "skip", "alternative", "purchase", "remove_card", "rest", "travel" })
                Check(LocalDecisionPolicy.Choose(new DecisionFrame("test", "NFixture", new { hp = 3 }, new[] { new LegalAction("a", "Lose 5 HP", kind) })) == null, "strategic_single_stays_model_" + kind);
            Check(LocalDecisionPolicy.Choose(new DecisionFrame("test", "NFixture", new { }, new[] { new LegalAction("a", "a", "continue"), new LegalAction("b", "b", "continue") })) == null, "multiple_continue_stays_model");
            Check(LocalDecisionPolicy.Choose(new DecisionFrame("test", "NRewardsScreen", new { }, new[] { new LegalAction("gold", "31 gold", "reward", new { type = "GoldReward", amount = 31 }), new LegalAction("card", "Card", "reward", new { type = "CardReward" }) }))?.Id == "gold", "gold_reward_safe_mechanical_action");
            Check(LocalDecisionPolicy.Choose(new DecisionFrame("test", "NRewardsScreen", new { }, new[] { new LegalAction("card", "Card", "reward", new { type = "CardReward" }) })) == null, "single_card_reward_stays_model");
            var boardContext = new DecisionContext(); var boardRoute = new RoutePlanner();
            var board = new { runKey = "board-fixture", cells = Enumerable.Range(0, 64).Select(i => new { row = i / 8, column = i % 8, revealed = false, clue = (int?)null }).ToArray() };
            var boardFrame = new DecisionFrame("b1", "SyntheticBoard", board, new[] { new LegalAction("cell:5", "Reveal", "event") });
            var initialBoard = boardContext.Prepare(boardFrame, boardRoute); boardContext.Commit(initialBoard);
            var revealedBoard = SessionFixture.Node(board); revealedBoard["cells"]![5]!["revealed"] = true; revealedBoard["cells"]![5]!["clue"] = 2;
            var changedBoard = boardContext.Prepare(boardFrame with { State = revealedBoard }, boardRoute);
            Check(JsonNode.DeepEquals(SessionFixture.Apply(SessionFixture.Node(initialBoard.FullState)["game"], SessionFixture.Node(changedBoard.State)), SessionFixture.Node(changedBoard.FullState)["game"]), "sparse_array_patch_reconstructs_board");
            Check(SessionFixture.Bytes(changedBoard.State) < SessionFixture.Bytes(changedBoard.FullState!) / 3, "sparse_reveal_uses_indexed_patch");
            var prefixContext = new DecisionContext(); var prefixRoute = new RoutePlanner(); var prefixSession = new AgentSession();
            var prefixIdentity = new AgentSession.SessionIdentity("fixture", "fixture", "survival", "Responses", null, "", "native", null, false);
            AgentSession.SessionRequest NativeRequest(DecisionFrame raw)
            {
                prefixRoute.Observe(raw, "survival"); var prepared = prefixContext.Prepare(raw, prefixRoute);
                string stable = JsonSerializer.Serialize(new { strategy = "survival", stableContext = prepared.FullStableContext });
                string changing = JsonSerializer.Serialize(new { prepared.Scene, state = prepared.FullState, prepared.Actions });
                return prefixSession.Prepare(prefixIdentity, true, false, stable, stable, changing, changing, 1, 102400, 2.5, true);
            }
            var prefixFirst = NativeRequest(frames[0].Frame); prefixSession.Commit(prefixFirst, Array.Empty<JsonNode>(), null);
            var prefixSecond = NativeRequest(frames[1].Frame);
            string initialMessage = prefixFirst.Input[0].ToJsonString();
            string legacyRebaseMessage = new JsonObject { ["role"] = "user", ["content"] = prefixSecond.Stable }.ToJsonString();
            int PrefixBytes(string left, string right)
            {
                byte[] a = Encoding.UTF8.GetBytes(left), b = Encoding.UTF8.GetBytes(right); int i = 0;
                while (i < Math.Min(a.Length, b.Length) && a[i] == b[i]) i++; return i;
            }
            var rebasePrefix = new { priorPrefixBytes = PrefixBytes(initialMessage, legacyRebaseMessage), optimizedPrefixBytes = PrefixBytes(initialMessage, prefixSecond.Input[0].ToJsonString()), optimizedInputBytes = prefixSecond.Info.InputBytes, scope = "Real native first two frames forced to rebase; exact common message bytes, provider cache behavior measured separately." };
            Check(rebasePrefix.optimizedPrefixBytes > rebasePrefix.priorPrefixBytes, "native_rebase_retains_larger_identical_prefix");
            success = true;
            Write(new { success, providerApiCalls = 0, checks, rebasePrefix, frames = ledger, aggregate = new { fullBytes, deltaBytes, bytesSaved = fullBytes - deltaBytes }, scope = "Real native six-state replay. Baseline repeats full state and stable context; delta counts changing payload plus the entire stable context whenever introduced or changed. These count new semantic payload bytes, excluding repeated session history, HTTP wrappers and assistant output; they are not provider token/cache measurements." });
        }
        catch (Exception ex) { Write(new { success, providerApiCalls = 0, checks, error = ex.ToString() }); }
        return success ? 0 : 1;
        void Check(bool passed, string name) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
        void Write(object report)
        {
            string text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (args.Length > 1) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!); File.WriteAllText(args[1], text); }
            Console.WriteLine(text);
        }
    }
}

public static class SessionFixture
{
    public static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    public static List<(string Path, DecisionFrame Frame)> Load(string directory)
    {
        var files = Directory.GetFiles(directory, "snapshot-*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var selected = files.Where(p => { var part = Path.GetFileName(p).Split('-'); return part.Length > 1 && int.TryParse(part[1], out int n) && n is >= 4 and <= 9; }).ToArray();
        if (selected.Length != 6) throw new InvalidDataException("Supply native snapshot-004 through snapshot-009 (six consecutive real frames).");
        return selected.Select(path => (path, JsonSerializer.Deserialize<DecisionFrame>(File.ReadAllText(path), ReadOptions)!)).ToList();
    }
    public static JsonNode Node(object? value) => JsonSerializer.SerializeToNode(value) ?? throw new InvalidDataException("Expected native snapshot object.");
    public static long Bytes(object value) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value));
    public static JsonNode Apply(JsonNode? prior, JsonNode update)
    {
        if (update["updateType"]?.ToString() == "snapshot") return update["game"]!.DeepClone();
        JsonNode result = prior?.DeepClone() ?? throw new InvalidOperationException("Patch requires base state.");
        foreach (var patch in update["gamePatch"]!.AsArray())
        {
            string path = patch!["path"]!.ToString(); string op = patch["op"]!.ToString();
            if (path.Length == 0) { result = patch["value"]!.DeepClone(); continue; }
            string[] parts = path.Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            JsonNode parent = result;
            foreach (var part in parts.SkipLast(1)) parent = (parent is JsonArray array ? array[int.Parse(part)] : parent[part]) ?? throw new InvalidDataException("Patch parent missing: " + path);
            if (parent is JsonArray targetArray)
            {
                if (op != "replace") throw new InvalidDataException("Array index updates replace existing elements.");
                targetArray[int.Parse(parts[^1])] = patch["value"]?.DeepClone(); continue;
            }
            if (parent is not JsonObject obj) throw new InvalidDataException("Patch parent must be an object or array.");
            if (op == "remove") obj.Remove(parts[^1]);
            else if (op == "replace") obj[parts[^1]] = patch["value"]?.DeepClone();
            else throw new InvalidDataException("Unsupported patch operation: " + op);
        }
        return result;
    }
    public static void AcceptFirstRoute(DecisionFrame raw, RoutePlanner route)
    {
        if (!route.PlanningRequired) return;
        var option = Node(route.RouteOptions[0]);
        route.Accept(raw, new ModelDecision(option["firstActionId"]!.ToString(), "scripted fixture route", RouteOptionId: option["id"]!.ToString()));
    }
}
