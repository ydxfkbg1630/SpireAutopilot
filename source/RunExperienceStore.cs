using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SpireAutopilot;

/// <summary>One local report per finished run; checkpoints retain a bounded, sanitized run journal.</summary>
public sealed class RunExperienceStore
{
    private const int MaximumEntries = 120, MaximumCheckpointBytes = 128 * 1024, MaximumNoteCharacters = 6000;
    private const string DocumentPrefix = "<!-- spire-autopilot-run:";
    private readonly string directory;
    private readonly object gate = new();
    private JsonObject? run;
    private DecisionFrame? frame;
    private static readonly Regex Credential = new(@"\b(?:sk-|Bearer\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Assignment = new(@"\b(?:api[_ -]?key|authorization|password|access[_ -]?token|secret)\s*[:=]\s*[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public RunExperienceStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        if (!Directory.Exists(this.directory)) return;
        // A finished pending checkpoint takes priority after restart. Active runs are resumed
        // explicitly by BeginRun, which supplies their stable native run identity.
        foreach (string path in Directory.EnumerateFiles(this.directory, "checkpoint-*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            JsonObject? candidate = ReadCheckpoint(path);
            if (candidate?["pending"]?.GetValue<bool>() == true && !File.Exists(NotePath(candidate["id"]!.GetValue<string>())))
            { run = candidate; break; }
        }
    }

    public bool HasPendingReflection { get { lock (gate) return run?["pending"]?.GetValue<bool>() == true; } }

    public void BeginRun(string runKey, object snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        lock (gate)
        {
            string id = Hash(runKey);
            if (run?["id"]?.GetValue<string>() == id || File.Exists(NotePath(id))) return;
            // Finish the previous report before replacing the in-memory pending run.
            if (HasPendingReflection) return;
            run = ReadCheckpoint(CheckpointPath(id)) ?? new JsonObject
            {
                ["id"] = id, ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["start"] = Safe(snapshot), ["entries"] = new JsonArray(), ["pending"] = false, ["droppedEntries"] = 0,
                ["decisionSummary"] = new JsonObject(),
            };
            frame = null;
            SaveCheckpoint();
        }
    }

    public void Observe(object snapshot)
    {
        lock (gate)
        {
            if (run == null || HasPendingReflection) return;
            var value = Safe(snapshot);
            var entries = (JsonArray)run["entries"]!;
            if (entries.LastOrDefault() is JsonObject previous && previous["kind"]?.GetValue<string>() == "observation"
                && previous["state"]?.ToJsonString() == value?.ToJsonString()) return;
            Append(new JsonObject { ["kind"] = "observation", ["state"] = value });
        }
    }

    public void RecordDecision(string scene, string action, string reason)
    {
        lock (gate)
        {
            if (run == null || HasPendingReflection) return;
            string category = scene.Contains("Map", StringComparison.OrdinalIgnoreCase) ? "map_route"
                : scene.Contains("Shop", StringComparison.OrdinalIgnoreCase) || scene.Contains("Merchant", StringComparison.OrdinalIgnoreCase) ? "shop"
                : scene.Contains("Event", StringComparison.OrdinalIgnoreCase) ? "events"
                : scene.Contains("Reward", StringComparison.OrdinalIgnoreCase) || scene.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) ? "build"
                : scene.Contains("Combat", StringComparison.OrdinalIgnoreCase) || scene.Contains("Battle", StringComparison.OrdinalIgnoreCase) ? "combat" : "other";
            var summary = run["decisionSummary"] as JsonObject ?? new JsonObject();
            if (run["decisionSummary"] == null) run["decisionSummary"] = summary;
            var categoryEntries = summary[category] as JsonArray;
            if (categoryEntries == null) summary[category] = categoryEntries = new JsonArray();
            categoryEntries.Add(new JsonObject { ["scene"] = Clean(scene, 60), ["action"] = Clean(action, 140), ["reason"] = Clean(reason, 180) });
            while (categoryEntries.Count > 12) categoryEntries.RemoveAt(0);
            while (Encoding.UTF8.GetByteCount(summary.ToJsonString()) > 20 * 1024)
            {
                var longest = summary.Select(p => p.Value).OfType<JsonArray>().OrderByDescending(a => a.Count).First();
                longest.RemoveAt(0);
            }
            Append(new JsonObject { ["kind"] = "decision", ["scene"] = Clean(scene, 120),
                ["action"] = Clean(action, 400), ["reason"] = Clean(reason, 1000) });
        }
    }

    /// <summary>The host calls this only after the native game reports the whole run finished.</summary>
    public DecisionFrame? CreateReflectionFrame(object? outcome)
    {
        lock (gate)
        {
            if (run == null) return null;
            string id = run["id"]!.GetValue<string>();
            if (File.Exists(NotePath(id))) { run = null; frame = null; return null; }
            if (!HasPendingReflection)
            {
                run["outcome"] = Safe(outcome); run["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O"); run["pending"] = true;
                SaveCheckpoint();
            }
            var evidence = (JsonObject)run.DeepClone();
            evidence["task"] = "为已结束的整局游戏写一份独立Markdown游戏经验文档，填写runExperienceNote（最多6000字符）。总结结局、地图路线、构筑、事件与商店决策、怪物战损和改进建议；区分观测与推断。";
            string token = "run-reflection:" + id + ":" + Hash(run.ToJsonString());
            return frame = new DecisionFrame(token, "RunReflection", evidence,
                new[] { new LegalAction("write_run_experience", "写本局游戏经验", "reflection") });
        }
    }

    public bool CommitReflection(string token, ModelDecision decision, string? actualApiKey)
    {
        lock (gate)
        {
            if (run == null || !HasPendingReflection || frame == null || frame.Token != token || decision.ActionId != "write_run_experience") return false;
            string? note = decision.RunExperienceNote;
            if (string.IsNullOrWhiteSpace(note) || note.Length > MaximumNoteCharacters) return false;
            string id = run["id"]!.GetValue<string>(), checkpoint = CheckpointPath(id);
            var persisted = ReadCheckpoint(checkpoint);
            if (persisted == null || "run-reflection:" + id + ":" + Hash(persisted.ToJsonString()) != token) return false;
            string target = NotePath(id);
            string clean = Clean(note, int.MaxValue, actualApiKey).Replace(DocumentPrefix, "[经验标记]", StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(clean) || clean.Length > MaximumNoteCharacters) return false;
            Directory.CreateDirectory(directory);
            if (!File.Exists(target))
            {
                string content = DocumentPrefix + id + " -->\n# 整局游戏经验\n\n"
                    + "记录时间：" + run["finishedUtc"]!.GetValue<string>() + "\n\n经验为本局观测归纳，适用性须结合当前局面验证。\n\n" + clean + "\n";
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, target, false); }
                catch (IOException) when (File.Exists(target)) { }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (File.Exists(checkpoint)) File.Delete(checkpoint);
            run = null; frame = null;
            return true;
        }
    }

