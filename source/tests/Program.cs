using System.Net;
using System.Text;
using System.Text.Json;
using SpireAutopilot;

var passed = 0;
var failures = new List<string>();
async Task Test(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.GetType().Name + " " + ex.Message); Console.WriteLine("FAIL " + name); }
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
AutopilotConfig Config() => new() { BaseUrl = "https://example.test/v1", Model = "configured-model", ApiKey = "test-secret-456", ApiMode = "ChatCompletions" };
DecisionFrame Frame() => new("token-123", "Map", new { hp = 30 }, new[] { new LegalAction("map:1", "Campfire", "travel"), new LegalAction("map:2", "Elite", "travel") });
HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
HttpResponseMessage Chat(string text) => JsonResponse(new { choices = new[] { new { message = new { content = text } } } });

await Test("endpoint roots and full endpoints", () =>
{
    foreach (var input in new[] { "https://example.test", "https://example.test/", "https://example.test/v1", "https://example.test/v1/", "https://example.test/v1/chat/completions", "https://example.test/v1/responses" })
    {
        Check(ProviderClient.BuildEndpoint(input, false).AbsoluteUri == "https://example.test/v1/chat/completions", input);
        Check(ProviderClient.BuildEndpoint(input, true).AbsoluteUri == "https://example.test/v1/responses", input);
    }
    Check(ProviderClient.BuildEndpoint("http://localhost:8080/proxy/v2", false).AbsolutePath == "/proxy/v2/chat/completions", "custom prefix");
    return Task.CompletedTask;
});

await Test("chat request and legal decision", async () =>
{
    using var handler = new FakeHandler(async (request, ct) =>
    {
        Check(request.Method == HttpMethod.Post, "method");
        Check(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "endpoint");
        Check(request.Headers.Authorization?.ToString() == "Bearer test-secret-456", "auth header");
        var body = await request.Content!.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(body);
        Check(json.RootElement.GetProperty("model").GetString() == "configured-model", "model");
        Check(json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString() == "system", "system message");
        Check(body.Contains("map:1") && !body.Contains("test-secret-456"), "legal actions and credential separation");
        Check(!json.RootElement.TryGetProperty("temperature", out _), "temperature omitted");
        Check(json.RootElement.GetProperty("response_format").GetProperty("type").GetString() == "json_object", "JSON mode default");
        Check(!json.RootElement.TryGetProperty("thinking", out _) && !json.RootElement.TryGetProperty("reasoning", out _), "thinking default omitted");
        return Chat("{\"actionId\":\"map:1\",\"reason\":\"Recover health\"}");
    });
    using var http = new HttpClient(handler);
    using var client = new ProviderClient(http);
    var result = await client.DecideAsync(Config(), Frame(), CancellationToken.None);
    Check(result.ActionId == "map:1" && result.Reason == "Recover health", "decision");
    Check(handler.Calls == 1, "single POST");
});

await Test("Responses request and nested output_text", async () =>
{
    var config = Config(); config.ApiMode = "Responses"; config.Temperature = 0.2;
    using var http = new HttpClient(new FakeHandler(async (request, ct) =>
    {
        Check(request.RequestUri!.AbsolutePath == "/v1/responses", "endpoint");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Check(body.RootElement.GetProperty("input")[1].GetProperty("content").GetString()!.Contains("map:1"), "input state");
        Check(body.RootElement.GetProperty("instructions").GetString()!.Contains("actionId"), "instructions");
        Check(!body.RootElement.TryGetProperty("store", out _), "generic stateless service omits store");
        Check(!body.RootElement.TryGetProperty("response_format", out _), "chat JSON extension omitted for Responses");
        Check(body.RootElement.GetProperty("temperature").GetDouble() == 0.2, "optional temperature");
        return JsonResponse(new { output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "```json\n{\"actionId\":\"map:2\",\"reason\":\"Strong deck\"}\n```" } } } } });
    }));
    using var client = new ProviderClient(http);
    Check((await client.DecideAsync(config, Frame(), CancellationToken.None)).ActionId == "map:2", "response decision");
});

await Test("Responses direct output and chat text parts", () =>
{
    using var direct = JsonDocument.Parse("{\"output_text\":\"hello\"}");
    Check(ProviderClient.ReadOutputText(direct.RootElement, true) == "hello", "direct text");
    using var parts = JsonDocument.Parse("{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}}]}");
    Check(ProviderClient.ReadOutputText(parts.RootElement, false) == "ab", "parts");
    return Task.CompletedTask;
});

