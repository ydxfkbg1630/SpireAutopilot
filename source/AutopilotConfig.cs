using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SpireAutopilot;

public sealed class AutopilotConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    [JsonIgnore] public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiMode { get; set; } = "Responses";
    public bool PersistentSession { get; set; } = true;
    public int SessionMaxTurns { get; set; } = 12;
    public int SessionMaxInputBytes { get; set; } = 102400;
    public double SessionMaxHistoryRatio { get; set; } = 2.5;
    public bool LocalMechanicalActions { get; set; } = true;
    public string Strategy { get; set; } = "优先生存并完成整局，根据当前牌组、生命、遗物与地图规划路线。";
    public int RequestTimeoutSeconds { get; set; } = 45;
    public double DecisionDelaySeconds { get; set; } = 1;
    public bool AutoCombat { get; set; } = true;
    public string CombatMode { get; set; } = "Hybrid";
    public bool ReflectAfterCombat { get; set; } = true;
    public bool UseBattleExperience { get; set; } = true;
    public int SolverWorkerLimit { get; set; }
    public bool SolverMaxSpeed { get; set; } = true;
    public bool BroadcastMode { get; set; } = true;
    public bool LightweightSolverDiagnostics { get; set; } = true;
    public bool AllowNativeUiFallback { get; set; } = true;
    public double? Temperature { get; set; }
    public bool UseJsonMode { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ThinkingEnabled { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static AutopilotConfig Load(string path)
    {
        if (!File.Exists(path)) return new AutopilotConfig();
        var root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("配置内容为空。");
        var config = root.Deserialize<AutopilotConfig>(JsonOptions) ?? new AutopilotConfig();
        var protectedKey = root["ApiKeyProtected"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(protectedKey)) config.ApiKey = WindowsSecret.Unprotect(protectedKey);
        return config;
    }

    public void Save(string path)
    {
        var root = JsonSerializer.SerializeToNode(this, JsonOptions)!.AsObject();
        root["ApiKeyProtected"] = string.IsNullOrEmpty(ApiKey) ? "" : WindowsSecret.Protect(ApiKey);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString(JsonOptions), new UTF8Encoding(false));
            File.Move(temp, fullPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// DPAPI binds the encrypted credential to the current Windows user. Keys never enter JSON in plain text.
internal static class WindowsSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);

    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value)
    {
        var bytes = Transform(Convert.FromBase64String(value), false);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("API 凭据保存使用 Windows DPAPI。请在 Windows 中配置。 ");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect
                ? CryptProtectData(ref input, "SpireAutopilot", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows 凭据加密操作失败。请在原 Windows 账户中重新填写 API Key。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
}
