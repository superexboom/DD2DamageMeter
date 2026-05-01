# DD2 Damage Meter

[English README](README.md)

DD2 Damage Meter 是一个用于《Darkest Dungeon II》的非官方 BepInEx 插件。它会在游戏内显示战斗统计覆盖层，并提供战斗日志、Buff/Debuff 记录、多场战斗统计和导出功能。

本插件主要用于本地游玩分析和调试，不隶属于 Red Hook Studios。

## 功能

- 游戏内英雄和敌方伤害统计面板。
- 统计直接伤害、DOT 伤害、过量伤害、理论承伤、实际承伤、治疗、击杀、暴击和闪避率。
- 玩家贡献统计，包括增伤贡献、护盾抵消、守护保护和浪费护盾。
- 可拖动、可缩放的 IMGUI 覆盖窗口。
- 战斗日志，记录伤害、治疗、DOT、压力、死亡和击杀。
- 独立 Buff/Debuff 日志，记录具体状态、Token 和效果细节。
- 支持跨多场战斗的整局记录与合并统计。
- 支持导出 TXT 战斗报告和 CSV 整局统计。

## 快捷键

| 按键 | 操作 |
| --- | --- |
| `F2` | 隐藏或显示全部覆盖窗口 |
| `F3` | 重置当前战斗统计 |
| `F4` | 导出 TXT 战斗报告 |

插件刻意避开 `F5`，因为它会和游戏内置截图快捷键冲突。

## 窗口控制

- `Heroes` / `Enemies`：在英雄队伍和敌方队伍统计之间切换。
- `Log`：打开或关闭战斗日志。
- `Buff/Debuff`：从战斗日志窗口打开或关闭独立状态日志。
- `Record Run`：开始或停止多场战斗记录。
- `Run Stats`：查看已记录战斗的合并统计。
- `Export CSV`：导出整局统计 CSV。

## 环境要求

- 《Darkest Dungeon II》
- 游戏目录中已安装 BepInEx
- 能够构建 `net48` 的 .NET SDK 或构建工具
- 来自游戏 `Darkest Dungeon II_Data/Managed` 目录的程序集文件

## 构建

1. 编辑 `Directory.Build.props`。
2. 将 `BepInExDir` 设置为游戏的 `BepInEx` 目录。
3. 将 `ManagedDir` 设置为游戏的 `Darkest Dungeon II_Data/Managed` 目录。
4. 构建项目：

```powershell
dotnet build .\DD2DamageMeter.csproj
```

编译后的插件 DLL 会输出到 `bin\Debug\net48\` 或 `bin\Release\net48\`，取决于构建配置。

## 安装

1. 构建项目。
2. 将 `DD2DamageMeter.dll` 复制到游戏的 `BepInEx\plugins\` 目录。
3. 启动已启用 BepInEx 的游戏。
4. 进入战斗后等待插件注册游戏事件；事件管理器就绪后覆盖层会开始工作。

## 导出

导出文件会写到已加载插件 DLL 所在目录：

- `DD2_Report_yyyyMMdd_HHmmss.txt`：当前战斗报告。
- `DD2_Run_yyyyMMdd_HHmmss.csv`：已记录整局统计。

## 注意事项

- 新战斗开始时，当前战斗统计会自动重置。
- 整局记录可以跨多场战斗捕获并合并角色统计。
- 游戏更新可能改变内部事件类型或字段，因此可能需要同步更新插件。
- 部分数值依赖游戏事件语义和对游戏程序集的反射，应视为实用战斗遥测，而不是官方战斗日志。