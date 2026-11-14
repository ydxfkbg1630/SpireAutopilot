using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;

namespace SpireAutopilot;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    private static AutopilotHost? host;
    public static void Initialize()
    {
        new Harmony("local.spire-autopilot").Patch(
            AccessTools.Method(typeof(NGame), "_Ready"),
            postfix: new HarmonyMethod(typeof(Entry), nameof(Attach)));
        if (NGame.Instance is { } game) Attach(game);
        GD.Print("[SpireAutopilot] Initialized. F8 settings; F9 start/pause.");
    }

    private static void Attach(NGame __instance)
    {
        if (GodotObject.IsInstanceValid(host)) return;
        host = new AutopilotHost { Name = "SpireAutopilotHost", ProcessMode = Node.ProcessModeEnum.Always };
        __instance.AddChild(host);
    }
}
