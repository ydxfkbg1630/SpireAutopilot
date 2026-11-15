using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SpireAutopilot;

if (args.Length != 4) { Console.WriteLine("SingleDecisionProbe configPath framePath outputPath useJsonMode"); return 2; }
AutopilotConfig? config = null;
string Safe(string text) => string.IsNullOrEmpty(config?.ApiKey) ? text : text.Replace(config.ApiKey, "[REDACTED]", StringComparison.Ordinal);
var report = new Dictionary<string, object?>();
var timer = Stopwatch.StartNew();
var exit = 0;
try
{
    config = AutopilotConfig.Load(args[0]);
    config.UseJsonMode = bool.Parse(args[3]);
    config.RequestTimeoutSeconds = 90;
    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
    var root = doc.RootElement;
    if (root.TryGetProperty("frame", out var nested)) root = nested;
    var frame = root.Deserialize<DecisionFrame>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    using var handler = new CapturingHandler(text =>
    {
        using var response = JsonDocument.Parse(text);
        var node = response.RootElement;
        if (node.TryGetProperty("usage", out var usage)) report["nativeUsage"] = usage.Clone();
        if (node.TryGetProperty("output", out var items) && items.ValueKind == JsonValueKind.Array)
            report["outputItemTypes"] = items.EnumerateArray().Select(item => item.TryGetProperty("type", out var type) ? type.GetString() : null).ToArray();
        // Native reasoning text stays private. Preserve usage and output types as thinking-mode evidence.
    });
    using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    using var provider = new ProviderClient(http);
    timer.Restart();
    var result = await provider.DecideAsync(config, frame, CancellationToken.None);
    report["actionId"] = Safe(result.ActionId);
    report["reason"] = Safe(result.Reason);
    report["routeOptionId"] = result.RouteOptionId;
    report["routePlan"] = result.RoutePlan;
    report["usage"] = result.Usage;
    report["success"] = true;
}
catch (Exception ex) { report["success"] = false; report["error"] = Safe(ex.Message); exit = 1; }
report["elapsedMs"] = timer.ElapsedMilliseconds;
report["useJsonMode"] = config?.UseJsonMode;
var output = Safe(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
var path = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
await File.WriteAllTextAsync(path, output, new UTF8Encoding(false));
Console.WriteLine(JsonSerializer.Serialize(new { success = report.GetValueOrDefault("success"), elapsedMs = timer.ElapsedMilliseconds, useJsonMode = config?.UseJsonMode }));
return exit;

sealed class CapturingHandler(Action<string> record) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        record(text.Length > 2 * 1024 * 1024 ? text[..(2 * 1024 * 1024)] : text);
        return response;
    }
}
