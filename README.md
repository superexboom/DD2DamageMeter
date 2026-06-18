# DD2 Damage Meter

[中文说明](README.zh-CN.md)

DD2 Damage Meter is an unofficial BepInEx 5 plugin for *Darkest Dungeon II*. It adds an in-game combat statistics overlay, battle logs, contribution tracking, and export tools for single battles and recorded runs.

This project is for local gameplay analysis, mod debugging, and post-battle review. It is not affiliated with Red Hook Studios.

## Highlights

- Tracks hero and enemy damage, DOT damage, effective damage, raw damage, overkill filtering, healing, kills, crits, avoidance, stress events, and death events.
- Tracks support contribution such as Strength, Vulnerable, Combo, Guard, Block, Dodge, shield prevention, DOT prevention, and selected floor-effect sources.
- Filters invalid corpse damage and overheal from normal totals.
- Provides draggable and resizable IMGUI windows for live combat stats, battle logs, and buff/debuff logs.
- Supports multi-battle run recording with merged export output.
- Exports readable battle reports plus CSV run summaries.
- Exposes a lightweight multiplayer/companion API used by DD2SteamMP and DD2DamageMeterAdvancedStats.

For deeper per-source breakdowns, install [DD2DamageMeterAdvancedStats](https://github.com/superexboom/DD2DamageMeterAdvancedStats) alongside this plugin.

## Hotkeys

| Key | Action |
| --- | --- |
| `F2` | Hide or show all overlay windows |
| `F3` | Reset current battle statistics |
| `F4` | Export current battle report |

`F5` is intentionally avoided because it conflicts with the game's screenshot hotkey.

## In-Game Controls

- `Heroes` / `Enemies`: switch the main table between teams.
- `Log`: open the combat log.
- `Buff/Debuff`: open the status log from the combat log window.
- `Record Run`: start or stop multi-battle recording.
- `Auto Rec`: remember whether recording should start automatically.
- `Run Stats`: open merged run statistics.
- `Export CSV`: export the recorded run.
- `Export Dir`: choose where reports are written.

## Installation

Install BepInEx 5 for the normal Unity/Mono build of *Darkest Dungeon II*, then extract the release zip into the game directory.

Expected release layout:

```text
Darkest Dungeon II/
└─ BepInEx/
   └─ plugins/
      └─ DD2DamageMeter/
         └─ DD2DamageMeter.dll
```

Start the game through Steam with BepInEx enabled. The overlay begins updating when the game event manager is ready, usually after combat starts.

## Exports

By default, exports are written next to the loaded plugin DLL unless a custom export folder is configured.

- `DD2_Report_yyyyMMdd_HHmmss.txt`: current battle report.
- `DD2_Run_yyyyMMdd_HHmmss.csv`: recorded run summary.

Settings are stored by BepInEx in `BepInEx/config/com.dd2.damagemeter.cfg`.

## Requirements

- *Darkest Dungeon II*
- BepInEx 5.x, tested with BepInEx 5.4.23.5
- Unity/Mono BepInEx build, not IL2CPP
- .NET SDK or build tools capable of building `net48`
- Local game assemblies from `Darkest Dungeon II_Data/Managed`

## Build

1. Copy `Directory.Build.props.example` to `Directory.Build.props`.
2. Set `BepInExDir` to the game's `BepInEx` folder.
3. Set `ManagedDir` to the game's `Darkest Dungeon II_Data/Managed` folder.
4. Build:

```powershell
dotnet build .\DD2DamageMeter.csproj -c Release
```

The source repository intentionally excludes game assemblies, decompiled game code, local install paths, exported assets, and build output.

## Compatibility Notes

- Game updates can change internal event fields and may require plugin updates.
- Values are practical telemetry from game events and runtime patches, not an official combat log.
- Advanced per-source analysis belongs in DD2DamageMeterAdvancedStats so the base meter can stay focused and stable.
