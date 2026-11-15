using SpireAutopilot;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length is < 2 or > 4) { Console.WriteLine("ExperienceMigrationProbe configPath experienceDirectory"); return 2; }
AutopilotConfig? config = null;
try
{
    config = AutopilotConfig.Load(args[0]);
    var store = new RunExperienceStore(args[1]);
    if (args.Length == 4 && args[2] == "apply-style")
    {
        var frame = store.CreateRevisionFrame();
        string revised = await File.ReadAllTextAsync(args[3]);
        if (!store.CommitReflection(frame.Token, new ModelDecision("write_run_experience", "", RunExperienceNote: revised), config.ApiKey))
            throw new InvalidOperationException("经验总结需通过正文校验并匹配当前版本。");
        Console.WriteLine("Experience summary updated; affirmative writing validation passed.");
        return 0;
    }
    using var handler = new SummaryRewriteHandler();
    using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    using var provider = new ProviderClient(http);
    if (args.Length == 3 && args[2] == "rewrite")
    {
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            var frame = store.CreateRevisionFrame();
            if (handler.LastNote is { Length: > 0 } draft)
            {
                var state = JsonSerializer.SerializeToNode(frame.State)!.AsObject();
                state["draftToRevise"] = draft;
                state["requiredRewrite"] = "逐句改为正向行动句，重写包含这些片段的句子：" + ExperienceSummaryRules.Violation(draft) + "。仅返回策略正文。";
                frame = frame with { State = state };
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(90, config.RequestTimeoutSeconds + 5)));
            try
            {
                var decision = await provider.DecideAsync(config, frame, deadline.Token);
                if (!store.CommitReflection(frame.Token, decision, config.ApiKey)) throw new InvalidOperationException("经验总结重写需重新读取。");
                Console.WriteLine("Experience summary updated; affirmative writing validation passed.");
                return 0;
            }
            catch (ModelProtocolException ex) when (attempt < 4) { Console.WriteLine("Rewriting summary: " + ex.Message); }
        }
    }
    int batches = 0;
    while (store.HasPendingReflection)
    {
        var frame = store.CreateReflectionFrame(null);
        if (frame == null || !frame.Token.StartsWith("experience-migration:", StringComparison.Ordinal)) break;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(60, config.RequestTimeoutSeconds + 5)));
        var decision = await provider.DecideAsync(config, frame, deadline.Token);
        if (!store.CommitReflection(frame.Token, decision, config.ApiKey)) throw new InvalidOperationException("经验迁移回复已过期，请重试。");
        Console.WriteLine($"migration batch={++batches} committed");
    }
    Console.WriteLine($"summary={Path.Combine(Path.GetFullPath(args[1]), "experience.md")} batches={batches}");
    return 0;
}
catch (Exception ex)
{
    string text = ex.GetType().Name + ": " + ex.Message;
    if (!string.IsNullOrEmpty(config?.ApiKey)) text = text.Replace(config.ApiKey, "[REDACTED]", StringComparison.Ordinal);
    Console.Error.WriteLine(text);
    return 1;
}

sealed class SummaryRewriteHandler : DelegatingHandler
{
    public string? LastNote { get; private set; }
    public SummaryRewriteHandler() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var response = await base.SendAsync(request, token);
        if (response.IsSuccessStatusCode)
        {
            using var outer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            string text = ProviderClient.ReadOutputText(outer.RootElement, request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal));
            try
            {
                using var decision = JsonDocument.Parse(text);
                if (decision.RootElement.TryGetProperty("runExperienceNote", out var note) && note.ValueKind == JsonValueKind.String) LastNote = note.GetString();
            }
            catch (JsonException) { }
        }
        return response;
    }
}
