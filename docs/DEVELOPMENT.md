# 开发指南

## 环境与构建

游戏侧运行目标为 Godot 4.5.1 / .NET 9。安装 .NET 9 SDK，并通过本地游戏目录提供游戏程序集。

在仓库根目录运行，按实际安装位置设置游戏数据目录：

```powershell
$env:STS2_GAME_DATA = 'D:/SteamLibrary/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64'
dotnet build source/SpireAutopilot.csproj -c Release -m:1
```

输出为 `source/.godot/mono/temp/bin/Release/SpireAutopilot.dll`。将 DLL 与仓库根目录的 `SpireAutopilot.json` 一同放入游戏 `mods/SpireAutopilot/`，在下一次启动加载。

## 测试

离线逻辑测试使用 .NET 10 SDK：

```powershell
dotnet run --project source/tests/ProviderTests.csproj
dotnet run --project source/tests/PlanningTests.csproj
dotnet run --project source/tests/RunExperienceTests.csproj
dotnet run --project source/tests/MemoryTests.csproj
```

动画隔离测试使用游戏对应的 .NET 9 与 Harmony：

```powershell
dotnet run --project source/tests/VisualIsolationTests.csproj
```

`*Probe` 为开发联调工具。真实服务探针使用本地配置，会产生 API 用量。

提交验证结果时，分别记录离线测试和游戏内观察。实机验证覆盖设置保存、局外决策、自动战斗、暂停恢复与整局经验更新等受改动影响的流程。

## 实现约定

游戏状态读取和原生动作在 Godot 主线程执行，HTTP 请求异步执行。执行模型动作前校验当前局面与合法动作。会话、缓存与诊断采样保持资源上限。

模型协议为 Responses 或 Chat Completions。持续会话维护历史前缀和局面差分；服务兼容能力由配置及接口实际支持情况决定。

## 本地数据

配置位于游戏用户数据目录中的 `spire_autopilot.json`。API Key 使用当前 Windows 账户的 DPAPI 加密。

统一经验文件位于同一用户数据目录的 `spire_autopilot_run_experience/experience.md`。每局更新前读取已有总结，结合本局结果修订，后续决策按设置引用。

公开配置模板为仓库根目录的 `spire_autopilot.example.json`。提交内容限源码、公开文档和人工测试数据；个人配置、密钥、真实会话、存档、游戏程序集和第三方二进制由本地环境管理。

## 发行

玩家说明见 [README](../README.md)，许可与第三方署名见 [LICENSE](../LICENSE) 和 [NOTICE](../NOTICE)。发布包携带构建出的 DLL 与版本一致的清单，源码中的个人配置始终以公开模板替代。

## 诊断构建

发行构建仅包含运行功能。需要实机探针时，构建附加 `-p:EnableDiagnostics=true`；环境变量与探针产物仅用于本地联调。

## 请求上下文

F8 → 高级 → 查看本次模型上下文，显示当前完整快照、卡牌字典、合法动作及实际序列化请求体。文件随每次模型请求覆盖，授权头与密钥独立处理。游戏运行期间的下一次请求生成该文件；HTTP补丁沿完整会话重建当前状态。
