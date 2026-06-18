# DD2 Damage Meter

[English README](README.md)

DD2 Damage Meter 是一个用于《Darkest Dungeon II》的非官方 BepInEx 5 插件。它提供游戏内战斗统计覆盖层、战斗日志、贡献统计，以及单场战斗和多场战斗记录导出。

本项目用于本地战斗分析、Mod 调试和战后复盘，不隶属于 Red Hook Studios。

## 主要功能

- 统计我方和敌方的直接伤害、DOT 伤害、有效伤害、理论承伤、溢出伤害过滤、治疗、击杀、暴击、闪避、压力事件和死亡事件。
- 统计辅助贡献，包括力量、易伤、连击、守护、格挡、闪避、护盾抵消、阻止 DOT 伤害，以及部分地板效果来源。
- 剔除对尸体造成的无效伤害和溢出治疗，减少假账。
- 提供可拖拽、可缩放的 IMGUI 窗口，用于实时统计、战斗日志和 Buff/Debuff 日志。
- 支持跨多场战斗的整局记录，并可合并导出。
- 支持导出可读战斗报告和 CSV 整局统计。
- 暴露轻量接口，供 DD2SteamMP 和 DD2DamageMeterAdvancedStats 读取数据。

如果需要更细的来源拆分，请同时安装 [DD2DamageMeterAdvancedStats](https://github.com/superexboom/DD2DamageMeterAdvancedStats)。

## 快捷键

| 按键 | 操作 |
| --- | --- |
| `F2` | 隐藏或显示全部覆盖窗口 |
| `F3` | 重置当前战斗统计 |
| `F4` | 导出当前战斗报告 |

插件刻意避开 `F5`，因为它会和游戏截图快捷键冲突。

## 游戏内控制

- `Heroes` / `Enemies`：切换我方和敌方统计表。
- `Log`：打开战斗日志。
- `Buff/Debuff`：从战斗日志窗口打开状态日志。
- `Record Run`：开始或停止多场战斗记录。
- `Auto Rec`：记住是否自动开始记录。
- `Run Stats`：查看整局合并统计。
- `Export CSV`：导出已记录的整局统计。
- `Export Dir`：选择报告输出目录。

## 安装

先为普通 Unity/Mono 版《Darkest Dungeon II》安装 BepInEx 5，然后把 release zip 解压到游戏目录。

预期目录结构：

```text
Darkest Dungeon II/
└─ BepInEx/
   └─ plugins/
      └─ DD2DamageMeter/
         └─ DD2DamageMeter.dll
```

通过 Steam 启动已启用 BepInEx 的游戏。插件会在游戏事件管理器就绪后开始刷新，通常是在进入战斗后。

## 导出

默认情况下，导出文件会写到插件 DLL 所在目录；也可以在界面中设置自定义导出目录。

- `DD2_Report_yyyyMMdd_HHmmss.txt`：当前战斗报告。
- `DD2_Run_yyyyMMdd_HHmmss.csv`：已记录整局统计。

设置由 BepInEx 保存到 `BepInEx/config/com.dd2.damagemeter.cfg`。

## 环境要求

- 《Darkest Dungeon II》
- BepInEx 5.x，当前使用 BepInEx 5.4.23.5 测试
- Unity/Mono 版 BepInEx，不是 IL2CPP 版
- 能够构建 `net48` 的 .NET SDK 或构建工具
- 来自 `Darkest Dungeon II_Data/Managed` 的本地游戏程序集

## 构建

1. 将 `Directory.Build.props.example` 复制为 `Directory.Build.props`。
2. 将 `BepInExDir` 设置为游戏的 `BepInEx` 目录。
3. 将 `ManagedDir` 设置为游戏的 `Darkest Dungeon II_Data/Managed` 目录。
4. 构建：

```powershell
dotnet build .\DD2DamageMeter.csproj -c Release
```

源码仓库刻意排除了游戏程序集、反编译游戏代码、本地安装路径、导出资源和构建产物。

## 兼容性说明

- 游戏更新可能改变内部事件字段，因此可能需要同步更新插件。
- 数值来自游戏事件和运行时补丁，是实用战斗遥测，不是官方战斗日志。
- 更细的来源分析放在 DD2DamageMeterAdvancedStats 中，基础 Damage Meter 只保留稳定常用统计。
