# Development guide

Running Grimes Golf from source, the switches for testing, how the code is organised, and where the course data
comes from. For the tools to install, see [System requirements](../README.md#to-build-from-source).

## Contents

- [Build and test](#build-and-test)
- [Running from source](#running-from-source)
- [Command-line switches](#command-line-switches)
- [Making a release build](#making-a-release-build)
- [Code layout](#code-layout)
- [Course data](#course-data)
- [Online play on one PC](#online-play-on-one-pc)

## Build and test

```bash
dotnet build GrimesGolf.sln
```

```bash
dotnet test tests/GrimesGolf.Tests
```

```bash
dotnet test tests/GrimesGolf.Physics.Tests
```

```bash
dotnet test tests/GrimesGolf.Online.Tests
```

`GrimesGolf.Tests` covers the Square protocol, course rules, rounds and scoring (against the hand-made holes in
`tests/GrimesGolf.Tests/TestCourse.cs` and against Deer Run). `GrimesGolf.Online.Tests` needs the .NET 10 SDK: it
hosts the relay in-process, plays scripted 18-hole rounds over an in-memory relay, and one round over real sockets.

## Running from source

Open `src/GrimesGolf.Godot` in the Godot 4.7 .NET editor and press Play, or build and run it from a terminal:

```bash
dotnet build src/GrimesGolf.Godot
```

```bash
godot --path src/GrimesGolf.Godot
```

## Command-line switches

Switches go after `--` (for example `godot --path src/GrimesGolf.Godot -- --range`). None are needed to play.

**For everyone:** `--autoconnect` connects even with auto-connect off, `--range` skips the main menu for the range,
`--course` skips it for the course (the unfinished round, if there is one), `--hole=N` starts on hole N.

**For development.** These runs skip the main menu and start on the range unless `--course` is given. Runs that hit
shots by themselves (`--demo-shots`, `--hole`, `--hit-at`, `--tune`) don't save or resume anything.

| Switch | What it does |
|---|---|
| `--demo-shots=N` | Hits N demo shots |
| `--hit-at=2,8.5` | Presses HIT SHOT at those times (seconds after start) |
| `--screenshot=file.png` | Saves the window and quits (after `--screenshot-delay=seconds`, default 4, or once the demo shots finish) |
| `--monitor-drawer`, `--settings`, `--menu`, `--setup-page`, `--scorecard` | Opens that screen (the scorecard just before the screenshot) |
| `--reconnecting`, `--loading-screen`, `--celebrate=Birdie` | Shows the reconnecting banner, the loading screen, or a hole's score announcement |
| `--players=Ann,Bob,Cat:L` | Players for this run (`:L` = left-handed) |
| `--text-size=Large`, `--graphics=Ultra` | Display settings for this run |
| `--game=Scramble`, `--holes=Back9`, `--wind=Breezy`, `--weather=Wet` | Game setup for this run (game: Stroke, Scramble, BestBall, MatchPlay) |
| `--green-read` | Turns on the green heat map and grid |
| `--tuner`, `--tuner-tab=Lies` | Opens the physics tuner (the screenshot also saves it as `file-tuner.png`), on a tab (`Flight`, `Lies`, or a surface such as `Green`) |
| `--tune=flight.drag=1.2` | Changes one tuning value after the demo shots and replays the last one |

**Online play (debug builds only):**

| Switch | What it does |
|---|---|
| `--relay=ws://127.0.0.1:5080/ws` | Fills in the relay address |
| `--data=user://host` | Keeps this copy's settings and rounds in their own folder (two copies on one PC) |
| `--no-bluetooth` | Doesn't start Bluetooth |
| `--fake-monitor` | HIT SHOT stands in for a launch monitor; `--fake-monitor=sessions/2026-11-04_120000.jsonl` plays a saved session's shots instead |
| `--auto-play` | The fake monitor hits by itself whenever it's this copy's turn |

## Making a release build

`./tools/export.ps1` exports the Windows build to `dist\Grimes Golf` and
puts a **Grimes Golf** shortcut on the desktop. The preset is `src/GrimesGolf.Godot/export_presets.cfg`; the
version comes from `application/config/version` in `project.godot` and is shown in the top bar. It needs the Godot
export templates for the editor's version (Editor > Manage Export Templates, or unzip the `.tpz` into
`%APPDATA%\Godot\export_templates\4.7.2.stable.mono`) and uses `src/GrimesGolf.Godot/GrimesGolf.Godot.sln`.
Physics presets go in `tuning\` next to the exe (existing ones are kept on re-export); saved data stays in
`%APPDATA%`.

## Code layout

The Godot code is split by topic: `Main.cs` (set-up, the "shot finished" event, saving shots), `Main.Monitor.cs`
(Bluetooth, auto-connect and reconnecting), `Main.Shots.cs` (shots and the range), `Main.Course.cs` (rounds and
turns), `Main.Menus.cs` (main menu and in-game menu), `Main.Controls.cs` (keyboard, numpad, gamepad and clicker),
`Main.Settings.cs`, `Main.DevTools.cs`; and `Hud.cs` (top bar, banner, toasts) with `Hud.Drawer.cs`, `Hud.Range.cs`,
`Hud.Course.cs`, `MainMenu.cs`, `MenuOverlay.cs`, `ShotControls.cs`, `MapOverlay.cs`, `OnlinePanel.cs`,
`SettingsPanel.cs` and `PlayersPanel.cs`.
Multiplayer turn order lives in `GrimesGolf.Core/Course/Match.cs`.

Online play is `Main.Online.cs` (the game side) and `OnlinePanel.cs` (the lobby); the networking is in
`src/GrimesGolf.Online` and the server in `src/GrimesGolf.Relay`.

Carry/total/apex, bounce and roll come from the app's own physics engine (`src/GrimesGolf.Physics`, see its README:
aerodynamic flight, bounce and roll over the course ground, and the lie model), not from the monitor, so expect them
to differ somewhat from the Square app. The engine's coefficients and the lie effects are provisional
and not yet validated against measured shots; every one can be adjusted in the physics tuner (the values and their
defaults are listed in `GrimesGolf.Core/PhysicsTuning.cs`, and `ActivePhysics` feeds them to every simulation the
app runs). Course rules (lies, roll, holing, penalties) live in
`GrimesGolf.Core/Course` and are unit-tested (against hand-made holes in `tests/GrimesGolf.Tests/TestCourse.cs`). Trees are scenery only
(balls don't hit them).

The world and UI are built in C# (`RangeWorld`, `CourseWorld`, `ShotPlayer`, `CameraRig`, `Hud`); `Main.tscn` is
just the root.

## Course data

### Mapped courses (OpenStreetMap)

`OsmCourse.cs` builds a course from OpenStreetMap data: `golf=hole` lines (ref, par, name), `golf=tee`, `green`,
`fairway`, `bunker`, water hazards / `natural=water`, ditches and streams, cart paths (`golf=cartpath`, or paths
tagged `golf_cart=yes`; 2.5 m wide unless `width` is tagged, and `bridge=yes` ones carry the path over water), woods,
and the `leisure=golf_course` outline (out of bounds).
Holes without a mapped fairway get a generated one; duplicate or missing hole numbers are fixed by routing (the
next tee nearest the last green). What it fixed or generated is printed to the log when the course loads.

Cart paths are concrete: a ball landing on one bounces higher and runs further (tune it under Physics → Bounce &
roll → Cart path). A ball that stops on a path gets free relief, moved to the nearest point no nearer the hole that's
0.6 m clear of every path (room for the stance) and on grass, not in a bunker, on a green or in water.

The data is embedded from `src/GrimesGolf.Core/Course/Data/deer-run.osm`. To pick up map edits (for example, after
tracing Deer Run's fairways, ponds and woods on openstreetmap.org), re-download it and rebuild:

```
./tools/fetch-osm-course.ps1
dotnet build src/GrimesGolf.Godot
```

Map data © OpenStreetMap contributors, under the [Open Database Licence](https://opendatacommons.org/licenses/odbl/);
the app shows this credit under the scorecard.

Ground elevation comes from the USGS 3D Elevation Program (1 m lidar DEM, public domain), embedded as
`src/GrimesGolf.Core/Course/Data/deer-run.dem`; re-download it with `./tools/fetch-elevation.ps1` (same bounding box
as the map). It's resampled to a 2 m grid (`Terrain`), lightly smoothed, and each green is reshaped as a built
surface (its overall tilt capped at 3.5%, a little of its contour kept) so lidar noise doesn't read as bumps.

The trees come from the same lidar, embedded as `src/GrimesGolf.Core/Course/Data/deer-run.trees.csv.gz` and read
by `TreeSurvey.cs`; the course keeps the ~9,900 within 150 m of it, and the scenery plants each one at its height
and crown width (tall, narrow ones mostly as conifers, wide ones as broadleaf), leaving only the camera spot behind
each tee clear. A course without tree data still gets trees planted at random between the holes. Re-make the file
with `./tools/fetch-canopy.ps1` (Python 3 with numpy and `laspy[lazrs]`; `-Install` installs them): it downloads
the 3DEP point cloud over the box from the public USGS Entwine point tiles on AWS (newest survey, cached in
`%LOCALAPPDATA%\SquareRange\ept-cache`), makes a canopy height grid (highest return minus ground), and finds the
tree tops with their height and crown radius, grouped as single trees, groups of 2-5 and dense woods (one row per
tree; `-Preview` also draws a PNG, in the cache folder, to check it by eye). Rebuild afterwards.


## Online play on one PC

Run a relay locally:

```bash
ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet run --project src/GrimesGolf.Relay
```

Then two copies of the game (a debug build), one hosting and one joining:

```bash
godot --path src/GrimesGolf.Godot -- --relay=ws://127.0.0.1:5080/ws --data=user://host --no-bluetooth --fake-monitor
```

```bash
godot --path src/GrimesGolf.Godot -- --relay=ws://127.0.0.1:5080/ws --data=user://friend --no-bluetooth --fake-monitor --auto-play
```

In the first, pick Play Online and Host a Lobby; in the second, Join with the code it shows. Plain `ws://` is only
accepted for a relay on this machine or the local network, or in a debug build; real relays use `wss://`.
