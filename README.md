# DD2 Damage Meter

[中文说明](README.zh-CN.md)

DD2 Damage Meter is an unofficial BepInEx plugin for *Darkest Dungeon II*. It adds an in-game overlay for combat statistics, battle logs, buff/debuff tracking, and run-level exports.

The plugin is intended for local gameplay analysis and debugging. It is not affiliated with Red Hook Studios.

## Features

- In-game damage meter for heroes and enemies.
- Tracks direct damage, DOT damage, overkill damage, raw damage taken, actual damage taken, healing, kills, crits, and avoidance rate.
- Player contribution tracking, including bonus damage, shield prevention, guard protection, and wasted shield.
- Resizable and draggable IMGUI overlay windows.
- Battle log with damage, healing, DOT, stress, deaths, and kills.
- Dedicated buff/debuff log with concrete status/token effect details.
- Run recording across multiple battles with merged run statistics.
- TXT battle report export and CSV run export.

## Hotkeys

| Key | Action |
| --- | --- |
| `F2` | Hide or show all overlay windows |
| `F3` | Reset current battle statistics |
| `F4` | Export a TXT battle report |

`F5` is intentionally avoided because it conflicts with the game's built-in screenshot hotkey.

## Overlay Controls

- `Heroes` / `Enemies`: switch the main meter between player and enemy teams.
- `Log`: open or close the battle log.
- `Buff/Debuff`: open or close the dedicated status log from the battle log window.
- `Record Run`: start or stop multi-battle run recording.
- `Run Stats`: show merged statistics for recorded battles.
- `Export CSV`: export run statistics to CSV.

## Requirements

- *Darkest Dungeon II*
- BepInEx installed in the game directory
- .NET SDK or build tools capable of building `net48`
- Game assemblies available from the game's `Darkest Dungeon II_Data/Managed` directory

## Build

1. Edit `Directory.Build.props`.
2. Set `BepInExDir` to the game's `BepInEx` folder.
3. Set `ManagedDir` to the game's `Darkest Dungeon II_Data/Managed` folder.
4. Build the project:

```powershell
dotnet build .\DD2DamageMeter.csproj
```

The compiled plugin DLL is produced under `bin\Debug\net48\` or `bin\Release\net48\`, depending on the selected configuration.

## Install

1. Build the project.
2. Copy `DD2DamageMeter.dll` to the game's `BepInEx\plugins\` directory.
3. Start the game with BepInEx enabled.
4. Wait until combat starts; the plugin registers game events once the event manager is ready.

## Exports

Exports are written next to the loaded plugin DLL:

- `DD2_Report_yyyyMMdd_HHmmss.txt`: current battle report.
- `DD2_Run_yyyyMMdd_HHmmss.csv`: recorded run statistics.

## Notes

- Combat statistics reset automatically when a new battle begins.
- Run recording can capture multiple battles and merge actor statistics across the run.
- Game updates may change internal event types or fields and can require plugin updates.
- Some values depend on game event semantics and reflection against game assemblies, so they should be treated as practical combat telemetry rather than an official combat log.