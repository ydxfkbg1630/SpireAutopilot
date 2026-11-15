using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;

namespace SpireAutopilot;

/// <summary>Opt-in, main-menu-only compatibility inventory. No game action or HTTP request.</summary>
[ModInitializer(nameof(Initialize))]
public static class InstalledModCompatibilityProbe
{
    public static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_MOD_COMPAT_PROBE"))) return;
        new Harmony("SpireAutopilot.InstalledCompatibilityProbe").Patch(AccessTools.Method(typeof(NGame), "_Ready"),
            postfix: new HarmonyMethod(typeof(InstalledModCompatibilityProbe), nameof(Ready)));
    }
    private static void Ready(NGame __instance) => _ = Capture(__instance);
    private static async Task Capture(NGame game)
    {
        string output = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_MOD_COMPAT_PROBE")!;
        Directory.CreateDirectory(output);
        try
        {
            double started = Time.GetTicksMsec();
            while (!Walk(game.GetTree().Root).OfType<NMainMenu>().Any(n => n.IsNodeReady()))
            {
                if (Time.GetTicksMsec() - started > 60000) throw new TimeoutException("Main-menu compatibility fixture exceeded 60 seconds.");
                await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            for (int i = 0; i < 30; i++) await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            var patches = Harmony.GetAllPatchedMethods().Select(method => new
            {
                target = method.DeclaringType?.FullName + "." + method.Name,
                parameters = method.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name).ToArray(),
                hooks = Harmony.GetPatchInfo(method) is { } info ? info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                    .Select(p => new { owner = p.owner, patch = p.PatchMethod.DeclaringType?.FullName + "." + p.PatchMethod.Name, p.priority }).ToArray() : null
            }).OrderBy(p => p.target).ToArray();
            File.WriteAllText(Path.Combine(output, "harmony.json"), JsonSerializer.Serialize(patches, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                success = true, providerApiCalls = 0, gameActions = 0, scope = "Current enabled mod set, native main menu and Harmony entry points only; combat untested by this fixture.",
                assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => new { name = a.GetName().Name, version = a.GetName().Version?.ToString() }).OrderBy(a => a.name),
                paths = new { UserDataPathProvider.IsRunningModded,
                    methods = typeof(UserDataPathProvider).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(m => m.Name == "GetProfileDir").Select(m => new { signature = m.ToString(),
                            value = m.Invoke(null, m.GetParameters().Length == 1 ? new object?[] { 1 } : new object?[] { 1, null }),
                            forceTrue = m.GetParameters().Length == 2 ? m.Invoke(null, new object?[] { 1, true }) : null,
                            forceFalse = m.GetParameters().Length == 2 ? m.Invoke(null, new object?[] { 1, false }) : null }).ToArray() },
                patchedTargets = patches.Length, elapsedSeconds = (Time.GetTicksMsec() - started) / 1000d
            }, new JsonSerializerOptions { WriteIndented = true }));
            game.GetTree().Quit();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); game.GetTree().Quit(1);
        }
    }
    private static IEnumerable<Node> Walk(Node node) { yield return node; foreach (var child in node.GetChildren()) foreach (var descendant in Walk(child)) yield return descendant; }
}