await Test("stable message prefix survives volatile frame changes in both protocols", async () =>
{
    foreach (var responses in new[] { false, true })
    {
        var captured = new List<string>();
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            captured.Add(await request.Content!.ReadAsStringAsync(ct));
            return responses ? JsonResponse(new { output_text = "{\"actionId\":\"map:1\",\"reason\":\"ready\"}" }) : Chat("{\"actionId\":\"map:1\",\"reason\":\"ready\"}");
        }));
        using var client = new ProviderClient(http);
        var config = Config(); config.ApiMode = responses ? "Responses" : "ChatCompletions";
        var first = Frame() with { StableContext = new { map = "stable-map", deck = new[] { "Strike", "Defend" } } };
        await client.DecideAsync(config, first, CancellationToken.None);
        await client.DecideAsync(config, first with { Token = "random-new-token", State = new { hp = 20, history = "new history" } }, CancellationToken.None);
        using var a = JsonDocument.Parse(captured[0]); using var b = JsonDocument.Parse(captured[1]);
        var field = responses ? "input" : "messages";
        var prefixLength = responses ? 1 : 2;
        for (var i = 0; i < prefixLength; i++)
        {
            Check(a.RootElement.GetProperty(field)[i].GetRawText() == b.RootElement.GetProperty(field)[i].GetRawText(), "byte identical stable messages");
            Check(!a.RootElement.GetProperty(field)[i].GetRawText().Contains("token-123"), "volatile token absent from prefix");
        }
        Check(b.RootElement.GetProperty(field).GetArrayLength() > a.RootElement.GetProperty(field).GetArrayLength(), "conversation appends without rewriting prefix");
        Check(!captured[1].Contains("random-new-token"), "local token omitted from model input");
        Check(captured[0].Split("stable-map").Length == 2, "stable context serialized once");
    }
});

await Test("native Chinese card descriptions remain real text inside protocol message content", async () =>
{
    foreach (var responses in new[] { false, true })
    {
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = doc.RootElement.GetProperty(responses ? "input" : "messages");
            var stable = messages[responses ? 0 : 1].GetProperty("content").GetString()!;
            var changing = messages[responses ? 1 : 2].GetProperty("content").GetString()!;
            Check(stable.Contains("造成6点伤害") && !stable.Contains("\\u9020"), "stable native text preserved");
            Check(changing.Contains("选择卡牌") && !changing.Contains("\\u9009"), "volatile native text preserved");
            Check(JsonDocument.Parse(stable).RootElement.GetProperty("stableContext").GetProperty("description").GetString() == "造成6点伤害", "nested JSON remains valid");
            return responses ? JsonResponse(new { output_text = "{\"actionId\":\"map:1\",\"reason\":\"ready\"}" }) : Chat("{\"actionId\":\"map:1\",\"reason\":\"ready\"}");
        }));
        using var client = new ProviderClient(http);
        var config = Config(); config.ApiMode = responses ? "Responses" : "ChatCompletions";
        await client.DecideAsync(config, Frame() with { StableContext = new { description = "造成6点伤害" }, State = new { prompt = "选择卡牌" } }, CancellationToken.None);
    }
});

await Test("route plan and visible reasoning summaries parse with strict coordinates", async () =>
{
    var result = ProviderClient.ParseDecision("{\"actionId\":\"map:1\",\"reason\":\"Recover\",\"routePlan\":[{\"row\":1,\"column\":2},{\"row\":2,\"column\":3}],\"planReason\":\"Campfire before boss\",\"reasoningSummary\":\"Low health favors rest\"}", Frame().Actions);
    Check(result.RoutePlan?.Count == 2 && result.RoutePlan[1] == new RouteStep(2, 3), "route parsed");
    Check(result.PlanReason == "Campfire before boss" && result.ReasoningSummary == "Low health favors rest", "summaries parsed");
    foreach (var route in new[] { "{}", "[{\"row\":1.5,\"column\":0}]", "[{\"row\":1}]", JsonSerializer.Serialize(Enumerable.Range(0, 101).Select(i => new { row = i, column = 0 })) })
        await Throws<ModelProtocolException>(() => Task.FromResult(ProviderClient.ParseDecision("{\"actionId\":\"map:1\",\"routePlan\":" + route + "}", Frame().Actions)));
});

