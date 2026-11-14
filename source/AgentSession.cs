using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;

namespace SpireAutopilot;

/// <summary>In-memory proposal ledger. Only successful validated responses enter this bounded history.</summary>
public sealed class AgentSession
{
    private SessionIdentity? identity;
    private readonly List<JsonNode> ledger = new();
    private string? lastStable;
    private string? responseId;
    private int turns;
    private Snapshot? previous;
    private long revision;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions TextJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal SessionRequest Prepare(SessionIdentity next, bool responses, bool serverState, string stable, string fullStable, string changing, string fullChanging, int maximumTurns, int maximumBytes, double maximumHistoryRatio, bool persistent)
    {
        lock (gate)
        {
        string? reason = !persistent ? "independent_frame" : identity == null ? "initial" : identity != next ? "scope_or_configuration_changed" : null;
        if (reason != null) { identity = next; ledger.Clear(); lastStable = null; responseId = null; turns = 0; previous = null; revision++; }
        var history = ledger.Select(n => n.DeepClone()).ToList();
        var additions = new List<JsonNode>();
        bool rebase = history.Count == 0;
        if (rebase || stable != lastStable)
            additions.Add(Message("user", rebase ? fullStable : StableAddition(lastStable, stable)));
        additions.Add(Message("user", rebase ? fullChanging : changing));
        history.AddRange(additions.Select(n => n.DeepClone()));
        // Measure the complete candidate input after escaping, not a character-count estimate.
        var baseline = new List<JsonNode> { Message("user", fullStable), Message("user", fullChanging) };
        double safeRatio = double.IsFinite(maximumHistoryRatio) ? Math.Max(1, maximumHistoryRatio) : 2.5;
        double inputLimit = Math.Min(Math.Max(1024, maximumBytes), Bytes(baseline) * safeRatio);
        if (persistent && !rebase && (turns >= Math.Max(1, maximumTurns) || Bytes(history) > inputLimit))
        {
            reason = "bounded_history_rebase"; rebase = true;
            additions = baseline;
            history = additions.Select(n => n.DeepClone()).ToList();
        }
        bool usePrevious = serverState && !rebase && responseId != null;
        var sent = usePrevious ? additions : history;
        string mode = !persistent ? "independent_frame" : responses ? serverState ? "responses_server" : "responses_replay" : "chat_replay";
        return new SessionRequest(revision, history, sent, stable, usePrevious ? responseId : null, rebase ? 0 : turns,
            new AgentSessionInfo(mode, (rebase ? 0 : turns) + 1, Bytes(sent), reason));
        }
    }

    internal bool Commit(SessionRequest request, IReadOnlyList<JsonNode> output, string? nextResponseId)
    {
        lock (gate)
        {
        if (request.Revision != revision) return false;
        previous = new Snapshot(ledger.Select(n => n.DeepClone()).ToArray(), lastStable, responseId, turns);
        ledger.Clear(); ledger.AddRange(request.History.Select(n => n.DeepClone()));
        ledger.AddRange(output.Select(n => n.DeepClone()));
        lastStable = request.Stable; responseId = nextResponseId; turns = request.PreviousTurns + 1;
        revision++;
        return true;
        }
    }

    public void DiscardLastDecision()
    {
        lock (gate)
        {
        if (previous == null) return;
        ledger.Clear(); ledger.AddRange(previous.Ledger.Select(n => n.DeepClone()));
        lastStable = previous.Stable; responseId = previous.ResponseId; turns = previous.Turns;
        previous = null; revision++;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
        identity = null; ledger.Clear(); lastStable = null; responseId = null; turns = 0; previous = null; revision++;
        }
    }

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };
    private static int Bytes(IEnumerable<JsonNode> items) => Encoding.UTF8.GetByteCount("[" + string.Join(',', items.Select(n => n.ToJsonString(TextJson))) + "]");
    private static string StableAddition(string? oldJson, string newJson)
    {
        if (oldJson == null) return newJson;
        var oldEnvelope = JsonNode.Parse(oldJson)?.AsObject();
        var newEnvelope = JsonNode.Parse(newJson)?.AsObject();
        if (oldEnvelope?["stableContext"] is not JsonObject oldContext || newEnvelope?["stableContext"] is not JsonObject newContext) return newJson;
        var change = new JsonObject();
        foreach (var pair in newContext)
        {
            var old = oldContext[pair.Key];
            if (JsonNode.DeepEquals(old, pair.Value)) continue;
            if (pair.Key == "cardDictionary" && old is JsonArray before && pair.Value is JsonArray after && before.Count <= after.Count
                && before.Select((item, i) => JsonNode.DeepEquals(item, after[i])).All(same => same))
            {
                change["newCardDefinitions"] = new JsonArray(after.Skip(before.Count).Select(n => n?.DeepClone()).ToArray());
            }
            else change[pair.Key] = pair.Value?.DeepClone();
        }
        return new JsonObject { ["contextUpdate"] = change }.ToJsonString(TextJson);
    }
    internal sealed record SessionIdentity(string Endpoint, string Model, string Strategy, string Protocol, bool? Thinking, string ApiKey, string Scope, double? Temperature, bool JsonMode);
    internal sealed record SessionRequest(long Revision, IReadOnlyList<JsonNode> History, IReadOnlyList<JsonNode> Input, string Stable, string? PreviousResponseId, int PreviousTurns, AgentSessionInfo Info);
    private sealed record Snapshot(IReadOnlyList<JsonNode> Ledger, string? Stable, string? ResponseId, int Turns);
}
