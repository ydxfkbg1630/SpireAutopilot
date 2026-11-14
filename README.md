# 尖塔全程领航 · SpireAutopilot

为《杀戮尖塔 2》单人跑局提供自动领航。兼容 OpenAI 协议的模型负责地图、事件、奖励、商店、休息和选牌；CombatSolver 负责搜索和执行战斗。通过游戏原生状态和动作接口完成决策，支持整幕路线规划、持续会话和精简直播窗口。

[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3839527748) · [GitHub Releases](https://github.com/ydxfkbg1630/SpireAutopilot/releases) · [更新记录](CHANGELOG.md)

## 安装

目标版本：游戏 v0.113.0、Godot 4.5.1、.NET 9；CombatSolver 0.51.5。

1. 通过 Steam 创意工坊安装并启用 **CombatSolver** 及其 **RitsuLib** 依赖。
2. 从本项目 GitHub Releases 下载模组安装包，将包内 `SpireAutopilot` 文件夹放入游戏的 `mods/` 目录。最终 `mods/SpireAutopilot/` 应包含 `SpireAutopilot.dll` 和 `SpireAutopilot.json`。
3. 启动游戏并启用「尖塔全程领航」，进入或继续一局单人游戏。
4. 按 **F8** 填写模型 Base URL、模型 ID 和 API Key，测试连接。
5. 按 **F9** 开始或暂停；按 **F10** 显示或隐藏直播小窗。

愚者与观者组合可按需单独安装 **FoolSolverCompat**。该兼容模组单独发布，由其自己的许可证约束。

## 模型与会话

支持 Responses 与 Chat Completions。Base URL 可填写服务根地址或完整接口地址，模型 ID 使用服务商当前提供的名称。第三方兼容服务的协议能力以其实际接口为准。

持续会话保留固定前缀并追加局面差分，按轮数、输入字节和相对局面长度整理历史；明确的继续和领取金币等动作在本地执行。DeepSeek Responses 采用客户端历史回放，原生 OpenAI Responses 支持响应链。

API Key 通过当前 Windows 用户的 DPAPI 加密保存在 `%APPDATA%/SlayTheSpire2/spire_autopilot.json`。仓库中的 `spire_autopilot.example.json` 用于说明设置。使用者在游戏面板填写自己的接口与密钥。

## 窗口和运行范围

直播小窗只显示当前阶段及当前决策摘要；战斗期间显示求解器搜索时间、节点或出牌进度。F8 高级页提供详细用量与会话统计。领航接管期间统一使用精简窗口，暂停时恢复求解器界面。

0.4.1 加入自适应内存保护：搜索使用常规 GC，分配检查周期为 256 MiB 至 1 GiB；根据当前物理与系统提交余量调整新搜索并行数，余量告急时通过求解器原生接口暂停。该周期是分配检查预算，实际进程内存包含游戏资源和各模组对象。默认保留至少 2 GiB 或物理内存 10% 的系统余量，以及 1 GiB 提交余量。

同时兼容奶蛙愚者皮肤：预测计算期间隔离其 Spine 动画查询，实际出牌仍播放皮肤动画。

支持地图、奖励、事件、商店、休息、选牌、宝箱、水晶球和教学提示。目标范围为已经开始的单人跑局，结算时停止。扩展模组的特殊内容依其提供的原生动作和 CombatSolver 的兼容范围运行。决策质量和通关结果随模型、牌组、求解预算及游戏局面变化。

## 从源码构建

安装 .NET 9 SDK 与合法拥有的游戏；NuGet 将还原 Godot.NET.Sdk/4.5.1。源码通过本地游戏目录引用 `sts2.dll`、`0Harmony.dll` 和 `GodotSharp.dll`。

在 PowerShell 中，从仓库根目录执行：

```powershell
$env:STS2_GAME_DATA = 'D:/SteamLibrary/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64'
dotnet build source/SpireAutopilot.csproj -c Release -m:1
```

也可显式传入 `-p:GameData="游戏目录/data_sts2_windows_x86_64"`。编译结果位于 `source/.godot/mono/temp/bin/Release/SpireAutopilot.dll`，与根目录的 `SpireAutopilot.json` 一同安装。

离线逻辑测试需要 .NET 10 SDK，运行时使用本地模拟 HTTP 服务：

```powershell
dotnet run --project source/tests/ProviderTests.csproj
dotnet run --project source/tests/PlanningTests.csproj
dotnet run --project source/tests/SessionEfficiencyTests.csproj
dotnet run --project source/tests/MemoryTests.csproj
```

动画线程隔离测试使用游戏对应的 .NET 9 运行时与 Harmony：`dotnet run --project source/tests/VisualIsolationTests.csproj`（同样设置 `STS2_GAME_DATA`）。

`*Probe` 项目用于显式联调。涉及真实服务的探针由调用者提供本地配置与输入，会产生对应 API 用量。

## 贡献和许可证

请参阅 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [SECURITY.md](SECURITY.md)。SpireAutopilot 的原创代码按 **Apache License 2.0** 发布，详见 [LICENSE](LICENSE) 与 [NOTICE](NOTICE)。

CombatSolver、RitsuLib、FoolSolverCompat、Godot、Harmony 以及游戏本体分别遵循各自的许可证或使用条款。本项目引用游戏的本地程序集，并通过运行时反射衔接 CombatSolver。游戏资源与这些第三方依赖由各自官方渠道提供。
