using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SpireAutopilot;

// Explicitly invoked live probe: one connection POST, followed by one decision POST if a frame is supplied.
// Usage: dotnet run --project LiveProviderProbe.csproj -- configPath [framePath|-] [outputPath]
if (args.Length is < 1 or > 3)
{
    Console.Error.WriteLine("Usage: LiveProviderProbe configPath [framePath|-] [outputPath]");
    return 2;
}

var report = new Dictionary<string, object?>();
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
AutopilotConfig? config = null;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var timer = Stopwatch.StartNew();
var exitCode = 0;
string Safe(string value) => config is { ApiKey.Length: > 0 } ? value.Replace(config.ApiKey, "[REDACTED]", StringComparison.Ordinal) : value;
try
{
    if (!File.Exists(args[0])) throw new InvalidOperationException("配置文件路径对应的文件需要先创建。");
    config = AutopilotConfig.Load(args[0]);
    var ephemeralKey = Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_LIVE_KEY");
    if (!string.IsNullOrWhiteSpace(ephemeralKey)) config.ApiKey = ephemeralKey;
    using var provider = new ProviderClient();
    timer.Restart();
    var connection = await provider.TestAsync(config, cancellation.Token);
    report["connection"] = new { status = Safe(connection), elapsedMs = timer.ElapsedMilliseconds };
    if (args.Length >= 2 && args[1] != "-" && !string.IsNullOrWhiteSpace(args[1]))
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(args[1], cancellation.Token));
        var root = doc.RootElement;
        // Accept both a plain DecisionFrame and the game's diagnostic {frame: ...} envelope.
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in root.EnumerateObject())
                if (string.Equals(property.Name, "frame", StringComparison.OrdinalIgnoreCase)) { root = property.Value; break; }
        var frame = root.Deserialize<DecisionFrame>(jsonOptions) ?? throw new InvalidOperationException("frame JSON 需要包含 DecisionFrame。");
        timer.Restart();
        var decision = await provider.DecideAsync(config, frame, cancellation.Token);
        report["decision"] = new { actionId = Safe(decision.ActionId), reason = Safe(decision.Reason), elapsedMs = timer.ElapsedMilliseconds };
    }
}
catch (Exception ex)
{
    exitCode = 1;
    report["error"] = new { type = ex.GetType().Name, message = Safe(ex.Message), elapsedMs = timer.ElapsedMilliseconds };
}

// Apply redaction after serialization as a final defense for every output field.
var output = Safe(JsonSerializer.Serialize(report, jsonOptions));
if (args.Length == 3)
{
    try
    {
        var outputPath = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, output, new UTF8Encoding(false));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Output write: " + Safe(ex.GetType().Name));
        exitCode = 1;
    }
}
Console.WriteLine(output);
return exitCode;