await Test("usage covers DeepSeek and OpenAI with missing counts nullable", () =>
{
    foreach (var sample in new[]
    {
        ("{\"usage\":{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":70,\"prompt_cache_miss_tokens\":30,\"completion_tokens\":8}}", 100L, 70L, 8L),
        ("{\"usage\":{\"prompt_cache_hit_tokens\":70,\"prompt_cache_miss_tokens\":30,\"completion_tokens\":8}}", 100L, 70L, 8L),
        ("{\"usage\":{\"prompt_tokens\":100,\"prompt_tokens_details\":{\"cached_tokens\":60},\"completion_tokens\":9}}", 100L, 60L, 9L),
        ("{\"usage\":{\"input_tokens\":100,\"input_tokens_details\":{\"cached_tokens\":50},\"output_tokens\":10}}", 100L, 50L, 10L)
    })
    {
        using var doc = JsonDocument.Parse(sample.Item1);
        var usage = ProviderClient.ReadUsage(doc.RootElement, 1234);
        Check(usage.PromptTokens == sample.Item2 && usage.CachedTokens == sample.Item3 && usage.CompletionTokens == sample.Item4 && usage.ElapsedMilliseconds == 1234, "usage mapping");
    }
    using var absent = JsonDocument.Parse("{}");
    Check(ProviderClient.ReadUsage(absent.RootElement, 42) == new ProviderUsage(null, null, null, 42), "missing usage stays nullable");
    return Task.CompletedTask;
});

await Test("route candidate ID parses without routePlan and respects length and type", async () =>
{
    var decision = ProviderClient.ParseDecision("{\"actionId\":\"map:1\",\"routeOptionId\":\"route-003\",\"planReason\":\"Rest before boss\"}", Frame().Actions);
    Check(decision.RouteOptionId == "route-003" && decision.RoutePlan == null && decision.PlanReason == "Rest before boss", "candidate selection");
    foreach (var option in new[] { "42", "\"\"", JsonSerializer.Serialize(new string('x', 81)) })
        await Throws<ModelProtocolException>(() => Task.FromResult(ProviderClient.ParseDecision("{\"actionId\":\"map:1\",\"routeOptionId\":" + option + "}", Frame().Actions)));
});

await Test("explicit thinking extension respects protocol and default", async () =>
{
    foreach (var responses in new[] { false, true })
    {
        var config = Config(); config.ApiMode = responses ? "Responses" : "ChatCompletions"; config.ThinkingEnabled = false;
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = doc.RootElement;
            if (responses)
            {
                Check(body.GetProperty("reasoning").GetProperty("effort").GetString() == "none", "responses reasoning");
                Check(!body.TryGetProperty("thinking", out _), "chat extension omitted for responses");
                return JsonResponse(new { output_text = "{\"actionId\":\"map:1\",\"reason\":\"ready\"}" });
            }
            Check(body.GetProperty("thinking").GetProperty("type").GetString() == "disabled", "chat thinking");
            return Chat("{\"actionId\":\"map:1\",\"reason\":\"ready\"}");
        }));
        using var client = new ProviderClient(http);
        Check((await client.DecideAsync(config, Frame(), CancellationToken.None)).ActionId == "map:1", "decision");
    }
});

await Test("combat requests enable thinking across both protocols and choice scenes", async () =>
{
    foreach (var responses in new[] { false, true })
    foreach (string scene in new[] { "Combat", "HandCardSelect", "NCombatPileCardSelectScreen" })
    {
        var config = Config(); config.ThinkingEnabled = false; config.ApiMode = responses ? "Responses" : "ChatCompletions";
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check(responses ? doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "high"
                : doc.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "enabled", "combat thinking must be enabled: " + scene);
            return responses ? JsonResponse(new { output_text = "{\"actionId\":\"map:1\",\"reason\":\"ready\"}" }) : Chat("{\"actionId\":\"map:1\",\"reason\":\"ready\"}");
        }));
        using var client = new ProviderClient(http);
        var frame = Frame() with { Scene = scene, IsCombatDecision = true };
        await client.DecideAsync(config, frame, CancellationToken.None);
        Check(config.ThinkingEnabled == false, "noncombat configuration retained");
    }
});
await Test("chat JSON mode may be disabled explicitly", async () =>
{
    var config = Config(); config.UseJsonMode = false;
    using var http = new HttpClient(new FakeHandler(async (request, ct) =>
    {
        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Check(!doc.RootElement.TryGetProperty("response_format", out _), "compatibility opt-out");
        return Chat("{\"actionId\":\"map:1\",\"reason\":\"ready\"}");
    }));
    using var client = new ProviderClient(http);
    Check((await client.DecideAsync(config, Frame(), CancellationToken.None)).ActionId == "map:1", "decision");
});

