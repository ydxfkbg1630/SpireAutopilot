using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using SpireAutopilot;

public static class SessionReplayProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length is < 3 or > 4 || args.Length == 4 && args[3] != "--execute")
        { Console.WriteLine("SessionReplayProbe configPath nativeFramesFolder report.json [--execute]"); return 2; }
        var frames = SessionFixture.Load(args[1]);
        string output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        bool execute = args.Length == 4;
        var requests = new List<ReplayMeasurement>();
        var report = new Dictionary<string, object?>
        {
            ["success"] = false, ["dryRun"] = !execute, ["providerApiCalls"] = 0, ["gameActionsExecuted"] = 0,
            ["scope"] = "Read-only replay of six consecutive native state snapshots, always queried in both arms. Independent-frame baseline uses the same Responses protocol with PersistentSession=false; session uses PersistentSession=true. This is not a legacy v0.2 binary comparison. Normally cached planned map moves can bypass API calls; this benchmark deliberately queries all six states.",
            ["frames"] = frames.Select(f => new { file = Path.GetFileName(f.Path), f.Frame.Scene, stateHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(f.Frame.State)))) }).ToArray()
        };
        AutopilotConfig? config = null;
        string Safe(string value) => string.IsNullOrEmpty(config?.ApiKey) ? value : value.Replace(config.ApiKey, "[REDACTED]", StringComparison.Ordinal);
        void Save()
        {
            report["requests"] = requests;
            report["aggregates"] = requests.GroupBy(r => r.Mode).Select(g => new
            {
                mode = g.Key, all = Aggregate(g), first = Aggregate(g.Where(r => r.Turn == 1)), warm = Aggregate(g.Where(r => r.Turn > 1))
            }).ToArray();
            File.WriteAllText(output, Safe(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })), new UTF8Encoding(false));
        }
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        using var wire = new BoundedReplayHandler(12);
        using var http = new HttpClient(wire) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            config = AutopilotConfig.Load(args[0]);
            if (execute && !string.Equals(config.ApiMode, "Responses", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The comparison requires a Responses-compatible config in both arms.");
            report["protocol"] = "Responses";
            report["model"] = config.Model;
            report["plannedRequests"] = 12;
            if (!execute)
            {
                // Prepare and validate real fixture semantics with zero HTTP calls.
                var context = new DecisionContext(); var route = new RoutePlanner();
                var prepared = new List<object>();
                foreach (var (_, raw) in frames)
                {
                    route.Observe(raw, config.Strategy);
                    var frame = context.Prepare(raw, route, Array.Empty<string>());
                    prepared.Add(new { raw.Scene, fullStateBytes = SessionFixture.Bytes(frame.FullState!), updateBytes = SessionFixture.Bytes(frame.State), stableBytes = SessionFixture.Bytes(frame.FullStableContext!) });
                    SessionFixture.AcceptFirstRoute(raw, route); context.Commit(frame);
                }
                report["prepared"] = prepared; report["success"] = true; Save(); Console.WriteLine("Prepared six native states; providerApiCalls=0. Add --execute for the bounded 12-request comparison."); return 0;
            }
            foreach (bool persistent in new[] { false, true })
            {
                string mode = persistent ? "persistent_responses_session" : "independent_frame_responses_baseline";
                config.PersistentSession = persistent;
                using var provider = new ProviderClient(http);
                provider.ResetSession();
                var context = new DecisionContext(); var route = new RoutePlanner();
                for (int i = 0; i < frames.Count; i++)
                {
                    var raw = frames[i].Frame;
                    route.Observe(raw, config.Strategy);
                    var prepared = context.Prepare(raw, route, Array.Empty<string>());
                    int callsBefore = wire.Calls;
                    var timer = Stopwatch.StartNew();
                    var decision = await provider.DecideAsync(config, prepared, cancel.Token);
                    timer.Stop();
                    if (!raw.Actions.Any(a => a.Id == decision.ActionId)) throw new InvalidDataException("Model reply selected an action outside the native legal list.");
                    if (route.PlanningRequired) route.Accept(raw, decision);
                    context.Commit(prepared);
                    ProviderUsage? usage = decision.Usage;
                    long? miss = usage is { PromptTokens: not null, CachedTokens: not null } ? usage.PromptTokens.Value - usage.CachedTokens.Value : null;
                    requests.Add(new ReplayMeasurement(mode, i + 1, Path.GetFileName(frames[i].Path), raw.Scene,
                        usage?.PromptTokens, usage?.CachedTokens, miss, usage?.CompletionTokens,
                        usage?.ElapsedMilliseconds ?? timer.ElapsedMilliseconds, timer.ElapsedMilliseconds,
                        wire.BytesSince(callsBefore), wire.Calls - callsBefore, decision.Usage?.Session));
                    report["providerApiCalls"] = wire.Calls; Save();
                    Console.WriteLine($"{mode} {i + 1}/6 input={usage?.PromptTokens} cached={usage?.CachedTokens} miss={miss} ms={timer.ElapsedMilliseconds}");
                    if (i < frames.Count - 1) await Task.Delay(TimeSpan.FromSeconds(3), cancel.Token);
                }
            }
            report["success"] = requests.Count == 12 && wire.Calls == 12;
            var baselineWarm = requests.Where(r => r.Mode.StartsWith("independent", StringComparison.Ordinal) && r.Turn > 1).ToArray();
            var sessionWarm = requests.Where(r => r.Mode.StartsWith("persistent", StringComparison.Ordinal) && r.Turn > 1).ToArray();
            report["comparison"] = new
            {
                warmMissTokensSaved = All(baselineWarm, r => r.MissTokens) is { } b && All(sessionWarm, r => r.MissTokens) is { } s ? (long?)(b - s) : null,
                metric = "Actual provider prompt_tokens minus actual provider cached_tokens, warm turns 2-6. Missing usage remains null.",
                firstAndWarmReportedSeparately = true, pricesEstimated = false
            };
            Save(); return report["success"] is true ? 0 : 1;
        }
        catch (Exception ex) { report["providerApiCalls"] = wire.Calls; report["errorType"] = ex.GetType().Name; report["error"] = Safe(ex.Message); Save(); Console.WriteLine("Replay report saved: " + output); return 1; }
    }

    private static long? All(IEnumerable<ReplayMeasurement> source, Func<ReplayMeasurement, long?> value)
    {
        var rows = source.ToArray(); return rows.Length == 0 || rows.Any(r => value(r) == null) ? null : rows.Sum(r => value(r)!.Value);
    }
    private static object Aggregate(IEnumerable<ReplayMeasurement> source)
    {
        var rows = source.ToArray(); return new { count = rows.Length, inputTokens = All(rows, r => r.InputTokens), cachedTokens = All(rows, r => r.CachedTokens), missTokens = All(rows, r => r.MissTokens), outputTokens = All(rows, r => r.OutputTokens), elapsedMilliseconds = rows.Sum(r => r.ElapsedMilliseconds), payloadBytes = rows.Sum(r => r.PayloadBytes) };
    }
}

public sealed record ReplayMeasurement(string Mode, int Turn, string File, string Scene, long? InputTokens, long? CachedTokens,
    long? MissTokens, long? OutputTokens, long ElapsedMilliseconds, long WallMilliseconds, long PayloadBytes, int HttpCalls, AgentSessionInfo? Session);

internal sealed class BoundedReplayHandler : HttpClientHandler
{
    private readonly int maximum;
    private readonly List<long> sizes = new();
    public int Calls => sizes.Count;
    public BoundedReplayHandler(int maximum) { this.maximum = maximum; AllowAutoRedirect = false; }
    public long BytesSince(int index) => sizes.Skip(index).Sum();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Calls >= maximum) throw new InvalidOperationException("The explicit 12-call replay bound was reached.");
        sizes.Add(request.Content == null ? 0 : (await request.Content.ReadAsByteArrayAsync(cancellationToken)).LongLength);
        return await base.SendAsync(request, cancellationToken);
    }
}
