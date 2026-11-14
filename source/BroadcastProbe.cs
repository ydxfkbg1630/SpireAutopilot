using System.Reflection;
using System.Text.Json;
using Godot;

namespace SpireAutopilot;

/// <summary>Isolated native UI lifecycle assertions. Uses synthetic content and no provider/gameplay actions.</summary>
public static class BroadcastProbe
{
    public static async Task<object> RunAsync(AutopilotHost host, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE")))
            throw new InvalidOperationException("Broadcast assertions require the isolated native probe environment.");
        var results = new List<object>();
        var layouts = new List<object>();
        SubViewport? viewport = null;
        try
        {
            foreach (var resolution in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                viewport = new SubViewport { Name = "BroadcastProbeViewport", Size = resolution, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
                host.AddChild(viewport);
                var panel = new AutopilotPanel(); viewport.AddChild(panel);
                panel.Initialize(new AutopilotConfig { ApiKey = "synthetic-mask-marker", Model = "synthetic-probe-model" }, _ => { }, () => { }, () => Task.CompletedTask);
                panel.ShowMonitor();
                await Settle();
                var monitor = Walk(panel).OfType<AutopilotMonitor>().Single();
                var prefix = resolution.Y + "p_";
                Current(panel, "model", "地图决策", "model-A");
                panel.SetPlan("OLD_ROUTE_SENTINEL"); Details(panel, "OLD_REASON_SENTINEL"); Metrics(panel, "OLD_METRIC_SENTINEL");
                await Settle();
                Assert(prefix + "model_A_present", VisibleText(monitor).Contains("OLD_REASON_SENTINEL"), new { owner = "model-A" });
                Current(panel, "model", "奖励决策", "model-B");
                await Settle();
                Assert(prefix + "model_B_clears_previous", !VisibleText(monitor).Contains("OLD_REASON_SENTINEL") && !VisibleText(monitor).Contains("OLD_ROUTE_SENTINEL") && !VisibleText(monitor).Contains("OLD_METRIC_SENTINEL"), new { text = VisibleText(monitor) });
                Details(panel, "MODEL_REASON_SENTINEL");
                Current(panel, "combat", "搜索", "battle-1-turn-1-search");
                Solver(panel, "search", "搜索", 1.5, 1234567, 15, 0, 0, "", "SEARCH_ONLY_SENTINEL");
                await Settle();
                Assert(prefix + "search_clears_model", !VisibleText(monitor).Contains("MODEL_REASON_SENTINEL") && VisibleText(monitor).Contains("SEARCH_ONLY_SENTINEL"), new { text = VisibleText(monitor) });
                Current(panel, "combat", "出牌", "battle-1-turn-1-deploy");
                Solver(panel, "deployment", "出牌", .4, 0, 15, 1, 3, "CURRENT_CARD_SENTINEL", "");
                await Settle();
                var deployingText = VisibleText(monitor);
                Assert(prefix + "deploy_clears_search", !deployingText.Contains("SEARCH_ONLY_SENTINEL") && !deployingText.Contains("1234567") && !deployingText.Contains("1,234,567") && deployingText.Contains("CURRENT_CARD_SENTINEL"), new { text = deployingText });
                var viewportRect = new Rect2(Vector2.Zero, resolution);
                Assert(prefix + "compact_inside_viewport", viewportRect.Encloses(monitor.GetGlobalRect()) && monitor.Size.Y <= 300, new { rect = Rect(monitor.GetGlobalRect()), screen = new { width = resolution.X, height = resolution.Y } });
                var body = Field<PanelContainer>(panel, "body");
                var key = Field<LineEdit>(panel, "key");
                panel.ToggleVisible(); await Settle();
                Assert(prefix + "F8_settings_key_mask", body.Visible && key.Secret && key.Text == "synthetic-mask-marker", new { visible = body.Visible, secretMask = key.Secret });
                Assert(prefix + "settings_inside_viewport", viewportRect.Encloses(body.GetGlobalRect()), new { rect = Rect(body.GetGlobalRect()) });
                panel.ToggleVisible();
                bool prior = monitor.Visible; panel.ToggleMonitor(); bool hidden = !monitor.Visible; panel.ToggleMonitor();
                Assert(prefix + "F10_toggle", prior && hidden && monitor.Visible, new { prior, hidden, shown = monitor.Visible });
                var fold = Walk(monitor).OfType<Button>().Single(b => b.Text is "收起" or "展开");
                float expandedHeight = monitor.Size.Y;
                fold.EmitSignal(BaseButton.SignalName.Pressed); await Settle();
                Assert(prefix + "fold_compacts", monitor.Size.Y < expandedHeight, new { expandedHeight, foldedHeight = monitor.Size.Y });
                fold.EmitSignal(BaseButton.SignalName.Pressed); await Settle();
                var header = Walk(monitor).OfType<Control>().First(n => n is HBoxContainer && n.MouseFilter == Control.MouseFilterEnum.Stop);
                using var press = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true };
                using var release = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false };
                header.EmitSignal(Control.SignalName.GuiInput, press); bool dragBegan = Field<bool>(monitor, "dragging"); monitor._Input(release);
                Assert(prefix + "native_drag_state", dragBegan && !Field<bool>(monitor, "dragging"), new { dragBegan });
                layouts.Add(new { resolution = new { width = resolution.X, height = resolution.Y }, rect = Rect(monitor.GetGlobalRect()), text = VisibleText(monitor) });
                viewport.QueueFree(); viewport = null; await Settle();
            }
            var report = new { success = true, nativeUi = true, isolatedSubViewports = true, providerApiCalls = 0, gameplayActions = 0, assertions = results, layouts };
            Write(report); return report;
        }
        catch (Exception ex) { Write(new { success = false, nativeUi = true, providerApiCalls = 0, gameplayActions = 0, assertions = results, failure = ex.ToString() }); throw; }
        finally { if (GodotObject.IsInstanceValid(viewport)) viewport!.QueueFree(); }
        void Assert(string name, bool passed, object evidence) { results.Add(new { name, passed, evidence }); if (!passed) throw new InvalidOperationException("Broadcast UI assertion: " + name); }
        void Write(object report) { Directory.CreateDirectory(outputDirectory); File.WriteAllText(Path.Combine(outputDirectory, "broadcast-ui.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); }
        async Task Settle() { for (int i = 0; i < 4; i++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame); }
    }

    private static void Current(AutopilotPanel panel, string owner, string phase, string context) => panel.BeginCurrent(owner, phase, context);
    private static void Details(AutopilotPanel panel, string value) => panel.SetCurrentDetails(value);
    private static void Metrics(AutopilotPanel panel, string value) => panel.SetCurrentMetrics(value);
    private static void Solver(AutopilotPanel panel, string mode, string phase, double seconds, long nodes, double budget, int completed, int total, string action, string detail)
    {
        panel.SetSolverProgress(new SolverProgressView(mode, phase, seconds, nodes, budget, completed, total, action, detail));
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static string VisibleText(Node root) => string.Join('\n', Walk(root).OfType<Label>().Where(l => l.IsVisibleInTree()).Select(l => l.Text));
    private static object Rect(Rect2 rect) => new { x = rect.Position.X, y = rect.Position.Y, width = rect.Size.X, height = rect.Size.Y };
    private static IEnumerable<Node> Walk(Node root) { yield return root; foreach (var child in root.GetChildren()) foreach (var item in Walk(child)) yield return item; }
}
