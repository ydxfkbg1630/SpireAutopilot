using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SpireAutopilot;

/// <summary>One rolling summary; run checkpoints survive interrupted updates.</summary>
public sealed class RunExperienceStore
{
    private const int MaximumEntries = 120, MaximumCheckpointBytes = 128 * 1024, MaximumNoteCharacters = 6000;
    private const string SummaryMarker = "<!-- spire-autopilot-experience:";
    private const string LegacyMarker = "<!-- spire-autopilot-run:";
    private string SummaryPath => Path.Combine(directory, "experience.md");
    private string? expectedSummaryHash;
    private List<LegacyNote>? migration;
    private sealed record LegacyNote(string Id, string Path, string Text, string Hash);
    private sealed record Summary(string Text, string Hash, HashSet<string> Runs);
    private readonly string directory;
    private readonly object gate = new();
    private JsonObject? run;
    private readonly Queue<JsonObject> pendingRuns = new();
    private DecisionFrame? frame;
    public string? RestoreDiagnostic { get; private set; }
    private static readonly Regex Credential = new(@"\b(?:sk-|Bearer\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Assignment = new(@"\b(?:api[_ -]?key|authorization|password|access[_ -]?token|secret)\s*[:=]\s*[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public RunExperienceStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        if (!Directory.Exists(this.directory)) return;
        Summary summary = ReadSummary();
        RetireCompleted(summary);
        // Finished journals and the current run have independent lifetimes. Retain all pending
        // reports across restarts while BeginRun resumes the live run by native identity.
        foreach (string path in Directory.EnumerateFiles(this.directory, "checkpoint-*.json*")
            .Where(p => p.EndsWith(".json", StringComparison.Ordinal) || p.EndsWith(".json.tmp", StringComparison.Ordinal) || p.EndsWith(".json.bak", StringComparison.Ordinal))
            .Select(p => p.EndsWith(".json", StringComparison.Ordinal) ? p : p[..^4]).Distinct(StringComparer.Ordinal)
            .OrderBy(p => File.GetLastWriteTimeUtc(File.Exists(p) ? p : File.Exists(p + ".tmp") ? p + ".tmp" : p + ".bak"))
            .ThenBy(p => p, StringComparer.Ordinal))
        {
            JsonObject? candidate = ReadCheckpoint(path);
            if (candidate?["pending"]?.GetValue<bool>() == true && !summary.Runs.Contains(candidate["id"]!.GetValue<string>()))
                pendingRuns.Enqueue(candidate);
        }
    }

    public bool HasPendingReflection
    {
        get
        {
            lock (gate)
            {
                Summary summary = ReadSummary();
                return pendingRuns.Any(r => !summary.Runs.Contains(r["id"]!.GetValue<string>()))
                    || LegacyNotes(summary).Count > 0;
            }
        }
    }

    public void BeginRun(string runKey, object snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        lock (gate)
        {
            string id = Hash(runKey);
            if (run?["id"]?.GetValue<string>() == id || ReadSummary().Runs.Contains(id)
                || pendingRuns.Any(r => r["id"]?.GetValue<string>() == id)) return;
            run = ReadCheckpoint(CheckpointPath(id)) ?? new JsonObject
            {
                ["id"] = id, ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["start"] = Safe(snapshot), ["entries"] = new JsonArray(), ["pending"] = false, ["droppedEntries"] = 0,
                ["decisionSummary"] = new JsonObject(),
            };
            SaveCheckpoint();
        }
    }

    public void Observe(object snapshot)
    {
        lock (gate)
        {
            if (run == null) return;
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
            if (run == null) return;
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

    public DecisionFrame? CreateReflectionFrame(object? outcome)
    {
        lock (gate)
        {
            if (run != null && outcome != null)
            {
                run["outcome"] = Safe(outcome); run["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O"); run["pending"] = true;
                SaveCheckpoint(); pendingRuns.Enqueue(run); run = null;
            }
            Summary summary = ReadSummary();
            RetireCompleted(summary);
            while (pendingRuns.TryPeek(out var done) && summary.Runs.Contains(done["id"]!.GetValue<string>()))
                pendingRuns.Dequeue();
            expectedSummaryHash = summary.Hash;
            migration = LegacyNotes(summary);
            if (migration.Count > 0)
            {
                var state = new JsonObject
                {
                    ["task"] = "将旧逐局记录合并到总体经验总结。保留适用条件，合并重复项，修正冲突；填写完整runExperienceNote（中文Markdown，最多6000字符）。",
                    ["previousExperience"] = summary.Text,
                    ["legacyNotes"] = new JsonArray(migration.Select(n => (JsonNode)new JsonObject { ["id"] = n.Id, ["text"] = n.Text }).ToArray()),
                };
                string token = "experience-migration:" + Hash(string.Join("|", migration.Select(n => n.Id + ":" + n.Hash))) + ":" + summary.Hash;
                return frame = new DecisionFrame(token, "RunReflection", state,
                    new[] { new LegalAction("write_run_experience", "合并经验总结", "reflection") });
            }
            migration = null;
            if (!pendingRuns.TryPeek(out var pending)) { frame = null; return null; }
            var evidence = (JsonObject)pending.DeepClone();
            evidence["previousExperience"] = summary.Text;
            evidence["task"] = "根据本局记录更新总体经验总结。保留仍适用的经验，合并重复项，修正旧结论，标明角色、牌组与适用条件；区分观测与推断。填写完整runExperienceNote（中文Markdown，最多6000字符），用于替换原总结。";
            return frame = new DecisionFrame(ReflectionToken(pending, summary.Hash), "RunReflection", evidence,
                new[] { new LegalAction("write_run_experience", "更新经验总结", "reflection") });
        }
    }

    public bool CommitReflection(string token, ModelDecision decision, string? actualApiKey)
    {
        lock (gate)
        {
            if (frame == null || frame.Token != token || decision.ActionId != "write_run_experience") return false;
            if (string.IsNullOrWhiteSpace(decision.RunExperienceNote) || decision.RunExperienceNote.Length > MaximumNoteCharacters) return false;
            string clean = Clean(decision.RunExperienceNote, int.MaxValue, actualApiKey)
                .Replace(SummaryMarker, "[经验标记]", StringComparison.Ordinal)
                .Replace(LegacyMarker, "[经验标记]", StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(clean) || clean.Length > MaximumNoteCharacters) return false;
            Directory.CreateDirectory(directory);
            using var writeLock = new FileStream(Path.Combine(directory, ".experience.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Summary summary = ReadSummary();
            if (summary.Hash != expectedSummaryHash) return false;
            string? checkpoint = null;
            if (migration != null)
            {
                if (migration.Any(n => !File.Exists(n.Path) || Hash(File.ReadAllText(n.Path)) != n.Hash)) return false;
                foreach (var note in migration) summary.Runs.Add(note.Id);
            }
            else
            {
                if (!pendingRuns.TryPeek(out var pending)) return false;
                string id = pending["id"]!.GetValue<string>(); checkpoint = CheckpointPath(id);
                var persisted = ReadCheckpoint(checkpoint);
                if (persisted == null || ReflectionToken(persisted, summary.Hash) != token) return false;
                summary.Runs.Add(id);
            }
            var metadata = new JsonObject { ["runs"] = new JsonArray(summary.Runs.Order(StringComparer.Ordinal).Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()) };
            string content = SummaryMarker + metadata.ToJsonString() + " -->\n" + clean + "\n";
            string temporary = SummaryPath + ".tmp";
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                if (File.Exists(SummaryPath)) File.Replace(temporary, SummaryPath, SummaryPath + ".bak");
                else File.Move(temporary, SummaryPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            if (checkpoint != null)
            {
                File.Delete(checkpoint);
                pendingRuns.Dequeue();
            }
            RetireCompleted(new Summary(clean, Hash(content), summary.Runs));
            frame = null; migration = null; expectedSummaryHash = null;
            return true;
        }
    }

    public string ReadLessons()
    {
        lock (gate)
        {
            string text = ReadSummary().Text;
            return text.Length == 0 ? "" : "经验总结（历史数据）：\n" + text;
        }
    }

    private static string ReflectionToken(JsonObject pending, string summaryHash)
        => "run-reflection:" + pending["id"]!.GetValue<string>() + ":" + Hash(pending.ToJsonString()) + ":" + summaryHash;

    private Summary ReadSummary()
    {
        if (!File.Exists(SummaryPath)) return new("", Hash(""), new(StringComparer.Ordinal));
        if (new FileInfo(SummaryPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("经验总结超过读取上限。");
        string raw = File.ReadAllText(SummaryPath), text = raw;
        HashSet<string> runs = new(StringComparer.Ordinal);
        if (raw.StartsWith(SummaryMarker, StringComparison.Ordinal))
        {
            int end = raw.IndexOf(" -->", StringComparison.Ordinal);
            if (end < 0) throw new InvalidDataException("经验总结标记损坏，请使用 experience.md.bak 恢复。");
            var metadata = JsonNode.Parse(raw[SummaryMarker.Length..end]) as JsonObject;
            if (metadata?["runs"] is not JsonArray ids) throw new InvalidDataException("经验总结记录格式错误。");
            foreach (var id in ids)
            {
                string value = id?.GetValue<string>() ?? "";
                if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("经验总结记录格式错误。");
                runs.Add(value);
            }
            text = raw[(end + 4)..].Trim();
        }
        return new(Clean(text, MaximumNoteCharacters), Hash(raw), runs);
    }

    private List<LegacyNote> LegacyNotes(Summary summary)
    {
        List<LegacyNote> notes = new();
        if (!Directory.Exists(directory)) return notes;
        int bytes = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "run-*.md").Order(StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(path)[4..];
            if (id.Length != 64 || !id.All(Uri.IsHexDigit) || summary.Runs.Contains(id)) continue;
            string raw = File.ReadAllText(path), text = Clean(raw, 12000);
            if (notes.Count > 0 && bytes + Encoding.UTF8.GetByteCount(text) > 48 * 1024) break;
            notes.Add(new(id, path, text, Hash(raw))); bytes += Encoding.UTF8.GetByteCount(text);
        }
        return notes;
    }

    private void RetireCompleted(Summary summary)
    {
        foreach (string id in summary.Runs)
        {
            foreach (string suffix in new[] { "", ".tmp", ".bak" })
            {
                string checkpoint = CheckpointPath(id) + suffix;
                if (File.Exists(checkpoint)) File.Delete(checkpoint);
            }
            string path = Path.Combine(directory, "run-" + id + ".md");
            if (!File.Exists(path)) continue;
            string archive = directory + ".legacy"; Directory.CreateDirectory(archive);
            File.Move(path, Path.Combine(archive, Path.GetFileName(path) + ".bak"), true);
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
    private JsonObject? ReadCheckpoint(string path)
    {
        JsonObject? ReadValid(string candidate)
        {
            if (!File.Exists(candidate)) return null;
            if (new FileInfo(candidate).Length > MaximumCheckpointBytes) throw new InvalidDataException("checkpoint size");
            var parsed = JsonNode.Parse(File.ReadAllText(candidate), documentOptions: new JsonDocumentOptions { MaxDepth = 24 }) as JsonObject;
            ValidateCheckpoint(parsed, path);
            return parsed;
        }
        string? failure = null;
        foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
        {
            try
            {
                var value = ReadValid(candidate);
                if (value == null) continue;
                if (candidate != path)
                {
                    if (File.Exists(path)) Preserve(path);
                    string restore = path + ".recovering";
                    try { File.Copy(candidate, restore, true); File.Move(restore, path, true); }
                    finally { if (File.Exists(restore)) File.Delete(restore); }
                    RestoreDiagnostic = "整局经验检查点已从有效副本恢复：" + candidate;
                }
                return value;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or FormatException)
            { failure ??= error.GetType().Name; }
        }
        if (failure != null)
        {
            if (File.Exists(path)) Preserve(path);
            RestoreDiagnostic = "整局经验检查点需要恢复，原始字节已另存，其他记录继续使用：" + path + "；" + failure;
        }
        return null;
    }
    private static void Preserve(string path)
    {
        string preserved = path + ".invalid-" + Guid.NewGuid().ToString("N");
        File.Move(path, preserved, false);
    }
    private static void ValidateCheckpoint(JsonObject? value, string path)
    {
        string? id = value?["id"]?.GetValue<string>();
        if (id == null || id.Length != 64 || !id.All(Uri.IsHexDigit) || value!["entries"] is not JsonArray entries
            || Path.GetFileName(path) != "checkpoint-" + id + ".json"
            || value["pending"] is not JsonValue pending || !pending.TryGetValue<bool>(out bool finished)
            || value["droppedEntries"] is not JsonValue dropped || !dropped.TryGetValue<int>(out int count) || count < 0
            || entries.Count > MaximumEntries || entries.Any(e => e is not JsonObject)
            || !DateTimeOffset.TryParse(value["startedUtc"]?.GetValue<string>(), out _)
            || finished && !DateTimeOffset.TryParse(value["finishedUtc"]?.GetValue<string>(), out _)
            || value["decisionSummary"] is not JsonObject summary || summary.Any(p => p.Value is not JsonArray a || a.Any(v => v is not JsonObject)))
            throw new InvalidDataException("整局经验检查点格式需要修复，文件已保留。");
    }
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
