using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAutopilot;

// Four bounded, read-only provider decisions: validate one full act route, then measure three matching-prefix decisions.
// args: configPath mapFramePath outputPath [runKey] [currentRow] [currentColumn]
if (args.Length is not (3 or 4 or 6)) { Console.WriteLine("CachePlanProbe configPath mapFramePath outputPath [runKey] [currentRow] [currentColumn]"); return 2; }
AutopilotConfig? config = null;
string Safe(string value) => string.IsNullOrEmpty(config?.ApiKey) ? value : value.Replace(config.ApiKey, "[REDACTED]", StringComparison.Ordinal);
var results = new List<object>();
var report = new Dictionary<string, object?> { ["success"] = false, ["providerApiCalls"] = 0, ["gameActionsExecuted"] = 0 };
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var exitCode = 0;
try
{
    config = AutopilotConfig.Load(args[0]);
    config.RequestTimeoutSeconds = 90;
    var root = JsonNode.Parse(await File.ReadAllTextAsync(args[1], cancellation.Token))!.AsObject();
    var envelope = root.FirstOrDefault(p => p.Key.Equals("frame", StringComparison.OrdinalIgnoreCase)).Value;
    var frame = (envelope ?? root).Deserialize<DecisionFrame>(options)!;
    if (frame.Scene != "NMapScreen") throw new InvalidOperationException("Probe requires an original NMapScreen snapshot.");
    var state = JsonSerializer.SerializeToNode(frame.State)!.AsObject();
    var map = state["context"]?["map"]?.AsArray() ?? throw new InvalidOperationException("Snapshot needs the native map in context.map.");
    state["runKey"] ??= args.Length >= 4 ? args[3] : "cache-plan-probe";
    if (args.Length == 6) state["currentMapPoint"] = new JsonObject { ["row"] = int.Parse(args[4]), ["column"] = int.Parse(args[5]) };
    else if (state["currentMapPoint"] == null)
    {
        var legalSteps = frame.Actions.Where(a => a.Kind == "travel").Select(a => JsonSerializer.SerializeToNode(a.Details)!).ToArray();
        var candidates = map.OfType<JsonObject>().Where(n => (n["children"] as JsonArray)?.Any(c => legalSteps.Any(s => s["row"]!.ToString() == c?["row"]?.ToString() && s["column"]!.ToString() == c?["column"]?.ToString())) == true).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("Supply currentRow/currentColumn to identify the native current map point.");
        state["currentMapPoint"] = new JsonObject { ["row"] = candidates[0]["row"]!.DeepClone(), ["column"] = candidates[0]["column"]!.DeepClone() };
    }
    frame = frame with { State = state };
    var planner = new RoutePlanner();
    planner.Observe(frame, config.Strategy);
    var context = new DecisionContext();
    using var provider = new ProviderClient();
    var usages = new List<ProviderUsage>();
    for (var i = 0; i < 4; i++)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var changed = frame with { Token = frame.Token + "-probe-" + i };
        var prepared = context.Prepare(changed, planner, new[] { "cache-measurement-" + i });
        report["providerApiCalls"] = i + 1;
        var decision = await provider.DecideAsync(config, prepared, cancellation.Token);
        if (i == 0)
        {
            try { planner.Accept(changed, decision); }
            catch
            {
                // The final serialized report receives API-key redaction before persistence or console output.
                report["rejectedDecision"] = decision;
                throw;
            }
            if (planner.PlanningRequired || planner.Remaining.Count == 0) throw new InvalidOperationException("Provider route needs a full legal path.");
            report["validatedRoute"] = planner.Remaining;
            report["planReason"] = Safe(decision.PlanReason ?? decision.Reason);
        }
        if (decision.Usage is { } usage) usages.Add(usage);
        results.Add(new { index = i + 1, actionId = Safe(decision.ActionId), reason = Safe(decision.Reason), usage = decision.Usage, cacheRatio = decision.Usage is { PromptTokens: > 0, CachedTokens: not null } u ? (double?)u.CachedTokens.Value / u.PromptTokens.Value : null });
        report["requests"] = results;
        // Cache persistence is asynchronous; preserve a real interval between completed requests.
        if (i < 3) await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
    }
    var complete = usages.Where(u => u.PromptTokens is > 0 && u.CachedTokens != null).ToArray();
    report["aggregateCacheRatio"] = complete.Length > 0 ? (double?)complete.Sum(u => u.CachedTokens!.Value) / complete.Sum(u => u.PromptTokens!.Value) : null;
    report["success"] = true;
}
catch (Exception ex) { exitCode = 1; report["errorType"] = ex.GetType().Name; report["error"] = Safe(ex.Message); report["requests"] = results; }
var output = Safe(JsonSerializer.Serialize(report, options));
var outputPath = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, output, new UTF8Encoding(false));
Console.WriteLine(output);
return exitCode;
