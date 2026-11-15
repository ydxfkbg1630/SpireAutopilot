using System.Globalization;
using Godot;

namespace SpireAutopilot;

/// <summary>Edits only explicitly changed native solver values; the parent owns scrolling and visibility.</summary>
public partial class SolverParametersPanel : VBoxContainer
{
    private readonly Dictionary<string, object?> dirty = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>();
    private IReadOnlyList<SolverParameter> fields = Array.Empty<SolverParameter>();
    private readonly Dictionary<string, Func<object?>> editors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> complexDrafts = new(StringComparer.Ordinal);
    private OptionButton category = null!;
    private LineEdit filter = null!;
    private VBoxContainer fieldHost = null!;
    private Label status = null!;
    private string[] groups = Array.Empty<string>();
    private bool initialized;
    public int FieldCount => fields.Count;

    public override void _Ready()
    {
        if (initialized) return;
        initialized = true; SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 9);
        AddChild(Wrap("CombatSolver 参数；应用后下次搜索生效。"));
        var tools = new HBoxContainer(); AddChild(tools);
        category = new OptionButton { CustomMinimumSize = new(150, 36) }; tools.AddChild(category);
        filter = new LineEdit { PlaceholderText = "搜索名称或字段", SizeFlagsHorizontal = SizeFlags.ExpandFill }; tools.AddChild(filter);
        var buttons = new HBoxContainer(); AddChild(buttons);
        var apply = new Button { Text = "应用设置" }; apply.Pressed += () => CommitPending(); buttons.AddChild(apply);
        var reload = new Button { Text = "重新读取" }; reload.Pressed += Reload; buttons.AddChild(reload);
        var reset = new Button { Text = "恢复默认" }; reset.Pressed += () =>
        {
            try { SolverSettingsAccess.ResetDefaults(); Reload(); SetStatus("已恢复默认，下次搜索生效。", false); }
            catch (Exception ex) { SetStatus("恢复默认：" + ex.Message, true); }
        }; buttons.AddChild(reset);
        status = Wrap("读取参数…"); AddChild(status);
        fieldHost = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; fieldHost.AddThemeConstantOverride("separation", 11); AddChild(fieldHost);
        category.ItemSelected += _ => Rebuild(); filter.TextChanged += _ => Rebuild();
        Reload();
    }

    public void Reload()
    {
        if (!initialized) return;
        try
        {
            var loadedFields = SolverSettingsAccess.GetFields();
            var loadedValues = SolverSettingsAccess.ReadValues();
            fields = loadedFields; values = loadedValues; dirty.Clear(); editors.Clear(); complexDrafts.Clear();
            string? selected = category.Selected > 0 && category.Selected - 1 < groups.Length ? groups[category.Selected - 1] : null;
            groups = fields.Select(f => f.Group).Distinct(StringComparer.Ordinal).ToArray();
            category.Clear(); category.AddItem("全部分类");
            foreach (var group in groups) category.AddItem(GroupLabel(group));
            int index = selected == null ? -1 : Array.IndexOf(groups, selected); category.Select(index + 1);
            Rebuild(); SetStatus($"{fields.Count} 项参数", false);
        }
        catch (Exception ex) { SetStatus("等待求解器参数：" + ex.Message, true); }
    }

    private void Rebuild()
    {
        editors.Clear();
        foreach (var child in fieldHost.GetChildren()) { fieldHost.RemoveChild(child); child.QueueFree(); }
        string? group = category.Selected > 0 ? groups[category.Selected - 1] : null;
        string query = filter.Text.Trim(); int visible = 0;
        foreach (var field in fields.Where(f => (group == null || f.Group == group) && (query.Length == 0 || f.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            var section = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; section.AddThemeConstantOverride("separation", 4); fieldHost.AddChild(section);
            var label = Wrap(field.Label); label.TooltipText = field.Name + "\n" + field.Description; section.AddChild(label);
            object? value = dirty.TryGetValue(field.Name, out var pending) ? pending : values.GetValueOrDefault(field.Name);
            BuildEditor(section, field, value);
            if (!string.IsNullOrWhiteSpace(field.Description)) { var hint = Wrap(field.Description); hint.AddThemeFontSizeOverride("font_size", 13); hint.AddThemeColorOverride("font_color", AutopilotTheme.Muted); section.AddChild(hint); }
            section.AddChild(new HSeparator()); visible++;
        }
        if (visible == 0) fieldHost.AddChild(Wrap("筛选结果：0 项"));
    }

    private void BuildEditor(VBoxContainer host, SolverParameter field, object? original)
    {
        Type type = Nullable.GetUnderlyingType(field.Type) ?? field.Type;
        bool nullable = Nullable.GetUnderlyingType(field.Type) != null;
        CheckBox? custom = null;
        if (nullable) { custom = new CheckBox { Text = "自定义", ButtonPressed = original != null }; host.AddChild(custom); }
        Func<object?> read; Action<bool> editable; Func<object?> complete = () => original;
        if (type == typeof(bool))
        {
            var input = new CheckBox { Text = "启用", ButtonPressed = original is true }; host.AddChild(input);
            read = () => input.ButtonPressed; editable = enabled => input.Disabled = !enabled;
            input.Toggled += _ => Mark();
        }
        else if (type.IsEnum)
        {
            var input = new OptionButton(); var options = Enum.GetValues(type).Cast<object>().ToArray();
            foreach (var option in options) input.AddItem(EnumLabel(option.ToString()!));
            int index = original == null ? 0 : Array.FindIndex(options, item => item.Equals(original)); input.Select(Math.Max(0, index)); host.AddChild(input);
            read = () => options[input.Selected]; editable = enabled => input.Disabled = !enabled;
            input.ItemSelected += _ => Mark();
        }
        else if (Numeric(type))
        {
            var input = new SpinBox { MinValue = Convert.ToDouble(field.Minimum ?? -1000000000d, CultureInfo.InvariantCulture), MaxValue = Convert.ToDouble(field.Maximum ?? 1000000000d, CultureInfo.InvariantCulture), Step = Convert.ToDouble(field.Step ?? (type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? .1 : 1), CultureInfo.InvariantCulture), Value = original == null ? 0 : Convert.ToDouble(original, CultureInfo.InvariantCulture), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            host.AddChild(input); read = () => Convert.ChangeType(input.Value, type, CultureInfo.InvariantCulture); editable = enabled => input.Editable = enabled;
            input.ValueChanged += _ => Mark();
        }
        else if (type == typeof(string))
        {
            var input = new LineEdit { Text = original as string ?? "", SizeFlagsHorizontal = SizeFlags.ExpandFill }; host.AddChild(input);
            read = () => input.Text; editable = enabled => input.Editable = enabled;
            input.TextChanged += _ => Mark();
        }
        else
        {
            var input = new TextEdit { Text = complexDrafts.GetValueOrDefault(field.Name) ?? SolverSettingsAccess.FormatComplex(original), CustomMinimumSize = new(0, 130), WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsHorizontal = SizeFlags.ExpandFill }; host.AddChild(input);
            read = () => SolverSettingsAccess.ParseComplex(field.Name, input.Text); editable = enabled => input.Editable = enabled;
            input.TextChanged += () => { complexDrafts[field.Name] = input.Text; Mark(); };
        }
        complete = () => custom is { ButtonPressed: false } ? null : read();
        editors[field.Name] = complete;
        if (custom != null) { editable(custom.ButtonPressed); custom.Toggled += enabled => { editable(enabled); Mark(); }; }
        void Mark()
        {
            try
            {
                var changed = complete(); var saved = values.GetValueOrDefault(field.Name);
                bool equal = Equals(changed, saved) || changed != null && saved != null && SolverSettingsAccess.FormatComplex(changed) == SolverSettingsAccess.FormatComplex(saved);
                if (equal) dirty.Remove(field.Name); else dirty[field.Name] = changed;
                SetStatus($"待应用：{dirty.Count} 项", false);
            }
            catch (Exception ex) { dirty[field.Name] = new PendingInvalid(ex.Message); SetStatus(field.Label + "：" + ex.Message, true); }
        }
    }

    public bool CommitPending()
    {
        if (!initialized) return true;
        try
        {
            foreach (string name in dirty.Keys.ToArray())
                if (editors.TryGetValue(name, out var read)) dirty[name] = read();
            foreach (var pair in dirty) if (pair.Value is PendingInvalid invalid) throw new InvalidOperationException(pair.Key + "：" + invalid.Message);
            if (dirty.Count == 0) { SetStatus("设置已保存", false); return true; }
            int count = dirty.Count; SolverSettingsAccess.Save(new Dictionary<string, object?>(dirty, StringComparer.Ordinal)); Reload();
            SetStatus($"已应用 {count} 项，下次搜索生效。", false); return true;
        }
        catch (Exception ex) { SetStatus("设置校验：" + ex.Message, true); return false; }
    }

    private void SetStatus(string text, bool error) { if (status == null) return; status.Text = text; status.AddThemeColorOverride("font_color", error ? new Color("#ed9999") : AutopilotTheme.Model); }
    private static Label Wrap(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(460, 0) };
    private static bool Numeric(Type type) => type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(uint) || type == typeof(ulong);
    private static string GroupLabel(string value) => value.ToLowerInvariant() switch { "search" => "搜索与性能", "battle" or "combat" => "战斗与自动执行", "growth" => "成长与遗物", "potion" or "potions" => "药水策略", "display" or "ui" or "overlay" => "界面与显示", "diagnostics" or "logs" => "日志与反馈", "notification" or "notifications" => "通知", _ => value };
    private static string EnumLabel(string value) => value switch { "Low" => "低", "Medium" => "中", "High" => "高", "VeryHigh" => "极高", "Custom" => "自定义", "FollowGame" => "跟随游戏", "Normal" => "普通", "Fast" => "快速", "Instant" => "即时", "OnlyWhenGameInBackground" => "仅游戏位于后台", "Always" => "始终", "Dark" => "深色", "Light" => "浅色", "Disabled" => "关闭", "Smart" => "智能", "RequireAtLeastOne" => "至少使用一瓶", "ProgressionFirst" => "优先推进", "MinimizeHpLoss" => "减少生命损失", _ => value };
    private sealed record PendingInvalid(string Message);
}
