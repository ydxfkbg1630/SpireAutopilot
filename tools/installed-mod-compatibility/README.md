# 已安装模组的 v0.113 API 兼容修补器

此工具为本机确认的特定 DLL 版本生成兼容副本，保留模组原有行为。输入由文件名及完整 SHA256 锁定；上游更新后需要重新审查。输出目录与原始安装目录分离。

使用 .NET 10 SDK；Mono.Cecil 0.11.6 通过 NuGet 自动恢复。

```powershell
dotnet run --project PatchTool.csproj -- "游戏/data_sts2_windows_x86_64" "补丁输出目录" "原始/DanmakuSpire.dll" "原始/P3Makoto.dll" "原始/NecrobinderOstyVaalmonicaMod.dll" "原始/UnifiedSavePath.dll" "原始/ItsLoading.dll"
```

## 修复范围

- DanmakuSpire、P3Makoto、NecrobinderOstyVaalmonicaMod 的角色选择回调：从当前按钮的 `Character` 读取原来独立传入的角色，然后调用原回调，使原 `PatchAll` 完整执行。
- DanmakuSpire 商店：接入当前 `OnTryPurchaseUnsynchronized`，保留原来的购买前快照及异步完成包装；根据原生条目类型识别删牌，使购买与删牌事实各生成一次。
- P3Makoto 伤害入口：仅给伤害补丁的精确参数筛选补上新增的可选 `modifyDamageHookType`，其余筛选维持原行为。
- UnifiedSavePath：明确 `GetProfileDir(int)` 单参目标，双参显式 force 调用维持游戏本来语义。原来的 getter/setter 规则保留；修改前应核对实际用户数据路径。
- ItsLoading：新建启动面板时通过 `CallDeferred("add_child", node)` 挂到根节点，避开根节点正在初始化子节点的时段。

工具生成 `patch-report.json`，包含输入输出 SHA256 与每类修改数量。第三方 DLL 由用户本机安装提供，公开源码包只提供工具与说明。

## 安装与恢复

先将原始 DLL 复制到独立备份目录并保存 SHA256，再把审核过的对应输出 DLL 复制到原安装路径。Steam 工坊更新可能恢复原 DLL；重新运行前由输入 SHA256 判断当前版本。

恢复时把备份 DLL 复制回原路径，并重开游戏。存档、模型配置、API Key 与模组设置由原位置继续使用。验证记录分别说明主菜单加载、Harmony 入口、原生商店事实和实际战斗范围。
