# SimracingBR Overlay

**English** · [Português](README.pt-BR.md)

Telemetry overlay for sim racing games: pedal input graph and bars (clutch, brake, throttle) that highlight when ABS/TC cut in, ABS/TC/TCC levels, gear, speed, shift lights, a rotating steering wheel and a fuel row (current fuel, consumption per lap, laps remaining).

| Game | Data source | Status |
|---|---|---|
| **Assetto Corsa EVO** | `Local\acevo_pmf_*` | Ready (same code as [EvoHud](https://github.com/eliezzer/assetto-corsa-evo-hud)) |
| **Assetto Corsa Competizione** | `Local\acpmf_*` | Implemented, not yet tested in game |
| **Automobilista 2** | `$pcars2$` (Project CARS 2 format) | Implemented, not yet tested in game |
| **Le Mans Ultimate** | `LMU_Data` (LMU's native shared memory) | Implemented, not yet tested in game |

The app detects which game is running by itself. No SimHub and nothing to install: it compiles with the .NET Framework that ships with Windows.

> [!IMPORTANT]
> **The overlay only appears while the car is on track.** It stays hidden in menus, replays, pause, and while the car is parked in the pit box or garage. If you see nothing after launching the game, that is expected: start driving.

## Per-game setup

- **AC EVO, ACC and LMU**: nothing to do, shared memory is on by default.
- **Automobilista 2**: in *Options → System*, set **Shared Memory** to **Project CARS 2**.
- Run the game **windowed** or **borderless** (overlays can't draw over exclusive fullscreen).

## Use

1. Run `SimracingBR.exe` (keep the `assets` folder next to it).
2. A green and yellow "S" icon appears in the system tray. Right-click it for:
   - **Settings...**: choose what the HUD shows, size and transparency.
   - **Unlock / Lock position**: unlock to drag the HUD (also `Ctrl+Shift+L`, or double-click the tray icon). You can also hover the mouse over the HUD and click the **UNLOCK** / **LOCK** button that appears in its top-right corner.
   - **Size**, **Reset position**, **Demo mode**, **Hide when not driving**, **Quit**.
3. Resize with `Ctrl+Shift++` / `Ctrl+Shift+-`, or unlock and scroll / drag the yellow corner.

The **TCC** (TC cut) chip only shows for games and cars that have that setting (AC EVO, ACC, and some LMU cars).

## What each game provides

| Data | AC EVO | ACC | AMS2 | LMU |
|---|---|---|---|---|
| Pedals, gear, RPM, speed | ✓ | ✓ | ✓ | ✓ |
| Wheel rotation | ✓ (game degrees) | `AccSteerLock` | `Ams2SteerLock` | ✓ (game wheel range) |
| ABS / TC levels | ✓ | ✓ | ✓ | ✓ |
| TCC level | ✓ | ✓ | – | when the car has it |
| ABS active | ✓ | ✓ | ✓ | ✓ |
| TC active | ✓ | ✓ | – (not published) | ✓ |
| Fuel per lap | from the game | from the game | measured by the HUD | measured by the HUD |

ACC and AMS2 only publish steering as -1..1, so the HUD converts it with a lock-to-lock rotation (default `540`). Set `AccSteerLock=` / `Ams2SteerLock=` in `settings.ini` to match your car.

## Configure

`settings.ini` is created next to the `.exe` (edit it while the app is closed):

| Key | Default | What it does |
|---|---|---|
| `Scale` | `1.0` | HUD size, 0.4 to 3.0 |
| `Opacity` | `0.94` | Window opacity |
| `Wheel` | `assets\wheel.png` | Steering wheel image (square PNG, transparent background) |
| `InvertSteer` | `0` | Flip the wheel rotation |
| `AccSteerLock` / `Ams2SteerLock` / `LmuSteerLock` | `540` | Lock-to-lock rotation in degrees (LMU uses the game's value when present) |
| `ColorClutch` / `ColorBrake` / `ColorThrottle` | blue / red / green | Pedal colors |
| `ColorAbs` / `ColorTc` | orange / yellow | Color while ABS/TC cut in |
| `ColorShiftA` / `ColorShiftB` | red / dark | Shift-point flash |
| `ShowGraph`, `ShowBars`, `ShowClutch`, `ShowAids`, `ShowGear`, `ShowSpeed`, `ShowShiftLights`, `ShowWheel`, `ShowFuel`, `ShowFuelPerLap`, `ShowLapsLeft` | `1` | Show or hide each part |
| `AidColors` | `1` | Recolor the pedals while ABS / TC cut in |
| `AutoHide`, `HideInPitBox`, `HideWhenGameNotFocused` | `1` | Hide rules |

## Build

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Produces `dist\SimracingBR.exe` (+ `dist\assets`).

## Diagnostics

`SimracingBR.exe --probe` writes `probe.log` for 40 s with the raw values of every supported game. Run it with the car on track and attach the file if something looks wrong. `SimracingBR.exe --demo` starts with demo data.

## Layout

```
src/Core/Model.cs           shared telemetry model, shared-memory base class, settings
src/Sources/AcEvoSource.cs  Assetto Corsa EVO
src/Sources/AccSource.cs    Assetto Corsa Competizione
src/Sources/Ams2Source.cs   Automobilista 2
src/Sources/LmuSource.cs    Le Mans Ultimate
src/Sources/GameSources.cs  game auto-detection + demo data
src/UI/HudView.cs           HUD drawing
src/UI/HudWindow.cs         window, tray, hotkeys, settings, auto-hide
```

To add a game: write an `ITelemetrySource` (usually derived from `ShmSource`) that fills in `Tele`, and add it to `GameSources`.

## Sectors overlay

A second overlay (switch it on/off and configure it in **Settings**, next to the pedals overlay): current / best / last lap, the leader's best lap (LMU and AMS2) and S1 / S2 / S3 split into sub-sectors.

The games only publish the 3 main sectors, so the overlay times the sub-sectors itself from the lap position and the lap time (every game publishes both). Colors, compared with your own laps in the session: **purple** = your fastest ever on that stretch, **green** = faster than the same stretch of your best lap, **yellow** = slower, **gray** = nothing to compare yet. The first clean lap (no pit lane, no cut) sets the reference.

Notes: the main-sector boundaries are learned from the game's sector changes (ACC, LMU, AMS2) and remembered per track in `settings.ini`; AC EVO does not publish the sector, so it uses thirds of the lap. `--probe` now also logs the lap position, sector and lap times. Keys: `SectorsSubs`, `SectorsShowTimes`, `SectorsShowLeader`, `SectorsShowSectorTimes`, `SectorsOpacity`, `SectorsScale`, `PedalsEnabled`, `SectorsEnabled`, `ColorSecPurple` / `ColorSecGreen` / `ColorSecYellow`.