await Test("invented and executable IDs rejected", async () =>
{
    foreach (var id in new[] { "map:3", "MAP:1", "map:1 ", "System.IO.File.Delete", "" })
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Chat(JsonSerializer.Serialize(new { actionId = id, reason = "choose" })))));
        using var client = new ProviderClient(http);
        await Throws<InvalidOperationException>(() => client.DecideAsync(Config(), Frame(), CancellationToken.None));
    }
});

await Test("invalid JSON safely rejected", async () =>
{
    foreach (var output in new[] { "Here is my answer: map:1", "{}", "{\"actionId\":42}", "[]" })
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Chat(output))));
        using var client = new ProviderClient(http);
        await Throws<InvalidOperationException>(() => client.DecideAsync(Config(), Frame(), CancellationToken.None));
    }
});

await Test("format diagnostics distinguish stages and redact before sample and hex", async () =>
{
    foreach (var inner in new[] { false, true })
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(inner
            ? Chat("test-secret-456 " + new string('x', 20000))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("test-secret-456 " + new string('x', 20000), Encoding.UTF8, "text/plain") })));
        using var client = new ProviderClient(http);
        try { await client.DecideAsync(Config(), Frame(), CancellationToken.None); throw new Exception("Expected diagnostic"); }
        catch (ProviderFormatException ex)
        {
            Check(ex.Stage == (inner ? "model_content" : "outer_response"), "parse stage");
            Check(ex.Diagnostic.Contains("responseBytes=") && ex.Diagnostic.Contains("first40Hex=") && ex.Diagnostic.Contains("jsonByte="), "diagnostic metadata");
            Check(!ex.ToString().Contains("test-secret-456") && !ex.Diagnostic.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes("test-secret-456"))), "redaction in samples and hex");
            Check(ex.Diagnostic.Length < 10000, "bounded diagnostic");
        }
    }
});

await Test("transient HTTP classified while authentication stays configuration failure", async () =>
{
    foreach (var status in new[] { 401, 429, 503 })
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        using var client = new ProviderClient(http);
        try { await client.DecideAsync(Config(), Frame(), CancellationToken.None); throw new Exception("Expected status error"); }
        catch (InvalidOperationException ex)
        {
            Check((ex is ProviderTransientException) == (status != 401), "classification");
        }
    }
});

await Test("HTTP errors redact provider body and perform single POST", async () =>
{
    using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("test-secret-456 malicious echoed credential") }));
    using var http = new HttpClient(handler);
    using var client = new ProviderClient(http);
    try { await client.DecideAsync(Config(), Frame(), CancellationToken.None); throw new Exception("Expected HTTP error"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("429") && !ex.Message.Contains("test-secret"), "safe status"); }
    Check(handler.Calls == 1, "no retry");
});

await Test("malformed credential yields safe validation error", async () =>
{
    using var handler = new FakeHandler((_, _) => Task.FromResult(Chat("{}")));
    using var http = new HttpClient(handler);
    using var client = new ProviderClient(http);
    var config = Config(); config.ApiKey = "secret-injected\r\nheader";
    try { await client.DecideAsync(config, Frame(), CancellationToken.None); throw new Exception("Expected credential error"); }
    catch (InvalidOperationException ex) { Check(!ex.Message.Contains("secret-injected"), "secret redacted"); }
    Check(handler.Calls == 0, "blocked before network");
});

await Test("caller cancellation propagates", async () =>
{
    using var http = new HttpClient(new FakeHandler(async (_, ct) => { await Task.Delay(10000, ct); return Chat("{}"); }));
    using var client = new ProviderClient(http);
    using var cancel = new CancellationTokenSource(30);
    await Throws<OperationCanceledException>(() => client.DecideAsync(Config(), Frame(), cancel.Token));
});

await Test("timeout reports bounded failure", async () =>
{
    using var http = new HttpClient(new FakeHandler(async (_, ct) => { await Task.Delay(10000, ct); return Chat("{}"); }));
    using var client = new ProviderClient(http);
    var config = Config(); config.RequestTimeoutSeconds = 1;
    await Throws<TimeoutException>(() => client.DecideAsync(config, Frame(), CancellationToken.None));
});

await Test("response size is bounded", async () =>
{
    using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) })));
    using var client = new ProviderClient(http);
    await Throws<InvalidOperationException>(() => client.DecideAsync(Config(), Frame(), CancellationToken.None));
});

