using System.Reflection;
using System.Text.Json;
using Godot;

namespace SpireAutopilot;

/// <summary>Native UI assertions scoped to the disposable native probe, with no gameplay actions.</summary>
public static class MonitorProbe
{
    public static async Task<object> RunAsync(AutopilotHost host, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPIRE_AUTOPILOT_PROBE")))
            throw new InvalidOperationException("Monitor assertions require the isolated native probe environment.");
        var results = new List<object>();
        var monitor = Walk(host).OfType<AutopilotMonitor>().Single();
        var originalVisible = monitor.Visible;
        bool originalFolded = Field<bool>(monitor, "folded");
        var originalPosition = monitor.Position;
        try
        {
            monitor.ShowMonitor();
            await Settle();
            var viewport = monitor.GetViewportRect();
            var rect = monitor.GetGlobalRect();
            var scroll = Walk(monitor).OfType<ScrollContainer>().Single();
            var content = scroll.GetChild<Control>(0);
            var header = Walk(monitor).OfType<HBoxContainer>().Single();
            Assert("monitor_visible", monitor.IsVisibleInTree(), new { monitor.Visible });
            Assert("monitor_inside_viewport", viewport.Encloses(rect), new { viewport = Rect(viewport), monitor = Rect(rect) });
            Assert("native_mouse_isolation", monitor.MouseFilter == Control.MouseFilterEnum.Stop && header.MouseFilter == Control.MouseFilterEnum.Stop,
                new { rootMouseFilter = monitor.MouseFilter.ToString(), headerMouseFilter = header.MouseFilter.ToString() });
            Assert("wrapped_scroll_content", scroll.HorizontalScrollMode == ScrollContainer.ScrollMode.Disabled
                && Walk(content).OfType<Label>().Where(l => l.CustomMinimumSize.X > 0).All(l => l.AutowrapMode == TextServer.AutowrapMode.WordSmart)
                && content.Size.X <= scroll.Size.X + 1,
                new { scroll = Rect(scroll.GetGlobalRect()), content = Rect(content.GetGlobalRect()), horizontalOverflow = content.Size.X - scroll.Size.X });

            using var f10 = new InputEventKey { Keycode = Key.F10, Pressed = true, Echo = false };
            host._Input(f10);
            Assert("f10_hides", !monitor.Visible, new { monitor.Visible });
            host._Input(f10);
            await Settle();
            Assert("f10_shows", monitor.IsVisibleInTree(), new { monitor.Visible });

            var collapse = Walk(header).OfType<Button>().Single(b => b.Text is "收起" or "展开");
            if (originalFolded) collapse.EmitSignal(BaseButton.SignalName.Pressed);
            await Settle();
            float expandedHeight = monitor.Size.Y;
            collapse.EmitSignal(BaseButton.SignalName.Pressed);
            await Settle();
            Assert("collapse_hides_body", !scroll.Visible && monitor.Size.Y < expandedHeight,
                new { expandedHeight, foldedHeight = monitor.Size.Y, scroll.Visible });
            collapse.EmitSignal(BaseButton.SignalName.Pressed);
            await Settle();
            Assert("expand_restores_body", scroll.Visible && monitor.Size.Y >= expandedHeight - 1,
                new { expandedHeight, restoredHeight = monitor.Size.Y, scroll.Visible });

            // Native header GUI signal reaches the drag state machine; release resets it.
            // Pointer position stays as-is so the probe can run beside other desktop work.
            using var press = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true };
            using var release = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false };
            header.EmitSignal(Control.SignalName.GuiInput, press);
            bool dragBegan = Field<bool>(monitor, "dragging");
            monitor._Input(release);
            Assert("native_drag_press_release", dragBegan && !Field<bool>(monitor, "dragging"), new { dragBegan, dragEnded = !Field<bool>(monitor, "dragging") });

            object report = new
            {
                success = true, nativeUi = true, gameplayActions = 0, isolatedProbe = true,
                display = DisplayServer.GetName(), viewport = Rect(viewport), assertions = results,
                controls = Walk(monitor).OfType<Control>().Select(c => new
                {
                    type = c.GetType().Name, name = c.Name.ToString(), c.Visible, visibleInTree = c.IsVisibleInTree(),
                    rect = Rect(c.GetGlobalRect()), minimum = new { x = c.GetCombinedMinimumSize().X, y = c.GetCombinedMinimumSize().Y },
                    filter = c.MouseFilter.ToString(), text = c is Label l ? l.Text : c is Button b ? b.Text : null
                }).ToArray(),
                scope = "Native visibility, layout, wrapping, scrolling, F10, collapse/expand and title drag signal state. Desktop pointer and run state stay intact."
            };
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "monitor-ui.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return report;
        }
        catch (Exception ex)
        {
            object report = new { success = false, nativeUi = true, isolatedProbe = true, assertions = results, failure = ex.ToString() };
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "monitor-ui.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            bool nowFolded = Field<bool>(monitor, "folded");
            if (nowFolded != originalFolded)
                Walk(monitor).OfType<Button>().Single(b => b.Text is "收起" or "展开").EmitSignal(BaseButton.SignalName.Pressed);
            monitor.Position = originalPosition;
            monitor.Visible = originalVisible;
        }

        void Assert(string name, bool passed, object evidence)
        {
            results.Add(new { name, passed, evidence });
            if (!passed) throw new InvalidOperationException("Monitor UI assertion: " + name);
        }
        async Task Settle()
        {
            for (int i = 0; i < 4; i++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static object Rect(Rect2 r) => new { x = r.Position.X, y = r.Position.Y, width = r.Size.X, height = r.Size.Y };
    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
    private static IEnumerable<Node> Walk(Node node)
    {
        yield return node;
        foreach (var child in node.GetChildren()) foreach (var descendant in Walk(child)) yield return descendant;
    }
}
