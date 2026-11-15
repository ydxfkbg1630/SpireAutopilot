using System.Collections;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;

namespace SpireAutopilot;

/// <summary>Opt-in evidence sampler for the real Danmaku fact bus during existing scene fixtures.</summary>
[ModInitializer(nameof(Initialize))]
public static class CompatibilityFactEvidence
{
    public static void Initialize()
    {
        if (System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_COMPAT_FACTS") is not { Length: > 0 } output) return;
        HarmonyLib.Harmony patcher = new("SpireAutopilot.CompatibilityFactEvidence");
        patcher.Patch(HarmonyLib.AccessTools.Method(typeof(NGame), "_Ready"), postfix: new HarmonyLib.HarmonyMethod(typeof(CompatibilityFactEvidence), nameof(Attach)));
    }
    private static void Attach()
    {
        string output = System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_COMPAT_FACTS")!;
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("DanmakuSpire.DanmakuSpireCode.Rules.GameFactBus", false)).FirstOrDefault(t => t != null);
        if (type == null) return;
        type.GetEvent("AuditRecorded")!.AddEventHandler(type.GetProperty("Instance")!.GetValue(null),
            BuildHandler(type.GetEvent("AuditRecorded")!.EventHandlerType!, output));
    }
    private static Delegate BuildHandler(Type handlerType, string output)
    {
        var signature = handlerType.GetMethod("Invoke")!;
        var args = signature.GetParameters().Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var call = System.Linq.Expressions.Expression.Call(typeof(CompatibilityFactEvidence).GetMethod(nameof(Record), BindingFlags.Static | BindingFlags.NonPublic)!,
            System.Linq.Expressions.Expression.Constant(output), System.Linq.Expressions.Expression.Convert(args[0], typeof(object)), System.Linq.Expressions.Expression.Convert(args[1], typeof(object)));
        return System.Linq.Expressions.Expression.Lambda(handlerType, call, args).Compile();
    }
    private static readonly List<object> records = new();
    private static void Record(string output, object audit, object fact)
    {
        records.Add(new { factType = fact.GetType().Name, audit = JsonSerializer.SerializeToElement(audit, audit.GetType()), fact = JsonSerializer.SerializeToElement(fact, fact.GetType()) });
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "danmaku-facts.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }
}
