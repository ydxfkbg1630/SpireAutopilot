using Godot;
namespace SpireAutopilot;
public partial class AutopilotPanel : CanvasLayer
{
    private PanelContainer body=null!;
    private Label status=null!,usageDetails=null!;
    private Button toggle=null!;
    private AutopilotMonitor monitor=null!;
    private TabContainer tabs=null!;
    private VBoxContainer connection=null!;
    private Button connectionToggle=null!;
    private LineEdit url=null!,key=null!,model=null!;
    private OptionButton mode=null!,thinking=null!;
    private TextEdit strategy=null!;
    private SpinBox timeout=null!,delay=null!,workerLimit=null!,sessionTurns=null!,sessionKilobytes=null!,sessionRatio=null!;
    private CheckBox autoCombat=null!,fallback=null!,jsonMode=null!,lightweightDiagnostics=null!,persistentSession=null!,localActions=null!,broadcast=null!,maxSpeed=null!,reflectAfterRun=null!,useRunExperience=null!;
    private AutopilotConfig initial=null!;
    private SolverParametersPanel solverParameters=null!;
    private Action<AutopilotConfig> save=null!;
    private Action runToggle=null!;
    private Func<Task> test=null!;
    public void Initialize(AutopilotConfig config,Action<AutopilotConfig> onSave,Action onToggle,Func<Task> onTest)
    {
        initial=config;save=onSave;runToggle=onToggle;test=onTest;Layer=110;
        monitor=new AutopilotMonitor();monitor.Initialize();AddChild(monitor);
        var badge=new Button{Text="领航 F8",CustomMinimumSize=new(110,34),Theme=AutopilotTheme.Create()};badge.SetAnchorsPreset(Control.LayoutPreset.TopRight);badge.OffsetLeft=-135;badge.OffsetRight=-25;badge.OffsetTop=155;badge.OffsetBottom=189;badge.Pressed+=ToggleVisible;AddChild(badge);
        body=new PanelContainer{Position=new Vector2(30,105),Size=new Vector2(580,620),Theme=AutopilotTheme.Create(),MouseFilter=Control.MouseFilterEnum.Stop};body.AddThemeStyleboxOverride("panel",AutopilotTheme.Panel(17));AddChild(body);
        var root=new VBoxContainer();root.AddThemeConstantOverride("separation",10);body.AddChild(root);
        var header=new HBoxContainer();root.AddChild(header);var title=new Label{Text="尖塔领航",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};title.AddThemeFontSizeOverride("font_size",23);header.AddChild(title);var close=new Button{Text="收起 F8"};close.Pressed+=body.Hide;header.AddChild(close);
        status=Wrap("就绪");status.MaxLinesVisible=2;root.AddChild(status);
        var actions=new HBoxContainer();root.AddChild(actions);toggle=new Button{Text="开始领航 F9"};toggle.Pressed+=()=>runToggle();actions.AddChild(toggle);var saveButton=new Button{Text="保存设置"};saveButton.Pressed+=()=>{try{save(ReadConfig());}catch(Exception ex){SetStatus(ex.Message,false);}};actions.AddChild(saveButton);var show=new Button{Text="显示直播窗 F10"};show.Pressed+=ShowMonitor;actions.AddChild(show);
        tabs=new TabContainer{CustomMinimumSize=new(540,450),SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};root.AddChild(tabs);
        var live=Page("直播");broadcast=Check(live,"打开设置时显示直播页",config.BroadcastMode);autoCombat=Check(live,"自动战斗",config.AutoCombat);live.AddChild(Wrap("CombatSolver 负责战斗；模型负责地图、事件、奖励和商店。"));maxSpeed=Check(live,"求解器全速搜索",config.SolverMaxSpeed);localActions=Check(live,"自动继续、确认与领取金币",config.LocalMechanicalActions);live.AddChild(Wrap("直播窗显示当前决策或搜索进度；拖动标题移动。"));live.AddChild(new HSeparator());live.AddChild(new Label{Text="策略偏好"});strategy=new TextEdit{Text=config.Strategy,CustomMinimumSize=new(0,105),WrapMode=TextEdit.LineWrappingMode.Boundary};live.AddChild(strategy);delay=Number(live,"动作间隔（秒）",.3,30,.1,config.DecisionDelaySeconds);
        var models=Page("模型");model=Field(models,"模型名称",config.Model,"服务提供方的模型 ID");models.AddChild(new Label{Text="模型思考模式"});thinking=new OptionButton();thinking.AddItem("跟随服务默认");thinking.AddItem("启用思考");thinking.AddItem("快速决策");thinking.Select(config.ThinkingEnabled is null?0:config.ThinkingEnabled.Value?1:2);models.AddChild(thinking);
        connectionToggle=new Button{Text="展开连接设置"};models.AddChild(connectionToggle);connection=new VBoxContainer();models.AddChild(connection);url=Field(connection,"接口地址",config.BaseUrl,"https://api.openai.com/v1");key=Field(connection,"API Key · Windows 账户加密保存",config.ApiKey,"sk-…");key.Secret=true;connection.AddChild(new Label{Text="协议"});mode=new OptionButton();mode.AddItem("Responses");mode.AddItem("Chat Completions");mode.Select(config.ApiMode=="Responses"?0:1);connection.AddChild(mode);connection.Hide();connectionToggle.Pressed+=()=>{connection.Visible=!connection.Visible;connectionToggle.Text=connection.Visible?"收起连接设置":"展开连接设置";};var testButton=new Button{Text="测试连接"};testButton.Pressed+=async()=>await test();models.AddChild(testButton);
        models.AddChild(new HSeparator());models.AddChild(new Label{Text="经验总结"});reflectAfterRun=Check(models,"每局更新经验总结",config.ReflectAfterRun);useRunExperience=Check(models,"决策时参考经验",config.UseRunExperience);models.AddChild(Wrap("每局结束后调用模型更新同一份 experience.md，供后续决策参考。"));var experienceFolder=new Button{Text="打开经验目录"};experienceFolder.Pressed+=()=>{try{string folder=System.IO.Path.Combine(OS.GetUserDataDir(),"spire_autopilot_run_experience");System.IO.Directory.CreateDirectory(folder);var result=OS.ShellOpen(folder);if(result!=Error.Ok)SetStatus("打开经验目录："+result,false);}catch(Exception ex){SetStatus("经验目录："+ex.Message,false);}};models.AddChild(experienceFolder);
        var advanced=Page("高级");persistentSession=Check(advanced,"持续会话（复用历史前缀）",config.PersistentSession);jsonMode=Check(advanced,"Chat 接口使用标准 JSON 输出",config.UseJsonMode);fallback=Check(advanced,"扩展场景使用原生按钮语义",config.AllowNativeUiFallback);lightweightDiagnostics=Check(advanced,"精简求解器日志",config.LightweightSolverDiagnostics);workerLimit=Number(advanced,"求解器线程上限（0 跟随原设）",0,16,1,config.SolverWorkerLimit);timeout=Number(advanced,"模型请求超时（秒）",5,300,1,config.RequestTimeoutSeconds);sessionTurns=Number(advanced,"会话轮数上限",2,32,1,config.SessionMaxTurns);sessionKilobytes=Number(advanced,"会话输入预算（KiB）",16,512,16,config.SessionMaxInputBytes/1024d);sessionRatio=Number(advanced,"历史长度 / 当前完整局面",1.25,4,.25,config.SessionMaxHistoryRatio);advanced.AddChild(new HSeparator());advanced.AddChild(new Label{Text="本次请求详情"});usageDetails=Wrap("等待请求");advanced.AddChild(usageDetails);
        var inspectContext=new Button{Text="查看本次模型上下文"};inspectContext.Pressed+=()=>{string path=System.IO.Path.Combine(OS.GetUserDataDir(),"spire_autopilot_model_context.json");if(System.IO.File.Exists(path))OS.ShellOpen(path);else SetStatus("下次模型请求后可查看上下文",false);};advanced.AddChild(inspectContext);
        var solverPage=Page("求解器");solverParameters=new SolverParametersPanel();solverPage.AddChild(solverParameters);
        body.Hide();FitSettings();GetViewport().SizeChanged+=FitSettings;
        VBoxContainer Page(string name){var scroll=new ScrollContainer{Name=name,HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};tabs.AddChild(scroll);var content=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};content.AddThemeConstantOverride("separation",10);scroll.AddChild(content);return content;}
    }
    private static Label Wrap(string text)=>new(){Text=text,AutowrapMode=TextServer.AutowrapMode.WordSmart,CustomMinimumSize=new(480,0),SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
    private static LineEdit Field(VBoxContainer parent,string title,string text,string placeholder){parent.AddChild(new Label{Text=title});var field=new LineEdit{Text=text,PlaceholderText=placeholder,CustomMinimumSize=new(0,36)};parent.AddChild(field);return field;}
    private static CheckBox Check(VBoxContainer parent,string title,bool pressed){var box=new CheckBox{Text=title,ButtonPressed=pressed};parent.AddChild(box);return box;}
    private static SpinBox Number(VBoxContainer parent,string title,double min,double max,double step,double value){var row=new HBoxContainer();parent.AddChild(row);row.AddChild(new Label{Text=title,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill});var number=new SpinBox{MinValue=min,MaxValue=max,Step=step,Value=value,CustomMinimumSize=new(115,36)};row.AddChild(number);return number;}
    public AutopilotConfig ReadConfig()=>new(){BaseUrl=url.Text.Trim(),ApiKey=key.Text.Trim(),Model=model.Text.Trim(),ApiMode=mode.Selected==0?"Responses":"ChatCompletions",PersistentSession=persistentSession.ButtonPressed,SessionMaxTurns=(int)sessionTurns.Value,SessionMaxInputBytes=(int)sessionKilobytes.Value*1024,SessionMaxHistoryRatio=sessionRatio.Value,LocalMechanicalActions=localActions.ButtonPressed,Strategy=strategy.Text.Trim(),RequestTimeoutSeconds=(int)timeout.Value,DecisionDelaySeconds=delay.Value,AutoCombat=autoCombat.ButtonPressed,ReflectAfterRun=reflectAfterRun.ButtonPressed,UseRunExperience=useRunExperience.ButtonPressed,SolverWorkerLimit=(int)workerLimit.Value,SolverMaxSpeed=maxSpeed.ButtonPressed,BroadcastMode=broadcast.ButtonPressed,LightweightSolverDiagnostics=lightweightDiagnostics.ButtonPressed,AllowNativeUiFallback=fallback.ButtonPressed,Temperature=initial.Temperature,ThinkingEnabled=thinking.Selected==0?null:thinking.Selected==1,UseJsonMode=jsonMode.ButtonPressed};
    public void ToggleVisible(){if(body.Visible)body.Hide();else ShowPanel();}
    public void ShowPanel(){if(broadcast.ButtonPressed)tabs.CurrentTab=0;connection.Hide();connectionToggle.Text="展开连接设置";key.Secret=true;FitSettings();body.Show();}
    public void SetStatus(string text,bool running){if(status.Text!=text)status.Text=text;string label=running?"暂停领航 F9":"开始领航 F9";if(toggle.Text!=label)toggle.Text=label;}
    public void SetMonitorState(string text)=>monitor.SetPhase(text);
    public void SetDecision(string text)=>monitor.SetCurrentDetails(text);
    public void SetUsage(string text){if(usageDetails.Text!=text)usageDetails.Text=text;}
    public void BeginCurrent(string owner,string phase,string contextKey)=>monitor.BeginCurrent(owner,phase,contextKey);
    public void SetCurrentDetails(string text)=>monitor.SetCurrentDetails(text);
    public void SetCurrentMetrics(string text)=>monitor.SetCurrentMetrics(text);
    public void SetSolverProgress(SolverProgressView view)=>monitor.SetSolverProgress(view);
    public void ToggleMonitor()=>monitor.ToggleMonitor();public void ShowMonitor()=>monitor.ShowMonitor();
    private void FitSettings()
    {
        var viewport=GetViewport().GetVisibleRect().Size;
        float overhead=Math.Max(150,body.GetCombinedMinimumSize().Y-tabs.GetCombinedMinimumSize().Y);
        tabs.CustomMinimumSize=new Vector2(540,Mathf.Clamp(viewport.Y-overhead-45,200,450));
        float height=Math.Min(viewport.Y-32,Math.Max(420,body.GetCombinedMinimumSize().Y));
        body.Size=new Vector2(580,height);
        body.Position=new Vector2(Math.Max(12,Math.Min(30,viewport.X-body.Size.X-12)),Math.Max(16,Math.Min(105,viewport.Y-height-16)));
    }
    public override void _ExitTree(){if(body!=null&&GetViewport() is {} viewport)viewport.SizeChanged-=FitSettings;}
}
