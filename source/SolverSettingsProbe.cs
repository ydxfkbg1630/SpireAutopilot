using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireAutopilot;

/// <summary>Explicit isolated settings integration acceptance, using the native save/validation implementation.</summary>
internal static class SolverSettingsProbe
{
    internal static async Task RunAsync(AutopilotHost host, string output)
    {
        var checks = new List<object>();
        var original = new Dictionary<string, object?>();
        object? nativeOriginal = null;
        Type? settings = null;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            while (!Walk(host.GetTree().Root).OfType<NMainMenu>().Any())
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            var panel = Walk(host).OfType<SolverParametersPanel>().Single();
            var fields = SolverSettingsAccess.GetFields();
            original = new(SolverSettingsAccess.ReadValues());
            settings = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CombatSolver.SolverSettings")).First(t => t != null)!;
            bool hotKernel = SolverPresentation.UsesHotKernel(settings.Assembly);
            nativeOriginal = settings.GetProperty("Current", flags)!.GetValue(null)!;
            int eligible = nativeOriginal.GetType().GetProperties().Count(p => p.SetMethod != null
                && !p.Name.StartsWith("Legacy") && p.Name != "PerformanceMigrationVersion");
            Check(fields.Count == eligible && panel.FieldCount == eligible, "complete_parameter_coverage");
            Check(fields.All(f => f.Label != f.Name), "all_parameters_have_chinese_labels");
            var tabs = Walk(host).OfType<TabContainer>().Single();
            int solverTab = Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "求解器");
            tabs.CurrentTab = solverTab;
            Check(panel.IsInsideTree(), "solver_tab_attached_to_f8");

