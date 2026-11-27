using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SpireAutopilot;

internal static class ClaudeCodeTransport
{
    private const int MaximumCliOutput = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static bool IsBalanceError(string message) =>
        new[] { "预扣费额度", "余额不足", "额度不足", "insufficient balance", "credit balance", "insufficient_quota" }
            .Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));

    internal static (string System, string Prompt) PromptParts(Dictionary<string, object?> request)
    {
        var input = JsonSerializer.SerializeToElement(request["input"]);
        var messages = input.EnumerateArray()
            .Where(item => item.TryGetProperty("role", out var role) && role.GetString() is "user" or "assistant")
            .ToArray();
        string system = (string)request["instructions"]! + " Return exactly one JSON object.";
        if (messages.All(item => item.GetProperty("role").GetString() == "user" && item.GetProperty("content").ValueKind == JsonValueKind.String))
            return (system, string.Join('\n', messages.Select(item => item.GetProperty("content").GetString())));
        return (system + " Treat the supplied JSON array as conversation history in chronological order. Answer the final user message.",
            JsonSerializer.Serialize(messages));
    }

    internal static async Task<HttpResponseMessage> SendAsync(Uri endpoint, AutopilotConfig config,
        Dictionary<string, object?> request, string scene, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey)) throw new ProviderConfigurationException("请填写 Claude Code 网关的 API Key。");
        if (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
            throw new ProviderConfigurationException("Claude Code 网关请填写 HTTPS 地址。");
        string path = endpoint.AbsolutePath[..^"/responses".Length].TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^3];
        var gateway = new UriBuilder(endpoint) { Path = path }.Uri.AbsoluteUri.TrimEnd('/');
        var (system, prompt) = PromptParts(request);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpireAutopilot", "ClaudeCode");
        string workdir = Path.Combine(root, "work");
        Directory.CreateDirectory(workdir);
        var officialInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        var start = new ProcessStartInfo
        {
            FileName = File.Exists(officialInstall) ? officialInstall : "claude.exe",
            WorkingDirectory = workdir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
        };
        foreach (var argument in new[] { "-p", "--bare", "--model", config.Model, "--output-format", "json", "--strict-mcp-config",
                     "--tools", "", "--disallowedTools", "*", "--system-prompt", system, "--no-session-persistence" })
            start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase) || key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["ANTHROPIC_BASE_URL"] = gateway;
        start.Environment["ANTHROPIC_API_KEY"] = config.ApiKey;
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(root, "config");
        start.Environment["DISABLE_TELEMETRY"] = "1";
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        start.Environment["CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS"] = "1";
        start.Environment["DISABLE_AUTOUPDATER"] = "1";
        start.Environment["CLAUDE_CODE_MAX_RETRIES"] = "0";
        start.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = scene == "RunReflection" ? "8192" : "1536";
        if (config.ThinkingEnabled is { } thinking)
            start.Environment["CLAUDE_CODE_EFFORT_LEVEL"] = thinking ? "high" : "low";

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception) { throw new ProviderConfigurationException("请安装 Windows 版 Claude Code，再测试此连接。"); }
        // Drain both pipes while sending input. Output is limited and never printed to game logs.
        var result = ReadBoundedAsync(process.StandardOutput, ct);
        var stderr = ReadBoundedAsync(process.StandardError, ct);
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await result.ConfigureAwait(false);
            var errorOutput = await stderr.ConfigureAwait(false);
            JsonDocument document;
            try { document = JsonDocument.Parse(output); }
            catch (JsonException)
            { throw ClassifyError(errorOutput); }
            using (document)
            {
                var data = document.RootElement;
                string text = ReadString(data, "result") ?? "";
                if (process.ExitCode != 0 || ReadString(data, "subtype") != "success"
                    || (data.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True))
                    throw ClassifyError(text + "\n" + errorOutput);
                if (!data.TryGetProperty("modelUsage", out var models) || models.ValueKind != JsonValueKind.Object
                    || models.EnumerateObject().Any(model => model.Name != config.Model) || !models.TryGetProperty(config.Model, out _))
                    throw new ModelProtocolException("Claude Code 返回的模型与配置不符。");
                var stop = ReadString(data, "stop_reason");
                if (stop == "max_tokens" || string.IsNullOrWhiteSpace(text))
                    throw new ModelProtocolException("Claude Code 返回的决策不完整。");
                data.TryGetProperty("usage", out var usage);
                long Tokens(string name) => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long tokens) ? Math.Clamp(tokens, 0, 10_000_000) : 0;
                long cached = Tokens("cache_read_input_tokens");
                var response = new
                {
                    status = "completed",
                    output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } },
                    usage = new { input_tokens = Tokens("input_tokens") + Tokens("cache_creation_input_tokens") + cached,
                        input_tokens_details = new { cached_tokens = cached }, output_tokens = Tokens("output_tokens") }
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Utf8, "application/json") };
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }
    }

    private static string? ReadString(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var content = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (content.Length < MaximumCliOutput) content.Append(buffer, 0, Math.Min(count, MaximumCliOutput - content.Length));
        }
        return content.ToString();
    }

    internal static Exception ClassifyError(string message)
    {
        if (IsBalanceError(message)) return new ProviderConfigurationException("Claude 网关余额低于请求预扣额。请补充余额后继续。");
        if (message.Contains("429", StringComparison.Ordinal) || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return new ProviderTransientException("Claude 接口限流。", 429);
        if (message.Contains("403", StringComparison.Ordinal) || message.Contains("401", StringComparison.Ordinal)
            || message.Contains("failed to authenticate", StringComparison.OrdinalIgnoreCase))
            return new ProviderConfigurationException("Claude 网关鉴权失败。请核对地址、API Key 和模型权限。");
        return new ProviderTransientException("Claude Code 请求中断或网关暂时不可用。", 502);
    }
}
