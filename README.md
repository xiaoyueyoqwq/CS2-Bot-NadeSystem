# CS2-Bot-NadeSystem

Maintained fork of [ed0ard/CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem).
The loaded plugin name is still `NadeSystem` (1.2.2).

This is a standalone plugin repo. CS2-Bot-Improver is the combined
distribution; do not develop NadeSystem on Improver feature branches.

Relative to upstream 1.2.1 (`21788dc`, CSS API, no RayTrace) this fork adds:

1. Native/coach modes (`native`, `coach_easy`, `coach_normal`, `coach_hard`) that leave grenade buy/throw/physics to Valve CCSBot
2. Replay/lineup scan and special projectile generation only run for `less|normal|more|max`
3. `NadeSystem.json` `Mode` plus `bot_nades` accept the extra modes and apply Valve buy weights

Live servers that still run the coupled Bot-Improver NadeSystem package must not mix this DLL/config with that package.

CS2-Bot-NadeSystem is a plugin based on CounterStrikeSharp that allows bots to replay lineups recorded by players and throw nades deftly according to the situation.

# Features
1. Bots can "rethrow" the nades recorded by players on every map.

2. Bots can make their own decisions to throw nades or not when passing by recorded lineups.

3. Players can record lineups in an intuitive way on every map for bots to use.
# Requirement
[NadeLauncher](https://github.com/StefanKunde/NadeLauncher) (only needed to record new lineups)

Ray-Trace is not required after upstream 2026-09-04 (`55da210`); this fork keeps that CSS API base.

# Installation
1. Build this repo (`dotnet build -c Release`) and upload `NadeSystem.dll` from `bin/Release/net10.0/`

2. Extract the `NadeSystem` folder and upload it to `game/csgo/addons/counterstrikesharp/plugins` on your server

3. If you record lineups, upload `NadeLauncher.dll` from [here](https://github.com/StefanKunde/NadeLauncher/releases) to `game/csgo/addons/counterstrikesharp/plugins/NadeLauncher/NadeLauncher.dll` on your server

# Commands
`bot_nades`  
Shows the current nade throwing mode

`bot_nades off`  
Bots won't throw any nades

`bot_nades less`  
Bots use the same decision logic as normal mode with lower count limits

`bot_nades normal`  
Bots follow almost the same count limits as human players (default)

`bot_nades more`  
Bots use the same decision logic as normal mode with higher count limits (recommended)

`bot_nades max`  
Bots have minimal limitations and think less before throwing nades

`bot_nades native`  
Valve CCSBot owns grenade buy/throw/physics. Plugin does not scan lineups or spawn replay projectiles.

`bot_nades coach_easy` / `coach_normal` / `coach_hard`  
Same as native, with higher Valve buy chance and grenade weights. `coach_hard` uses chance 80, smoke/flash/HE/molotov 6/4/4/6, decoy 1.

# Instructions
## Record Lineups
Record lineups using [NadeLauncher](https://github.com/StefanKunde/NadeLauncher).

<img width="2560" height="1440" alt="730_71" src="https://github.com/user-attachments/assets/1b380d1c-5c72-4715-bec1-e0f5e920cc9a" />

(You can find your lineup libraries in `game\csgo\addons\counterstrikesharp\plugins\NadeLauncher\data\lineups`)

## Add Descriptions
If you want the bots to **only** use the lineups at **round start**, add uppercase `T` for Terrorists and uppercase `CT` for Counter-Terrorists at the start of lineup descriptions.

If your lineup needs to **break the window first**, stand in the same position and throw a flashbang to smash the window, and add `decoy` to the lineup description. Then the bots will throw a decoy first to break the window before triggering your lineup.

## Convert Lineups
Run **convert_lineups.py** in your CMD or PowerShell with the command `python convert_lineups.py "NadeLauncherPath" "NadeSystemPath"`.

(For example, `python convert_lineups.py "C:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\addons\counterstrikesharp\plugins\NadeLauncher" "C:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\addons\counterstrikesharp\plugins\NadeSystem"`)

If you don't have Python installed on your computer, double-click **convert_lineups.exe** to convert lineups automatically.

(After conversion, you can check lineup libraries for bots in `game\csgo\addons\counterstrikesharp\plugins\NadeSystem\grenades`)

## So bots can "rethrow" the nades recorded by you now

<img width="2560" height="1440" alt="730_73" src="https://github.com/user-attachments/assets/ac569784-374f-494c-a4be-bdd89d0347c8" />

# Credits
Special thanks to [QQundalf](https://github.com/StefanKunde) and [DANK1NG1I45l4](https://github.com/DANK1NG1I45l4).