await Test("empty and duplicate actions blocked before POST", async () =>
{
    using var handler = new FakeHandler((_, _) => Task.FromResult(Chat("{}")));
    using var http = new HttpClient(handler);
    using var client = new ProviderClient(http);
    await Throws<InvalidOperationException>(() => client.DecideAsync(Config(), new("t", "Map", new { }, Array.Empty<LegalAction>()), CancellationToken.None));
    await Throws<InvalidOperationException>(() => client.DecideAsync(Config(), new("t", "Map", new { }, new[] { new LegalAction("a", "1", "x"), new LegalAction("a", "2", "x") }), CancellationToken.None));
    Check(handler.Calls == 0, "blocked before network");
});

await Test("connection test validates decision", async () =>
{
    using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Chat("{\"actionId\":\"test\",\"reason\":\"ready\"}"))));
    using var client = new ProviderClient(http);
    Check((await client.TestAsync(Config(), CancellationToken.None)).Contains("连接成功"), "test result");
});

await Test("configuration roundtrip with Windows user credential", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "spire-autopilot-test-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(dir, "config.json");
    try
    {
        var config = Config(); config.Strategy = "保持生命"; config.DecisionDelaySeconds = 1.5;
        config.Save(path);
        var saved = File.ReadAllText(path);
        Check(!saved.Contains(config.ApiKey), "secret encrypted at rest");
        Check(saved.Contains("ApiKeyProtected"), "encrypted field");
        var loaded = AutopilotConfig.Load(path);
        Check(loaded.ApiKey == config.ApiKey && loaded.Strategy == config.Strategy && loaded.DecisionDelaySeconds == 1.5, "roundtrip");
        File.WriteAllText(Path.Combine(dir, "legacy.json"), "{}");
        config.ApiKey = ""; config.Save(path);
        Check(AutopilotConfig.Load(path).ApiKey == "", "clear key");
        Check(AutopilotConfig.Load(Path.Combine(dir, "absent.json")).RequestTimeoutSeconds == 45, "default config");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    return Task.CompletedTask;
});

await Test("stateless Responses replays complete output items exactly and appends stable delta", async () =>
{
    var bodies = new List<JsonElement>();
    var reasoning = new { type = "reasoning", id = "reason-item", encrypted_content = "opaque-provider-value", summary = Array.Empty<object>() };
    var message = new { type = "message", id = "message-item", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = "{\"actionId\":\"map:1\",\"reason\":\"ready\"}", annotations = Array.Empty<object>() } } };
    using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(body.RootElement.Clone()); return JsonResponse(new { id = "response-id", output = new object[] { reasoning, message }, usage = new { input_tokens = 100, output_tokens = 5 } }); }));
    using var client = new ProviderClient(http); var config = Config(); config.ApiMode = "Responses";
    var first = Frame() with { StableContext = new { baseline = "stable-first" } };
    var a = await client.DecideAsync(config, first, CancellationToken.None);
    var b = await client.DecideAsync(config, first with { StableContext = new { addition = "stable-delta" }, FullStableContext = new { baseline = "stable-first", addition = "stable-delta" }, State = new { hp = 20 } }, CancellationToken.None);
    var input = bodies[1].GetProperty("input");
    Check(input.GetArrayLength() == 6 && input[2].GetProperty("encrypted_content").GetString() == "opaque-provider-value", "non-message item replayed intact");
    Check(input[3].GetRawText() == JsonSerializer.SerializeToElement(message).GetRawText(), "full message output replayed intact");
    Check(input[4].GetProperty("content").GetString()!.Contains("stable-delta"), "new stable addition appended");
    Check(!bodies[1].TryGetProperty("store", out _) && !bodies[1].TryGetProperty("previous_response_id", out _), "generic service omits server state fields");
    Check(a.Usage?.Session?.TurnCount == 1 && b.Usage?.Session?.TurnCount == 2 && b.Usage.Session.Mode == "responses_replay", "actual session metadata");
});

await Test("OpenAI Responses uses previous_response_id with only new input", async () =>
{
    var bodies = new List<JsonElement>();
    using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(body.RootElement.Clone()); return JsonResponse(new { id = "resp_abc", output_text = "{\"actionId\":\"map:1\"}" }); }));
    using var client = new ProviderClient(http); var config = Config(); config.ApiMode = "Responses"; config.BaseUrl = "https://api.openai.com/v1";
    await client.DecideAsync(config, Frame(), CancellationToken.None); await client.DecideAsync(config, Frame(), CancellationToken.None);
    Check(bodies[0].GetProperty("store").GetBoolean() && !bodies[0].TryGetProperty("previous_response_id", out _), "initial server branch");
    Check(bodies[1].GetProperty("previous_response_id").GetString() == "resp_abc" && bodies[1].GetProperty("input").GetArrayLength() == 1, "server state continuation sends only latest input");
});