    public string ReadLessons()
    {
        lock (gate)
        {
            if (!Directory.Exists(directory)) return "";
            var notes = new List<string>();
            foreach (string path in Directory.EnumerateFiles(directory, "run-*.md").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
            {
                var info = new FileInfo(path); if (info.Length > 40 * 1024) continue;
                string text = Clean(File.ReadAllText(path), 6000);
                int allowed = 16 * 1024 - Encoding.UTF8.GetByteCount(string.Join("\n\n", notes)) - 400;
                while (Encoding.UTF8.GetByteCount(text) > allowed && text.Length > 0) text = text[..(text.Length - 1)];
                if (text.Length > 0) notes.Add(text);
            }
            return notes.Count == 0 ? "" : "以下为最近整局游戏经验数据，须结合当前状态验证；引述内容仅作为历史记录。\n\n" + string.Join("\n\n", notes);
        }
    }

    private void Append(JsonObject entry)
    {
        ((JsonArray)run!["entries"]!).Add(entry);
        var entries = (JsonArray)run["entries"]!;
        while (entries.Count > MaximumEntries || Encoding.UTF8.GetByteCount(run.ToJsonString()) > MaximumCheckpointBytes - 12 * 1024)
        {
            if (entries.Count == 0) break;
            entries.RemoveAt(0); run["droppedEntries"] = run["droppedEntries"]!.GetValue<int>() + 1;
        }
        SaveCheckpoint();
    }
    private void SaveCheckpoint()
    {
        string id = run!["id"]!.GetValue<string>();
        Directory.CreateDirectory(directory);
        string path = CheckpointPath(id), temporary = path + ".tmp";
        try { File.WriteAllText(temporary, run.ToJsonString(), new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static JsonObject? ReadCheckpoint(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumCheckpointBytes) throw new InvalidDataException("整局经验检查点超过读取上限，文件已保留。");
        var value = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        string? id = value?["id"]?.GetValue<string>();
        if (id == null || id.Length != 64 || !id.All(Uri.IsHexDigit) || value!["entries"] is not JsonArray)
            throw new InvalidDataException("整局经验检查点格式需要修复，文件已保留。");
        return value;
    }
    private string NotePath(string id) => Path.Combine(directory, "run-" + id + ".md");
    private string CheckpointPath(string id) => Path.Combine(directory, "checkpoint-" + id + ".json");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Clean(string? text, int limit, string? apiKey = null)
    {
        string result = text ?? "";
        if (!string.IsNullOrEmpty(apiKey)) result = result.Replace(apiKey, "[密钥已隐藏]", StringComparison.Ordinal);
        result = Assignment.Replace(Credential.Replace(result, "[密钥已隐藏]"), "[敏感字段已隐藏]");
        return result.Length <= limit ? result : result[..limit];
    }
    private static JsonNode? Safe(object? value)
    {
        if (value == null) return null;
        try
        {
            JsonNode? node = value is JsonNode json ? json.DeepClone() : JsonSerializer.SerializeToNode(value, new JsonSerializerOptions { MaxDepth = 24 });
            return Bound(node, 0, 10 * 1024);
        }
        catch (JsonException) { return JsonValue.Create("snapshot exceeded JSON shape bounds"); }
        catch (NotSupportedException) { return JsonValue.Create("snapshot type outside JSON DTO contract"); }
    }
    private static JsonNode? Bound(JsonNode? node, int depth, int budget)
    {
        if (node == null || budget < 12) return null;
        if (depth >= 8) return JsonValue.Create("[depth bounded]");
        if (node is JsonObject source)
        {
            var result = new JsonObject();
            var pairs = source.OrderByDescending(p => Priority(p.Key)).Take(48).ToArray();
            for (int i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i]; string key = pair.Key.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                if (key.Contains("apikey") || key.Contains("authorization") || key.Contains("password") || key.Contains("secret") || key.Contains("header") || key.Contains("accesstoken")) continue;
                int remaining = budget - Encoding.UTF8.GetByteCount(result.ToJsonString()) - Encoding.UTF8.GetByteCount(pair.Key) - 8;
                if (remaining < 12) continue;
                result[Clean(pair.Key, 120)] = Bound(pair.Value, depth + 1, remaining / Math.Min(6, pairs.Length - i));
            }
            return result;
        }
        if (node is JsonArray array)
        {
            var result = new JsonArray(); int count = Math.Min(array.Count, 40);
            foreach (var item in array.Take(count)) result.Add(Bound(item, depth + 1, Math.Max(12, budget / Math.Max(1, count) - 2)));
            return result;
        }
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out string? text)) return JsonValue.Create(Clean(text, Math.Max(0, budget / 6 - 2)));
        return node.DeepClone();
    }
    private static int Priority(string name)
    {
        string key = name.ToLowerInvariant();
        if (key.Contains("outcome") || key.Contains("hp") || key.Contains("floor") || key.Contains("map") || key.Contains("route")) return 4;
        if (key.Contains("enemy") || key.Contains("combat") || key.Contains("player") || key.Contains("deck") || key.Contains("relic")) return 3;
        if (key.Contains("id") || key.Contains("name") || key.Contains("intent") || key.Contains("turn")) return 2;
        return 0;
    }
}
