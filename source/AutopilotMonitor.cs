using Godot;
namespace SpireAutopilot;
/// <summary>Compact live card: only the current stage and observed progress are retained.</summary>
public partial class AutopilotMonitor : PanelContainer
{
    private Label phase = null!, decision = null!, usage = null!, chip = null!, dot = null!;
    private ScrollContainer scroll = null!;
    private Button collapse = null!;
    private VBoxContainer live = null!;
    private bool folded, dragging, initialized;
    private Vector2 dragOffset;
    private string? currentKey;
    public void Initialize()
    {
        if (initialized) return; initialized = true; Name = "AutopilotDecisionMonitor"; Theme = AutopilotTheme.Create();
        Position = new Vector2(18,205); CustomMinimumSize = new Vector2(360,0); Size = new Vector2(360,208); MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", AutopilotTheme.Panel());
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; column.AddThemeConstantOverride("separation",7); AddChild(column);
        var header = new HBoxContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(0,30), MouseDefaultCursorShape = CursorShape.Move }; column.AddChild(header);
        var title = new Label { Text = "尖塔领航", SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        title.AddThemeFontOverride("font",new SystemFont { FontNames = new[] { "SimSun", "Microsoft YaHei UI" } }); title.AddThemeFontSizeOverride("font_size",21); header.AddChild(title);
        dot = new Label { Text = "•", MouseFilter = MouseFilterEnum.Ignore }; dot.AddThemeColorOverride("font_color",AutopilotTheme.Muted); header.AddChild(dot);
        chip = new Label { Text = "待命", MouseFilter = MouseFilterEnum.Ignore }; chip.AddThemeFontSizeOverride("font_size",14); header.AddChild(chip);
        collapse = new Button { Text = "收起", TooltipText = "拖动标题移动 · F10 显示或隐藏" }; collapse.AddThemeFontSizeOverride("font_size",13); collapse.Pressed += ToggleFold; header.AddChild(collapse); header.GuiInput += HeaderInput;
        live = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; live.AddThemeConstantOverride("separation",6); column.AddChild(live); live.AddChild(new HSeparator());
        phase = Wrapped("等待领航启动。",16); phase.MaxLinesVisible = 1; live.AddChild(phase);
        scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto, CustomMinimumSize = new Vector2(0,76), SizeFlagsHorizontal = SizeFlags.ExpandFill }; live.AddChild(scroll);
        decision = Wrapped("F9 开始领航。",18); scroll.AddChild(decision);
        usage = Wrapped("F8 设置    F9 启停    F10 收起",13); usage.MaxLinesVisible = 1; usage.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; usage.AddThemeColorOverride("font_color",AutopilotTheme.Muted); live.AddChild(usage); Hide();
    }
    public void BeginCurrent(string owner,string stage,string contextKey)
    {
        string key = owner+"\n"+stage+"\n"+contextKey;
        if (currentKey != key) { currentKey = key; Set(decision,""); Set(usage,""); scroll.ScrollVertical = 0; FitBody(""); }
        bool combat = owner.Contains("战斗",StringComparison.Ordinal)||owner.Contains("solver",StringComparison.OrdinalIgnoreCase)||owner.Contains("combat",StringComparison.OrdinalIgnoreCase);
        Set(chip,combat?"战斗":owner.Contains("模型",StringComparison.Ordinal)||owner.Contains("model",StringComparison.OrdinalIgnoreCase)?"模型":owner);
        dot.AddThemeColorOverride("font_color",combat?AutopilotTheme.Amber:AutopilotTheme.Model); Set(phase,stage);
    }
    public void SetCurrentDetails(string text) { if(decision.Text==text)return;Set(decision,text);FitBody(text); }
    public void SetCurrentMetrics(string text) { Set(usage,text.Replace('\r',' ').Replace('\n',' ')); usage.TooltipText = ""; }
    public void SetSolverProgress(SolverProgressView view)
    {
        bool executing = view.Mode is "deploy" or "deployment";
        Set(chip, executing ? "执行" : view.Mode == "paused" ? "已暂停" : "战斗");
        dot.AddThemeColorOverride("font_color", view.Mode == "paused" ? AutopilotTheme.Muted : AutopilotTheme.Amber);
        Set(phase, view.Phase);
        string details = string.Join("\n", new[] { view.CurrentAction, view.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
        SetCurrentDetails(details.Length > 0 ? details : view.Mode == "search" ? "正在比较出牌路线。" : executing ? "正在执行当前路线。" : "等待下一步开始。");
        var values = new List<string>();
        if (view.Mode == "search")
        {
            if (view.ElapsedSeconds is double elapsed) values.Add($"{elapsed:F1} 秒");
            if (view.ExpandedNodes is long nodes) values.Add($"{nodes:N0} 节点");
        }
        else if (executing && view.CompletedActions is int done && view.TotalActions is int total) values.Add($"{done}/{total} 步");
        SetCurrentMetrics(string.Join("  ·  ", values));
    }
    public override void _Ready() { if(!initialized)Initialize(); FitViewport(); GetViewport().SizeChanged += FitViewport; }
    public override void _ExitTree() { if(GetViewport() is {} viewport)viewport.SizeChanged -= FitViewport; }
    public override void _Input(InputEvent e) { if(!dragging)return; if(e is InputEventMouseMotion){Position=ClampPosition(GetViewport().GetMousePosition()-dragOffset);GetViewport().SetInputAsHandled();}else if(e is InputEventMouseButton {ButtonIndex:MouseButton.Left,Pressed:false}){dragging=false;GetViewport().SetInputAsHandled();} }
    private void HeaderInput(InputEvent e) { if(e is InputEventMouseButton {ButtonIndex:MouseButton.Left,Pressed:true}){dragging=true;dragOffset=GetViewport().GetMousePosition()-GlobalPosition;AcceptEvent();} }
    private void ToggleFold() { folded=!folded;live.Visible=!folded;collapse.Text=folded?"展开":"收起";Size=new Vector2(360,folded?56:scroll.CustomMinimumSize.Y+118);Position=ClampPosition(Position); }
    private void FitBody(string text) { int lines=Math.Max(1,text.Split('\n').Sum(l=>Math.Max(1,(l.Length+17)/18)));scroll.CustomMinimumSize=new Vector2(0,Mathf.Clamp(lines*25,50,105));if(!folded)Size=new Vector2(360,scroll.CustomMinimumSize.Y+118);if(IsInsideTree())Position=ClampPosition(Position); }
    private void FitViewport() { if(initialized)Position=ClampPosition(Position); }
    private Vector2 ClampPosition(Vector2 p) {var v=GetViewportRect().Size;return new Vector2(Mathf.Clamp(p.X,8,Math.Max(8,v.X-Size.X-8)),Mathf.Clamp(p.Y,8,Math.Max(8,v.Y-Size.Y-8)));}
    public void ShowMonitor(){FitViewport();Show();} public void ToggleMonitor(){if(Visible)Hide();else ShowMonitor();}
    public void SetPhase(string text)=>Set(phase,text); public void SetPlan(string text){} public void SetDecision(string text)=>SetCurrentDetails(text);public void SetUsage(string text)=>SetCurrentMetrics(text.Split('\n').FirstOrDefault()??"");public void SetRecent(string text){}
    private static void Set(Label label,string text){if(label.Text!=text)label.Text=text;}
    private static Label Wrapped(string text,int size){var label=new Label{Text=text,AutowrapMode=TextServer.AutowrapMode.WordSmart,SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new Vector2(306,0),MouseFilter=MouseFilterEnum.Ignore};label.AddThemeFontSizeOverride("font_size",size);return label;}
}