await Test("failed decision and explicit rollback leave replay history unpolluted", async () =>
{
    var bodies = new List<JsonElement>(); int calls = 0;
    using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(body.RootElement.Clone()); return Chat(++calls == 1 ? "{\"actionId\":\"invalid\"}" : "{\"actionId\":\"map:1\"}"); }));
    using var client = new ProviderClient(http); var config = Config();
    await Throws<ModelProtocolException>(() => client.DecideAsync(config, Frame(), CancellationToken.None));
    await client.DecideAsync(config, Frame(), CancellationToken.None); client.DiscardLastDecision(); await client.DecideAsync(config, Frame(), CancellationToken.None);
    Check(bodies.All(b => b.GetProperty("messages").GetArrayLength() == 3), "invalid and discarded proposals absent");
});

await Test("turn bound rebases with complete current state and full stable context", async () =>
{
    var bodies = new List<JsonElement>();
    using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(body.RootElement.Clone()); return Chat("{\"actionId\":\"map:1\"}"); }));
    using var client = new ProviderClient(http); var config = Config(); config.SessionMaxTurns = 1;
    var frame = Frame() with { State = new { patch = "delta" }, FullState = new { hp = 99, marker = "full-current" }, StableContext = new { delta = "last-card" }, FullStableContext = new { cards = "complete-dictionary" } };
    await client.DecideAsync(config, frame, CancellationToken.None); var rebased = await client.DecideAsync(config, frame, CancellationToken.None);
    Check(bodies[1].GetProperty("messages").GetArrayLength() == 3, "bounded replay length");
    Check(bodies[1].GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("complete-dictionary") && bodies[1].GetProperty("messages")[2].GetProperty("content").GetString()!.Contains("full-current"), "rebase full envelopes");
    Check(rebased.Usage?.Session?.RebaseReason == "bounded_history_rebase" && rebased.Usage.Session.TurnCount == 1, "rebase metadata");
});

await Test("scope and key changes reset session and TestAsync stays isolated", async () =>
{
    var counts = new List<int>();
    using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var messages = body.RootElement.GetProperty("messages"); counts.Add(messages.GetArrayLength()); return Chat(messages.GetRawText().Contains("connection-test") || messages.GetRawText().Contains("ConnectionTest") ? "{\"actionId\":\"test\"}" : "{\"actionId\":\"map:1\"}"); }));
    using var client = new ProviderClient(http); var config = Config();
    var frame = Frame() with { FullState = new { game = new { runKey = "run-a", CurrentActIndex = 0 } } };
    await client.DecideAsync(config, frame, CancellationToken.None); await client.TestAsync(config, CancellationToken.None); await client.DecideAsync(config, frame, CancellationToken.None);
    config.ApiKey = "changed-secret"; await client.DecideAsync(config, frame, CancellationToken.None);
    await client.DecideAsync(config, frame with { FullState = new { game = new { runKey = "run-a", CurrentActIndex = 1 } } }, CancellationToken.None);
    Check(counts.SequenceEqual(new[] { 3, 3, 5, 3, 3 }), "test isolation and configuration/act reset");
});

await Test("independent frame mode uses full envelopes and no accumulated turns", async () =>
{
    var counts = new List<int>(); using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); counts.Add(body.RootElement.GetProperty("messages").GetArrayLength()); return Chat("{\"actionId\":\"map:1\"}"); }));
    using var client = new ProviderClient(http); var config = Config(); config.PersistentSession = false;
    await client.DecideAsync(config, Frame(), CancellationToken.None); var last = await client.DecideAsync(config, Frame(), CancellationToken.None);
    Check(counts.SequenceEqual(new[] { 3, 3 }) && last.Usage?.Session?.Mode == "independent_frame" && last.Usage.Session.TurnCount == 1, "independent sessions");
});

