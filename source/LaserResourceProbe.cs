using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireAutopilot;

/// <summary>Explicit native resource loading and instantiation check for the repaired author laser assets.</summary>
[ModInitializer(nameof(Initialize))]
public static class LaserResourceProbe
{
    public static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_LASER_RESOURCE_PROBE"))) return;
        new Harmony("SpireAutopilot.LaserResourceProbe").Patch(AccessTools.Method(typeof(NGame), "_Ready"), postfix: new HarmonyMethod(typeof(LaserResourceProbe), nameof(Ready)));
    }
    private static void Ready(NGame __instance) => _ = Run(__instance);
    private static async Task Run(NGame game)
    {
        string output = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_LASER_RESOURCE_PROBE")!;
        Directory.CreateDirectory(output); var assertions = new List<object>(); var retained = new List<Resource>();
        try
        {
            ulong deadline = Time.GetTicksMsec() + 60000;
            while (!Walk(game.GetTree().Root).OfType<NMainMenu>().Any(n => n.IsNodeReady()))
            { if (Time.GetTicksMsec() >= deadline) throw new TimeoutException("Laser resource main menu deadline."); await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame); }
            foreach (var (path, type) in new[]
            {
                ("res://BetterAnimation2/spine/laser.atlas", "SpineAtlasResource"),
                ("res://BetterAnimation2/spine/laser.skel", "SpineSkeletonFileResource"),
                ("res://BetterAnimation2/spine/laser_skel_data.tres", "SpineSkeletonDataResource")
            })
            {
                var resource = ResourceLoader.Load(path, type, ResourceLoader.CacheMode.Ignore);
                if (resource == null || !resource.IsClass(type)) throw new InvalidOperationException("Native resource load failed: " + path);
                retained.Add(resource); assertions.Add(new { path, expectedType = type, actualType = resource.GetClass().ToString(), passed = true });
            }
            foreach (string path in new[] { "res://BetterAnimation2/scene/laser.tscn", "res://BetterAnimation2/scene/laser_vfx.tscn", "res://BetterAnimation2/scene/cubex_laser_rings.tscn" })
            {
                var scene = ResourceLoader.Load<PackedScene>(path, cacheMode: ResourceLoader.CacheMode.Ignore) ?? throw new InvalidOperationException("Packed laser scene load failed: " + path);
                retained.Add(scene); var instance = scene.Instantiate();
                if (instance == null) throw new InvalidOperationException("Laser scene instantiation failed: " + path);
                assertions.Add(new { path, actualType = instance.GetType().Name, nodes = Walk(instance).Count(), passed = true }); instance.Free();
            }
            Write(new { success = true, providerApiCalls = 0, gameActions = 0, nativeResourceLoads = true, assertions });
        }
        catch (Exception ex) { Write(new { success = false, providerApiCalls = 0, gameActions = 0, assertions, failure = ex.ToString() }); }
        finally { GC.KeepAlive(retained); game.GetTree().Quit(); }
        void Write(object value) => File.WriteAllText(Path.Combine(output, "laser-resource.json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static IEnumerable<Node> Walk(Node root) { yield return root; foreach (var child in root.GetChildren()) foreach (var item in Walk(child)) yield return item; }
}
