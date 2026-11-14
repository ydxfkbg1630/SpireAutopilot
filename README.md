# 尖塔全程领航 · SpireAutopilot

为《杀戮尖塔 2》单人跑局提供自动领航。兼容 OpenAI 协议的模型负责地图、事件、奖励、商店、休息和局外选牌；战斗优先使用 STS2 Solver，搜索达到 120 秒时由 CombatSolver 接替。通过游戏原生状态和动作接口完成决策，支持整幕路线规划、持续会话、整局游戏经验和精简直播窗口。

[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3839527748) · [GitHub Releases](https://github.com/ydxfkbg1630/SpireAutopilot/releases) · [更新记录](CHANGELOG.md)

## 安装

目标版本：游戏 v0.113.0、Godot 4.5.1、.NET 9；主求解器 STS2 Solver、备用 CombatSolver 0.51.5、RitsuLib 0.6.11+。

1. 通过 Steam 创意工坊安装并启用 **STS2 Solver（尖塔求解器）**、**CombatSolver** 和 **RitsuLib**。
2. 从本项目 GitHub Releases 下载模组安装包，将包内 `SpireAutopilot` 文件夹放入游戏的 `mods/` 目录。最终 `mods/SpireAutopilot/` 应包含 `SpireAutopilot.dll` 和 `SpireAutopilot.json`。
3. 启动游戏并启用「尖塔全程领航」，进入或继续一局单人游戏。
4. 按 **F8** 填写模型 Base URL、模型 ID 和 API Key，测试连接。
5. 按 **F9** 开始或暂停；按 **F10** 显示或隐藏直播小窗。

在 **F8 → 直播** 勾选「启用自动战斗」。战斗控制固定为 **STS2 Solver 优先 → 搜索 120 秒超时转 CombatSolver**。主求解器负责搜索和执行；实际搜索达到 120 秒且仍在计算时，备用求解器接管本场后续战斗，下一场重新使用 STS2 Solver。领航协调两套求解器的控制权，当前执行方负责战斗出牌、用药和原生选牌。

愚者与观者组合可按需单独安装 **FoolSolverCompat**。该兼容模组单独发布，由其自己的许可证约束。

## 模型与会话

支持 Responses 与 Chat Completions。Base URL 可填写服务根地址或完整接口地址，模型 ID 使用服务商当前提供的名称。第三方兼容服务的协议能力以其实际接口为准。

持续会话保留固定前缀并追加局面差分，按轮数、输入字节和相对局面长度整理历史；明确的继续和领取金币等动作在本地执行。DeepSeek Responses 采用客户端历史回放，原生 OpenAI Responses 支持响应链。

API Key 通过当前 Windows 用户的 DPAPI 加密保存在 `%APPDATA%/SlayTheSpire2/spire_autopilot.json`。仓库中的 `spire_autopilot.example.json` 用于说明设置。使用者在游戏面板填写自己的接口与密钥。

### 整局游戏经验

模型处理战斗外的战略决策，并在整局结算后根据路线、牌组构筑、资源选择与结果整理一份游戏经验。**F8 → 模型 → 游戏经验** 提供「整局结束后生成游戏经验」和「后续决策读取游戏经验」两个独立开关，默认开启。生成经验按实际模型调用产生用量；经验作为历史观测，结合当前局面使用。

点击「游戏经验目录」可打开游戏用户数据目录下的 `spire_autopilot_run_experience/`。每整局对应一份经验文档，后续局外决策按开关读取。F8 的「非战斗思考模式」用于局外决策与游戏经验整理；直播窗只呈现当前内容。

## 窗口和运行范围

直播小窗只显示当前阶段及当前决策摘要；战斗期间显示求解器搜索时间、节点或出牌进度。F8 高级页提供详细用量与会话统计。领航接管期间统一使用精简窗口，暂停时恢复求解器界面。

0.4.5 恢复求解器原有的搜索质量与预算：时间、节点、束宽、分支范围及搜索组合均沿用求解器配置。保留 F9 提前接管、实际执行器诊断、工作线程设置，以及内存回收和动画线程隔离。工作线程数沿用求解器配置或用户手动上限；实际搜索进度以求解器状态为准。

### 在 F8 调整备用求解器

**F8 → 备用求解器** 页集中提供 CombatSolver 的 **53 项参数**。主求解器 STS2 Solver 的搜索、药水等设置使用其自带面板。备用参数支持分类或搜索中文名称、原始字段名，可编辑搜索、执行、内存、药水收益、诊断和窗口参数。可空参数的「自定义」控制是否覆盖原生预设；选择性能预设时沿用求解器原生同步逻辑，手动调整搜索预算会切换为 Custom。

改好后点击「应用求解器设置」，仅提交已修改字段，经原生校验保存，下一次搜索采用新设置；「重新读取」取回当前配置，「恢复求解器默认」应用原生默认值。成长预算、遗物计数规则和药水指令等复杂参数以 JSON 编辑，字段结构与枚举参照面板说明。运行时 GC 配置通常随下一次启动生效。

备用求解器保留原 300 秒、500,000 节点、束宽 135 和搜索组合配置。后续预算由玩家在面板内选择；此前试验原型已归档，发布内容以本版源码与参数面板为准。

0.4.3 的内存管理使用常规 GC，分配检查周期上限为 4 GiB；由求解器原生调度器结合可复用堆和当前余量安排任务，在原生回收边界释放缓存并继续搜索。工作线程数沿用求解器设置或用户手动上限。周期是分配检查预算，实际进程内存包含游戏资源和各模组对象。默认保留至少 2 GiB 或物理内存 10% 的系统余量，以及 1 GiB 提交余量。

同时兼容奶蛙愚者皮肤：预测计算期间隔离其 Spine 动画查询，实际出牌仍播放皮肤动画。

支持地图、奖励、事件、商店、休息、选牌、宝箱、水晶球和教学提示。目标范围为已经开始的单人跑局，结算后按设置生成游戏经验并停止领航。扩展模组的特殊内容依其提供的原生动作和当前求解器的兼容范围运行。决策质量和通关结果随模型、牌组、求解预算及游戏局面变化。

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

STS2 Solver、CombatSolver、RitsuLib、FoolSolverCompat、Godot、Harmony 以及游戏本体分别遵循各自的许可证或使用条款。本项目引用游戏的本地程序集，并通过运行时反射衔接两套求解器。游戏资源与这些第三方依赖由各自官方渠道提供。

### 说书人兼容补丁

STS2 Solver 0.2.3 对原生遗物说书人的注册缺口会导致 unsupported-relic STORYTELLER。Release 附带 Sts2Solver-Storyteller-fix.zip，包含本地兼容 DLL、许可证、原作者说明及源码差异。它补齐真实遗物语义并保留原有内容校验。安装到 mods/Sts2Solver，或待原作者版本包含该修复后使用原版。