await Test("stable card additions append only new definitions without repeating map", async () =>
{
    var bodies = new List<JsonElement>(); using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(body.RootElement.Clone()); return Chat("{\"actionId\":\"map:1\"}"); }));
    using var client = new ProviderClient(http); var config = Config();
    var first = Frame() with { StableContext = new { actMap = "same-large-map", cardDictionary = new[] { new { cardRef = "card_001", definition = "Strike" } } } };
    await client.DecideAsync(config, first, CancellationToken.None);
    await client.DecideAsync(config, first with { StableContext = new { actMap = "same-large-map", cardDictionary = new[] { new { cardRef = "card_001", definition = "Strike" }, new { cardRef = "card_002", definition = "Defend" } } } }, CancellationToken.None);
    var added = bodies[1].GetProperty("messages")[4].GetProperty("content").GetString()!;
    Check(added.Contains("newCardDefinitions") && added.Contains("card_002") && !added.Contains("card_001") && !added.Contains("same-large-map"), "stable delta compact and append-only");
});

await Test("candidate UTF8 input budget triggers rebase before next request", async () =>
{
    var bodies = new List<JsonElement>(); using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(body.RootElement.Clone()); return Chat("{\"actionId\":\"map:1\",\"reason\":\"" + new string('z', 600) + "\"}"); }));
    using var client = new ProviderClient(http); var config = Config(); config.SessionMaxInputBytes = 1024;
    await client.DecideAsync(config, Frame() with { State = new { old = new string('x', 400) } }, CancellationToken.None);
    var next = await client.DecideAsync(config, Frame() with { State = new { patch = new string('y', 400) }, FullState = new { authoritative = "latest-current" } }, CancellationToken.None);
    Check(next.Usage?.Session?.RebaseReason == "bounded_history_rebase" && bodies[1].GetProperty("messages").GetArrayLength() == 3 && bodies[1].GetProperty("messages")[2].GetRawText().Contains("latest-current"), "full candidate measured and rebased");
});

await Test("cancellation after an existing turn does not commit cancelled input", async () =>
{
    var bodies = new List<JsonElement>(); int call = 0;
    using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(body.RootElement.Clone()); if (++call == 2) await Task.Delay(10000, ct); return Chat("{\"actionId\":\"map:1\"}"); }));
    using var client = new ProviderClient(http); var config = Config(); await client.DecideAsync(config, Frame(), CancellationToken.None);
    using var cancel = new CancellationTokenSource(20);
    await Throws<OperationCanceledException>(() => client.DecideAsync(config, Frame() with { State = new { cancelledMarker = "never-commit" } }, cancel.Token));
    await client.DecideAsync(config, Frame(), CancellationToken.None);
    Check(!bodies[2].GetRawText().Contains("never-commit") && bodies[2].GetProperty("messages").GetArrayLength() == 5, "cancelled turn absent from ledger");
});

await Test("relative history budget rebases from full state and reports exact UTF8 input bytes", async () =>
{
    var bodies = new List<JsonElement>();
    using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(body.RootElement.Clone()); return JsonResponse(new { output_text = "{\"actionId\":\"map:1\",\"reason\":\"ready\"}" }); }));
    using var client = new ProviderClient(http); var config = Config(); config.ApiMode = "Responses"; config.SessionMaxHistoryRatio = 1.1; config.SessionMaxInputBytes = 102400;
    var first = Frame() with { StableContext = new { description = "造成6点伤害" }, FullState = new { authoritative = "full-one" } };
    await client.DecideAsync(config, first, CancellationToken.None);
    var result = await client.DecideAsync(config, first with { State = new { patch = "delta-two" }, FullState = new { authoritative = "full-two" } }, CancellationToken.None);
    var input = bodies[1].GetProperty("input");
    Check(result.Usage?.Session?.RebaseReason == "bounded_history_rebase" && result.Usage.Session.TurnCount == 1 && input.GetArrayLength() == 2, "relative cap resets full history");
    Check(input[1].GetProperty("content").GetString()!.Contains("full-two") && !input.GetRawText().Contains("delta-two"), "current full snapshot used");
    var textOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    var exact = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(input, textOptions));
    Check(result.Usage!.Session!.InputBytes == exact, "exact actual input UTF8 byte count");
});

await Test("Responses reasoning and function items stay out of decision text but replay in full", async () =>
{
    var bodies = new List<JsonElement>();
    var output = new object[]
    {
        new { type = "reasoning", content = new[] { new { type = "reasoning_text", text = "private reasoning that is not JSON" } }, opaque = "retain-this" },
        new { type = "function_call", content = new[] { new { type = "text", text = "not a decision" } }, name = "unused" },
        new { type = "message", role = "assistant", content = new object[] { new { type = "reasoning_text", text = "exclude-even-here" }, new { type = "output_text", text = "{\"actionId\":\"map:1\",\"reason\":\"visible summary\"}" } } }
    };
    using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(body.RootElement.Clone()); return JsonResponse(new { output }); }));
    using var client = new ProviderClient(http); var config = Config(); config.ApiMode = "Responses"; config.SessionMaxHistoryRatio = 20;
    var first = await client.DecideAsync(config, Frame(), CancellationToken.None); await client.DecideAsync(config, Frame(), CancellationToken.None);
    Check(first.Reason == "visible summary" && !first.Reason.Contains("private"), "only final public message parsed");
    Check(bodies[1].GetProperty("input")[2].GetProperty("opaque").GetString() == "retain-this" && bodies[1].GetRawText().Contains("private reasoning"), "opaque full output retained for provider replay");
});

