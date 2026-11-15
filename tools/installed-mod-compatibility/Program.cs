using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

string gameData = args[0], output = args[1];
Directory.CreateDirectory(output);
var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(gameData);
using var game = ModuleDefinition.ReadModule(Path.Combine(gameData, "sts2.dll"), new ReaderParameters { AssemblyResolver = resolver });
var characterButton = Find(game, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton");
var characterGetter = characterButton.Methods.Single(m => m.Name == "get_Character");
var reports = new List<object>();
foreach (string file in args.Skip(2))
{
    var accepted = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DanmakuSpire.dll"] = "8A0681D393D5281E01B183229367C507146A99C09D4961A7C3ADD382DC34FCF2",
        ["P3Makoto.dll"] = "86455AA628A11F436EEF99E2F9F10F60E44B5CFCB41D5DEFA54CA57C5A975F35",
        ["NecrobinderOstyVaalmonicaMod.dll"] = "AE3856C0DF495A7240095BC52DE4ECDC848093864DBB3084797A981B48C5921F",
        ["UnifiedSavePath.dll"] = "08B7CBB36C7B48820E44C060D2DC6B6C49EDBB945C7CB1E8F7A44866F7C650CC"
        ,["ItsLoading.dll"] = "380F832B3EB672BF09E0B1216EDB5C944F08C2F00C749B3EC38D5D360A3A1D5A"
    };
    string inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    if (!accepted.TryGetValue(Path.GetFileName(file), out var expected) || expected != inputHash)
        throw new InvalidDataException("Input DLL version/hash requires a fresh compatibility review: " + Path.GetFileName(file));
    resolver.AddSearchDirectory(Path.GetDirectoryName(file)!);
    using var module = ModuleDefinition.ReadModule(file, new ReaderParameters { AssemblyResolver = resolver });
    var referenceRepairs = new List<string>();
    foreach (var method in All(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray())
        foreach (var instruction in method.Body.Instructions.ToArray())
            if (instruction.Operand is MethodReference called && called is not GenericInstanceMethod && !called.ContainsGenericParameter && called.DeclaringType.Scope.Name == "sts2")
            {
                var currentType = All(game.Types).SingleOrDefault(t => t.FullName == called.DeclaringType.FullName);
                if (currentType == null) continue;
                var matches = currentType.Methods.Where(m => m.Name == called.Name && m.Parameters.Count == called.Parameters.Count
                    && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(called.Parameters.Select(p => p.ParameterType.FullName))).ToArray();
                if (matches.Length != 1 || matches[0].ReturnType.FullName == called.ReturnType.FullName) continue;
                // This reviewed drift narrows IEnumerable<MerchantEntry> to List<MerchantEntry>.
                if (called.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory" && called.Name == "get_AllEntries")
                { instruction.Operand = module.ImportReference(matches[0]); referenceRepairs.Add(called.FullName + " -> " + matches[0].FullName); }
                else if (called.ReturnType.FullName == "System.Void" && called.DeclaringType.FullName == "MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager"
                    && called.Name is "RemoveHotkeyPressedBinding" or "RemoveHotkeyReleasedBinding" && matches[0].ReturnType.FullName == "System.Int64")
                {
                    instruction.Operand = module.ImportReference(matches[0]);
                    method.Body.GetILProcessor().InsertAfter(instruction, Instruction.Create(OpCodes.Pop));
                    referenceRepairs.Add(called.FullName + " -> " + matches[0].FullName + " (discard new result)");
                }
                else if (module.Name == "NecrobinderOstyVaalmonicaMod.dll" && called.DeclaringType.FullName == "MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaAnimationState"
                    && called.Name == "SetAnimation" && matches[0].ReturnType.FullName == "System.Void")
                {
                    // Preserve access to the newly current entry; dispose it after the original animation helper returns.
                    var stateType = matches[0].DeclaringType; var current = stateType.Methods.Single(m => m.Name == "GetCurrent");
                    var helper = new MethodDefinition("SetAnimationTrackedV0113", MethodAttributes.Private | MethodAttributes.Static, module.ImportReference(current.ReturnType));
                    helper.Parameters.Add(new ParameterDefinition("state", ParameterAttributes.None, module.ImportReference(stateType)));
                    foreach (var p in called.Parameters) helper.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, module.ImportReference(p.ParameterType)));
                    method.DeclaringType.Methods.Add(helper); var il = helper.Body.GetILProcessor();
                    for (int arg = 0; arg < 4; arg++) il.Emit(OpCodes.Ldarg, helper.Parameters[arg]);
                    il.Emit(OpCodes.Callvirt, module.ImportReference(matches[0])); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Callvirt, module.ImportReference(current)); il.Emit(OpCodes.Ret);
                    instruction.OpCode = OpCodes.Call; instruction.Operand = helper;
                    var store = instruction.Next;
                    VariableDefinition entry = store.OpCode.Code switch { Code.Stloc => (VariableDefinition)store.Operand, Code.Stloc_S => (VariableDefinition)store.Operand,
                        Code.Stloc_0 => method.Body.Variables[0], Code.Stloc_1 => method.Body.Variables[1], Code.Stloc_2 => method.Body.Variables[2], Code.Stloc_3 => method.Body.Variables[3], _ => throw new InvalidDataException("Animation track local shape changed.") };
                    var dispose = Find(game, "MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSpineBinding").Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 0);
                    foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
                    {
                        var first = Instruction.Create(OpCodes.Ldloc, entry); ret.OpCode = first.OpCode; ret.Operand = first.Operand;
                        var skip = Instruction.Create(OpCodes.Ret); var cursor = ret;
                        foreach (var added in new[] { Instruction.Create(OpCodes.Brfalse, skip), Instruction.Create(OpCodes.Ldloc, entry), Instruction.Create(OpCodes.Callvirt, module.ImportReference(dispose)), skip })
                        { method.Body.GetILProcessor().InsertAfter(cursor, added); cursor = added; }
                    }
                    referenceRepairs.Add(called.FullName + " -> SetAnimation/GetCurrent; track disposed on calling thread");
                }
                else throw new InvalidDataException("Unreviewed native return-type drift: " + module.Name + " " + called.FullName + " -> " + matches[0].FullName);
            }
    int wrappers = 0, shop = 0, damage = 0, savePath = 0, loading = 0;
    foreach (var type in All(module.Types).ToArray())
    {
        bool knownCharacterPatch = type.FullName is "DanmakuSpire.DanmakuSpireCode.GameIntegration.Rules.CharacterFactPatches/CharacterPreselectedPatch"
            or "P3Makoto.Patches.CharSelectInfoPatch" or "NecrobinderOstyVaalmonicaMod.Scripts.Entry/CharacterSelectScreenPatch";
        var old = knownCharacterPatch ? type.Methods.SingleOrDefault(m => m.Name == "Postfix" && m.Parameters.Any(p => p.Name == "characterModel")) : null;
        if (old != null && type.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
        {
            old.Name = "CompatibilityOriginalPostfix";
            var wrapper = new MethodDefinition("Postfix", old.Attributes, module.TypeSystem.Void);
            foreach (var parameter in old.Parameters.Where(p => p.Name != "characterModel"))
                wrapper.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
            var button = wrapper.Parameters.SingleOrDefault(p => p.Name == "charSelectButton");
            if (button == null)
            { button = new ParameterDefinition("__0", ParameterAttributes.None, module.ImportReference(characterButton)); wrapper.Parameters.Add(button); }
            var il = wrapper.Body.GetILProcessor();
            foreach (var parameter in old.Parameters)
            {
                if (parameter.Name == "characterModel") { il.Emit(OpCodes.Ldarg, button); il.Emit(OpCodes.Callvirt, module.ImportReference(characterGetter)); }
                else il.Emit(OpCodes.Ldarg, wrapper.Parameters.Single(p => p.Name == parameter.Name));
            }
            il.Emit(OpCodes.Call, old); il.Emit(OpCodes.Ret); type.Methods.Add(wrapper); wrappers++;
        }
        if (type.FullName.EndsWith("NonCombatFactPatches/ShopPurchasedPatch", StringComparison.Ordinal))
            foreach (var attribute in type.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
                for (int i = 0; i < attribute.ConstructorArguments.Count; i++)
                    if (attribute.ConstructorArguments[i].Value as string == "OnTryPurchaseWrapper")
                    { attribute.ConstructorArguments[i] = new CustomAttributeArgument(module.TypeSystem.String, "OnTryPurchaseUnsynchronized"); shop++; }
        if (type.FullName.EndsWith("NonCombatFactPatches/ShopCardRemovalPurchasedPatch", StringComparison.Ordinal))
        {
            foreach (var attribute in type.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch").ToArray()) type.CustomAttributes.Remove(attribute);
        }
    }
    if (module.Name == "DanmakuSpire.dll")
    {
        var owner = All(module.Types).Single(t => t.Name == "NonCombatFactPatches");
        var wrap = owner.Methods.Single(m => m.Name == "WrapPurchase");
        var attempt = owner.NestedTypes.Single(t => t.Name == "ShopPurchaseAttempt");
        var item = attempt.Methods.Single(m => m.Name == "get_Item");
        var kind = item.ReturnType.Resolve().Methods.Single(m => m.Name == "get_Kind");
        var boolLoad = wrap.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldarg_3);
        var il = wrap.Body.GetILProcessor();
        // The unified v0.113 purchase entry runs once for both purchases and card removal.
        il.InsertBefore(boolLoad, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(boolLoad, il.Create(OpCodes.Callvirt, item));
        il.InsertBefore(boolLoad, il.Create(OpCodes.Callvirt, module.ImportReference(kind)));
        il.InsertBefore(boolLoad, il.Create(OpCodes.Ldstr, "MerchantCardRemovalEntry"));
        il.InsertBefore(boolLoad, il.Create(OpCodes.Call, module.ImportReference(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) })!)));
        il.InsertAfter(boolLoad, il.Create(OpCodes.Or));
    }
    if (module.Name == "P3Makoto.dll")
    {
        var sig = All(module.Types).Single(t => t.FullName == "P3Makoto.Patches.Sig");
        var original = sig.Methods.Single(m => m.Name == "Params");
        var helper = new MethodDefinition("DamageParamsV0113", MethodAttributes.Assembly | MethodAttributes.Static, module.TypeSystem.Boolean);
        foreach (var p in original.Parameters) helper.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        sig.Methods.Add(helper);
        var il = helper.Body.GetILProcessor();
        // Append only the newly added optional final parameter, then use the original exact matcher.
        var arrayVar = new VariableDefinition(new ArrayType(module.TypeSystem.String)); helper.Body.Variables.Add(arrayVar); helper.Body.InitLocals = true;
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Newarr, module.TypeSystem.String); il.Emit(OpCodes.Stloc, arrayVar);
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, arrayVar); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Call, module.ImportReference(typeof(Array).GetMethod("Copy", new[] { typeof(Array), typeof(Array), typeof(int) })!));
        il.Emit(OpCodes.Ldloc, arrayVar); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ldstr, "modifyDamageHookType"); il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, arrayVar); il.Emit(OpCodes.Call, original); il.Emit(OpCodes.Ret);
        foreach (var type in All(module.Types).Where(t => t.FullName.Contains("DamagePatch", StringComparison.Ordinal)))
            foreach (var method in type.Methods.Where(m => m.HasBody))
                foreach (var instruction in method.Body.Instructions)
                    if (instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference called && called.FullName == original.FullName)
                    { instruction.Operand = helper; damage++; }
    }
    if (module.Name == "UnifiedSavePath.dll")
    {
        var type = Find(module, "UnifiedSavePath.PatchGetProfileDir");
        var attribute = type.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
        var harmonyType = attribute.AttributeType.Resolve();
        var constructor = harmonyType.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 3
            && m.Parameters[0].ParameterType.FullName == "System.Type" && m.Parameters[1].ParameterType.FullName == "System.String"
            && m.Parameters[2].ParameterType.FullName == "System.Type[]");
        var replacement = new CustomAttribute(module.ImportReference(constructor));
        replacement.ConstructorArguments.Add(attribute.ConstructorArguments[0]);
        replacement.ConstructorArguments.Add(attribute.ConstructorArguments[1]);
        var systemType = module.ImportReference(typeof(Type));
        replacement.ConstructorArguments.Add(new CustomAttributeArgument(new ArrayType(systemType),
            new[] { new CustomAttributeArgument(systemType, module.TypeSystem.Int32) }));
        type.CustomAttributes.Remove(attribute); type.CustomAttributes.Add(replacement); savePath++;
    }
    if (module.Name == "ItsLoading.dll")
    {
        using var godot = ModuleDefinition.ReadModule(Path.Combine(gameData, "GodotSharp.dll"), new ReaderParameters { AssemblyResolver = resolver });
        var godotObject = Find(godot, "Godot.GodotObject"); var variant = Find(godot, "Godot.Variant"); var stringName = Find(godot, "Godot.StringName");
        var bridge = Find(module, "ItsLoading.GdBridgeBar"); var original = bridge.Methods.Single(m => m.Name == "InstantiateFresh");
        var attach = original.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "AddChild");
        var add = (MethodReference)attach.Operand;
        var helper = new MethodDefinition("AttachAfterRootReady", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        helper.Parameters.Add(new ParameterDefinition("parent", ParameterAttributes.None, add.DeclaringType));
        foreach (var p in add.Parameters) helper.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        bridge.Methods.Add(helper); var il = helper.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, "add_child");
        il.Emit(OpCodes.Call, module.ImportReference(stringName.Methods.Single(m => m.Name == "op_Implicit" && m.Parameters[0].ParameterType.FullName == "System.String")));
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Newarr, module.ImportReference(variant)); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, module.ImportReference(variant.Methods.Single(m => m.Name == "op_Implicit" && m.Parameters[0].ParameterType.FullName == "Godot.GodotObject")));
        il.Emit(OpCodes.Stelem_Any, module.ImportReference(variant));
        il.Emit(OpCodes.Callvirt, module.ImportReference(godotObject.Methods.Single(m => m.Name == "CallDeferred" && m.Parameters[1].ParameterType is ArrayType)));
        il.Emit(OpCodes.Pop); il.Emit(OpCodes.Ret);
        attach.OpCode = OpCodes.Call; attach.Operand = helper; loading++;
    }
    string target = Path.Combine(output, module.Name); module.Write(target);
    reports.Add(new { file = module.Name, wrappers, shop, damage, savePath, loading, referenceRepairs, original = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), patched = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))) });
}
File.WriteAllText(Path.Combine(output, "patch-report.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
static TypeDefinition Find(ModuleDefinition module, string name) => All(module.Types).Single(t => t.FullName == name);
static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types) { foreach (var t in types) { yield return t; foreach (var n in All(t.NestedTypes)) yield return n; } }