            foreach (string name in new[] { "PotionDirectives", "RelicCounterRules", "GrowthBudgets" })
            {
                string json = SolverSettingsAccess.FormatComplex(original[name]);
                var parsed = SolverSettingsAccess.ParseComplex(name, json);
                Check(SolverSettingsAccess.FormatComplex(parsed) == json, "structured_roundtrip_" + name);
            }
            SolverSettingsAccess.Save(new Dictionary<string, object?> { ["SearchMaxDegreeOfParallelism"] = 7 });
            var changed = SolverSettingsAccess.ReadValues();
            Check(Convert.ToInt32(changed["SearchMaxDegreeOfParallelism"]) == 7, "native_worker_save");
            Check(Equals(original["SearchTimeLimitSeconds"], changed["SearchTimeLimitSeconds"])
                && Equals(original[hotKernel ? "MaxCombatSolveSeconds" : "UseNoveltyPortfolio"],
                    changed[hotKernel ? "MaxCombatSolveSeconds" : "UseNoveltyPortfolio"]), "unmodified_search_quality_preserved");
            if (hotKernel)
            {
                var limit = fields.Single(f => f.Name == "MaxCombatSolveSeconds");
                Check(limit.Minimum == 1 && limit.Maximum == 600 && limit.Step == 1, "native_limit_ui_range");
                SolverSettingsAccess.Save(new Dictionary<string, object?> { [limit.Name] = 17 });
                var capture = settings.GetMethod("Capture", flags)!.Invoke(null, null)!;
                Check((int)capture.GetType().GetProperty(limit.Name)!.GetValue(capture)! == 17, "native_solve_limit_save");
            }
            var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(OS.GetUserDataDir(), "combat_solver_settings.json")));
            Check(persisted.RootElement.GetProperty("searchMaxDegreeOfParallelism").GetInt32() == 7, "native_file_persistence");
            bool rejected = false;
            try { SolverSettingsAccess.Save(new Dictionary<string, object?> { ["SearchTimeLimitSeconds"] = 9999d }); }
            catch (Exception) { rejected = true; }
            Check(rejected && Equals(changed["SearchTimeLimitSeconds"], SolverSettingsAccess.ReadValues()["SearchTimeLimitSeconds"]), "native_reject_retains_configuration");
            var presetType = Nullable.GetUnderlyingType(fields.Single(f => f.Name == "PerformancePreset").Type)!;
            var high = Enum.Parse(presetType, "High");
            var expected = settings.GetMethod("ApplyPerformancePreset", flags)!.Invoke(null,
                [settings.GetProperty("Current", flags)!.GetValue(null), high])!;
            SolverSettingsAccess.Save(new Dictionary<string, object?> { ["PerformancePreset"] = high });
            changed = SolverSettingsAccess.ReadValues();
            Check(Equals(changed["SearchBeamWidth"], expected.GetType().GetProperty("SearchBeamWidth")!.GetValue(expected))
                && Equals(changed["SearchMaxExpandedNodes"], expected.GetType().GetProperty("SearchMaxExpandedNodes")!.GetValue(expected)), "native_preset_applies_profile");
            SolverSettingsAccess.Save(new Dictionary<string, object?> { ["SearchBeamWidth"] = 135 });
            Check(SolverSettingsAccess.ReadValues()["PerformancePreset"]!.ToString() == "Custom", "manual_budget_selects_custom");
            SolverSettingsAccess.Save(new Dictionary<string, object?> { ["SearchMaxDegreeOfParallelism"] = null });
            Check(SolverSettingsAccess.ReadValues()["SearchMaxDegreeOfParallelism"] == null, "nullable_preset_value");
            SolverSettingsAccess.ResetDefaults();
            Check(SolverSettingsAccess.ReadValues().Count == eligible, "native_defaults_available");

            settings.GetMethod("Update", flags)!.Invoke(null, [nativeOriginal]);
            panel.Reload();
            var category = Walk(panel).OfType<OptionButton>().First();
            for (int i = 1; i < category.ItemCount; i++)
            {
                category.Select(i); category.EmitSignal(OptionButton.SignalName.ItemSelected, (long)i);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            category.Select(0); category.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
            var filter = Walk(panel).OfType<LineEdit>().First(f => f.PlaceholderText == "搜索名称或字段");
            filter.Text = "SearchBeamWidth"; filter.EmitSignal(LineEdit.SignalName.TextChanged, filter.Text);
            Check(Walk(panel).OfType<SpinBox>().Count() == 1, "search_filter_targets_parameter");
            var input = Walk(panel).OfType<SpinBox>().Single();
            var custom = input.GetParent().GetChildren().OfType<CheckBox>().Single();
            custom.ButtonPressed = true;
            input.Value = 137;
            Check(panel.CommitPending() && Convert.ToInt32(SolverSettingsAccess.ReadValues()["SearchBeamWidth"]) == 137, "ui_numeric_edit_and_apply");
            Check(original.All(pair => pair.Key == "SearchBeamWidth" || pair.Key == "PerformancePreset"
                || SolverSettingsAccess.FormatComplex(pair.Value) == SolverSettingsAccess.FormatComplex(SolverSettingsAccess.ReadValues()[pair.Key])), "ui_saves_only_dirty_parameter");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "parameters.json"), JsonSerializer.Serialize(fields.Select(f => new
                { f.Name, f.Label, f.Group, f.Description, Type = f.Type.Name, f.Minimum, f.Maximum, f.Step,
                    Default = SolverSettingsAccess.FormatComplex(original[f.Name]) }), new JsonSerializerOptions { WriteIndented = true }));
            Write(true, null);
        }
        catch (Exception ex) { Write(false, ex.ToString()); }
        finally
        {
            if (settings != null && nativeOriginal != null) settings.GetMethod("Update", flags)!.Invoke(null, [nativeOriginal]);
            host.GetTree().Quit();
        }
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        void Write(bool success, string? error)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            { success, checks, parameterCount = original.Count, apiCalls = 0, isolatedUserData = OS.GetUserDataDir(), error },
                new JsonSerializerOptions { WriteIndented = true }));
            GD.Print($"[SpireAutopilot/SettingsProbe] success={success} checks={checks.Count} parameters={original.Count}");
        }
    }
    private static IEnumerable<Node> Walk(Node node)
    {
        yield return node;
        foreach (Node child in node.GetChildren()) foreach (var descendant in Walk(child)) yield return descendant;
    }
}