await Test("failed or incomplete Responses never commit even with valid content", async () =>
{
    foreach (var failedStatus in new[] { "failed", "incomplete" })
    {
        var lengths = new List<int>(); int calls = 0;
        using var http = new HttpClient(new FakeHandler(async (request, ct) => { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); lengths.Add(body.RootElement.GetProperty("input").GetArrayLength()); return JsonResponse(new { status = ++calls == 1 ? failedStatus : "completed", output_text = "{\"actionId\":\"map:1\",\"reason\":\"ready\"}" }); }));
        using var client = new ProviderClient(http); var config = Config(); config.ApiMode = "Responses";
        await Throws<ModelProtocolException>(() => client.DecideAsync(config, Frame(), CancellationToken.None));
        var good = await client.DecideAsync(config, Frame(), CancellationToken.None);
        Check(lengths.SequenceEqual(new[] { 2, 2 }) && good.Usage?.Session?.TurnCount == 1, "bad status keeps session empty");
    }
});

await Test("malformed successful envelope remains a retryable protocol error", async () =>
{
    foreach (bool responses in new[] { false, true })
    foreach (string payload in new[] { "null", "[]", "42", "{\"choices\":null}", "{\"output\":null}" })
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") })));
        using var client = new ProviderClient(http);
        var config = Config(); config.ApiMode = responses ? "Responses" : "ChatCompletions";
        await Throws<ModelProtocolException>(() => client.DecideAsync(config, Frame(), CancellationToken.None));
    }
});

await Test("chat truncation does not execute or commit a partial completion", async () =>
{
    int call = 0; var counts = new List<int>();
    using var http = new HttpClient(new FakeHandler(async (request, ct) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        counts.Add(body.RootElement.GetProperty("messages").GetArrayLength());
        return JsonResponse(new { choices = new[] { new { finish_reason = ++call == 1 ? "length" : "stop", message = new { content = "{\"actionId\":\"map:1\"}" } } } });
    }));
    using var client = new ProviderClient(http);
    await Throws<ModelProtocolException>(() => client.DecideAsync(Config(), Frame(), CancellationToken.None));
    await client.DecideAsync(Config(), Frame(), CancellationToken.None);
    Check(counts.SequenceEqual(new[] { 3, 3 }), "partial response omitted from session");
});

await Test("response body interruption is retryable and leaves session intact", async () =>
{
    using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new BrokenContent() })));
    using var client = new ProviderClient(http);
    await Throws<ProviderTransientException>(() => client.DecideAsync(Config(), Frame(), CancellationToken.None));
});

await Test("map planning requires the model selected full route before session commit", async () =>
{
    int call = 0; var counts = new List<int>();
    using var http = new HttpClient(new FakeHandler(async (request, ct) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        counts.Add(body.RootElement.GetProperty("messages").GetArrayLength());
        Check(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("routeOptionId"), "map reply contract in instructions");
        return Chat(++call == 1 ? "{\"actionId\":\"map:1\"}" : "{\"actionId\":\"map:1\",\"routeOptionId\":\"route-001\"}");
    }));
    using var client = new ProviderClient(http);
    var frame = Frame() with { State = new { mapPlanningRequired = true, routeOptions = new[] { new { id = "route-001", firstActionId = "map:1" } } } };
    await Throws<ModelProtocolException>(() => client.DecideAsync(Config(), frame, CancellationToken.None));
    Check((await client.DecideAsync(Config(), frame, CancellationToken.None)).RouteOptionId == "route-001", "model owns complete route choice");
    Check(counts.SequenceEqual(new[] { 3, 3 }), "incomplete map proposal stays outside session");
});

Console.WriteLine($"RESULT passed={passed} failed={failures.Count}");
foreach (var failure in failures) Console.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return handler(request, cancellationToken);
    }
}

sealed class BrokenContent : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        => Task.FromException(new IOException("response stream interrupted"));
}
