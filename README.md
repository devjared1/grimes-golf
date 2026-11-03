# Square Golf → GSPro Connector (unofficial)

A small Windows console app that reads shots from a **Square Golf Omni** over Bluetooth LE and
forwards them to **GSPro** using the GSPro Open Connect v1 protocol (TCP `127.0.0.1:921`).

## Use it

1. In GSPro, choose **Other / Open API** as the launch monitor and start GSPro.
2. Turn on the Square. **Close the Square phone/tablet app** (only one client can hold the link).
   **Do not pair the Square in Windows Bluetooth settings**; it does not pair.
3. Run `SquareGsproConnector.exe`. It scans for `SquareGolf(XXXX)`, connects, arms the monitor,
   and sends each shot to GSPro. It reconnects automatically to both sides.
4. Club and handedness follow GSPro (the connector forwards GSPro's club changes to the monitor).

Settings live in `connector.json` next to the exe (host/port, exact device name if you have
several monitors, club-data timeout, advanced vs standard spin, default club).

## Square Range 3D (Godot)

`src/SquareGspro.Godot` is a standalone practice app built in **Godot 4.7 (.NET edition)** that doesn't need GSPro.

- **Main menu** (where the app opens): **New Game** (a list of courses with their holes, par, length and a short
  description, and who's playing; **Start Round**), **Resume Game** (shown when a round is unfinished, with its
  course, hole and players), **Driving Range**, **Settings** and **Quit**, plus the launch monitor's status (with a
  button for the monitor drawer) and the version.
- **Top bar** (in play): where you are (Driving range or the course), the player chip (with 2+ players: whose turn
  it is, or on the range who the shots are saved under; click it there for the next player), the launch monitor
  status (ready state and battery; click it for the monitor drawer) and **Menu** (`M`, `Esc` or gamepad Start).
- **In-game menu**: on a course, Scorecard, Settings, Previous Hole, Next Hole, Restart Hole, Add / Remove Players
  (players can join on the current hole, where they start from the tee and earlier holes don't count for them, or
  leave), Restart Game and End Game (keep the round for Resume Game, or discard it); on the range, Settings, Clear
  Session and Main Menu. Both have the camera mode, the launch monitor drawer and the physics tuner.
- **Hitting from the screen** (no monitor needed): a **power** slider (1-100% of the club's full carry; for the
  putter, of the roll shown beside it), **shape** (Draw / Straight / Fade: starts right and curves back left for a
  right-hander, or the opposite) and **spin** (Low / Medium / High: lower flight with more roll, or higher with more
  stopping), then **HIT SHOT** (`Space`). On a course the power is set for each new shot (the distance it plays, from
  that lie; putts get the pace from the green read) and can be changed (`↑ / ↓`, Shift = 5%). A **Ready / Not
  ready** light beside HIT SHOT is green once the last shot has finished and, with a monitor connected, the monitor
  sees a ball ready to hit. Shape and spin go back to Straight and Medium after every shot.
- **Camera**: in Follow mode (`C`) it chases the ball until it stops. Before a shot, **Zoom** (`Z`) takes it down
  the aim line to look at where the ball will land with the aim and power set; it follows aim and power changes,
  and comes back behind the ball when pressed again or when the shot is hit. Before the first tee
  shot on each hole it flies down the hole from behind the tee to the green (`Space` or `Esc` skips it; can be
  turned off in Settings).
- **Results wait for the ball**: the shot's numbers (range) and the result card, the next shot's distance, lie and
  club (course) only appear once the ball has stopped.
- **Monitor drawer** (`L` or the status chip): every Square it can hear over Bluetooth (with signal strength), Connect
  / Disconnect, the monitor's serial, hardware, firmware (LM / MMI / launcher), uptime, state, ball-in-position
  status and battery, the left-handed setting, and the log.
- **Connecting**: with auto-connect on (the default) the app connects by itself, to the monitor used last time, or
  to the strongest one it hears if that one isn't found within 8 s. If the link drops, a big banner says
  "Launch monitor disconnected — reconnecting…" and it keeps retrying (every few seconds) until it's back; Disconnect
  in the drawer stops that.
- **Range**: each shot flies down a 3D range with a camera that follows the ball, trails, landing rings and a
  minimap. Aim left and right (`← / →`, the ‹ › buttons, or click on the minimap), or step through the target
  flags (50-300 yd) to aim straight at one (`F` / `Shift+F`, gamepad Y, or the target button); a line on the range
  and the minimap shows where the shot is aimed and how far the power set carries it. The bottom bar shows Carry and Total large, with ball and club speed, smash, launch, direction, apex,
  spin, spin axis, descent, offline, roll and hang time. The right column has the session history and a per-club
  summary (average carry and total, offline spread).
- **Course**: pick a course with New Game:
  - **Deer Run (Moulton, AL)**, all 18 holes imported from OpenStreetMap: real routing, hole lengths and pars,
    dog-legs, tee, green and bunker shapes, with the course property line as out of bounds. Fairways are mapped for
    holes 1-4; on the others 35-yd fairways are generated along each hole line. Trees are placed between holes,
    not mapped.

  The course screen is laid out round the ball. Top left, the hole card: hole number, par and length, whose turn it
  is, the stroke being played and the score (to par, "thru 4"), and the Mulligan button. Top right, the hole
  overview: the hole from above with the ball ringed in the player's colour, a line from it to where the shot is
  aimed (it swings with the aim and stretches or shrinks with the power) with that distance, and the flag with its
  distance (click or drag on the map to aim). Bottom left, the lie window: a picture of the ball in its lie (teed up,
  sitting down in the rough, settled in sand, ...) on ground tilted with the slope, the left / right and up / down
  slope angles, and the shot penalty (how much shorter the club carries from there than from a clean, level lie);
  on the green, the green read with the flag, heat map and grid buttons instead. Bottom right, the shot bar:
  distance to the pin with the rise or fall to it ("153 yds", "up 2'", and what it plays), the club with its carry
  from here, the aim, and the shot controls with the Ready light and HIT SHOT. A result card after each shot gives
  where it finished and carry / total / offline / distance left; the scorecard opens with `S`, from the menu, and
  after each hole. Once everyone has finished a hole the next one starts by itself after 5 seconds (counting down
  on the NEXT HOLE button; `Space` goes straight away, and the count pauses while a menu is open).

  Each shot is played from where the last one stopped. The app suggests a club for the distance and lie (and switches
  the monitor to it). Lies are tee, fairway, fringe (the collar around greens), rough (a first cut beside the
  fairways, tees and greens), deep rough, bunker and green. Shots from the rough, deep rough, sand and fringe lose
  speed and spin, and sloping lies change the launch (uphill higher, ball above the feet pulls and draws), because a
  monitor reading a ball off a mat can't see the lie. Balls bounce and roll by surface: high-spin wedges check and spin
  back on the greens, the rough grabs, sand stops the ball. Water and out of bounds are stroke-and-distance
  (one-stroke penalty, replay from the previous spot); putts that stop within the gimme distance are tapped in. The
  cup has depth: a putt at the right pace drops in, one that's too firm for how far off-centre it is lips out (or
  skips over if it's dead straight). The flag is pulled once you're on the green (`F` to leave it in).

  **Hot-seat multiplayer, 1-4 players** (set up in Settings): each player has their own ball, name, colour and
  handedness. Everyone tees off in honour order (lowest score on the last hole first; player order on the first),
  then whoever is farthest from the hole plays next. The hole card says "ANN TO PLAY" in her colour with everyone's
  score, the other balls sit on the course with their names over them (and as dots on the minimap), and the result
  card says who's up next. When the turn changes the monitor switches to that player's hand and suggested club. The
  scorecard has a row per player.

  **House rules** (Settings): mulligans per player per round (**Mulligan** on the hole card, `U`, numpad 0 or
  gamepad X takes back the last shot of the round, penalty included, once the ball has stopped and before anyone
  else hits; not a holed one), the gimme distance (0 = hole everything out), and picking up at double par. The
  default is 2 mulligans, 2 ft gimmes and double-par pick-up. Player and rule changes apply from the next round
  ("Save and start a new round" starts one now).

  **The round is saved after every shot.** If the app is closed or crashes, **Resume Game** on the main menu
  carries on with it, with the same players. **Restart Game** in the menu (or **New round** on the scorecard)
  starts again.
- **Settings** (main menu, the in-game menu, numpad `*` or gamepad Back): players (1-4, name and left-handed),
  house rules, auto-connect, text size (Small / Normal / Large / Extra large: smaller for a laptop, bigger for
  reading from the mat or a projector), full screen, graphics quality (Low / Medium / High / Ultra: anti-aliasing,
  shadow detail and reach, bounced light and reflections in the water, how far 3D grass reaches round the camera, and soft focus on grass right in front of a low camera;
  High is the default), and the camera's hole flyover. The course last played and the camera mode are remembered too, so no command-line switches are
  needed. The whole UI also scales up with the window (a full-screen projector shows it bigger), and switches to a
  compact layout when large text leaves less room.
- **Without a keyboard**: a gamepad, a wireless numpad or a presenter clicker can play a whole round (table below).
- **Saved data** lives in `%APPDATA%\Square Range 3D`: `settings.json`, `current-round.json` (the round in
  progress: every player's scorecard and ball, and whose turn it is), `rounds\` (finished rounds, the scorecards of
  everyone who played), `sessions\` (every shot, range and course, with the player's name, one JSON line each, a
  file per app session; a shot taken back with a mulligan is written again marked `"Mulligan": true`), `crash.log`
  (errors and unhandled exceptions) and `logs\` (Godot's own log). Shots are saved from the app's "shot finished"
  event, which carries everything about a shot (launch data, club, player, lie, stance, plays-like distance, result,
  everyone's standing). Development runs that hit shots by themselves (`--demo-shots`, `--hole`, `--hit-at`,
  `--tune`) don't save or resume anything.
- **Physics tuner** (`T` or **Physics Tuner** in the menu) opens in its own window, so it can sit beside the game or on
  another monitor. It has a slider and number box for every physics value, and changes apply from the next shot:
  - **Flight**: drag, lift, spin decay and how drag changes with ball speed; air temperature, altitude and humidity;
    wind speed, direction, gusts and how much weaker it is near the ground.
  - **Bounce & roll**, per surface (fairway, fringe, rough, deep rough, sand, green): bounciness, dig-in, firmness,
    friction, spin grab, and rolling resistance (green speed as a stimp reading on the green); plus wetness everywhere.
  - **Lies**: ball speed, spin and launch change from the fringe, rough, deep rough and sand, and the slope effects.

  The last six shots are shown side by side (carry, total, roll, apex, descent, offline), with the change from the
  shot before and which values changed between them. **Replay last shot** (`F5`) flies the same monitor numbers again
  with the current physics: on a course from the same spot, replacing the shot rather than adding a stroke. The
  previous flight is drawn faintly in the game for comparison. Changed values are marked, with a reset per value,
  section or everything. Presets save as JSON in a `tuning` folder (next to the exported app, or in
  `src/SquareGspro.Godot` when run from Godot); "Use these values at start-up" keeps the current values for the next
  launch. Wind direction is relative to the target on the range and a compass bearing on a course.

**Install it** (the way guests use it): `./tools/export.ps1` exports the Windows build to `dist\Square Range 3D` and
puts a **Square Range 3D** shortcut on the desktop. The preset is `src/SquareGspro.Godot/export_presets.cfg`; the
version comes from `application/config/version` in `project.godot` and is shown in the top bar. It needs the Godot
export templates for the editor's version (Editor > Manage Export Templates, or unzip the `.tpz` into
`%APPDATA%\Godot\export_templates\4.7.2.stable.mono`) and uses `src/SquareGspro.Godot/SquareGspro.Godot.sln`.
Physics presets go in `tuning\` next to the exe (existing ones are kept on re-export); saved data stays in
`%APPDATA%`.

Run it from source: open the folder in the Godot 4.7 .NET editor and press Play, or `godot --path
src/SquareGspro.Godot` (build first with `dotnet build src/SquareGspro.Godot`). Optional switches: `--
--autoconnect` connects even with auto-connect off, `-- --range` skips the main menu for the range, `-- --course`
skips it for the course (the unfinished round, if there is one), `-- --hole=N` starts on hole N. For development
(these runs skip the main menu and start on the range unless `--course` is given), `--demo-shots=N` hits N demo
shots, `--hit-at=2,8.5` presses HIT SHOT at those times (seconds after start), `--screenshot=file.png` saves the
window and quits (after `--screenshot-delay=seconds`, default 4, or once the demo shots finish), `--monitor-drawer`
opens the drawer, `--settings` the settings, `--menu` the in-game menu, `--scorecard` the scorecard just before the
screenshot, and `--reconnecting` shows the reconnecting banner. `--players=Ann,Bob,Cat:L` (`:L` = left-handed), `--text-size=Large` and `--graphics=Ultra` apply to that run only.
`--tuner` opens the physics tuner (the screenshot then also saves it as `file-tuner.png`), `--tuner-tab=Lies` (or
`Flight`, or a surface such as `Green`) picks its tab, and `--tune=key=value` (e.g. `--tune=flight.drag=1.2`) changes
one value after the demo shots and replays the last one.

| Action | Keyboard | Numpad | Gamepad | Clicker |
|---|---|---|---|---|
| HIT SHOT; after the hole, next hole | **Space** | 5 / Enter | A | Next (Page Down) |
| Power 1% / 5% | **↑ / ↓**, Shift + ↑ / ↓ | | | |
| Aim ¼° (course and range) | **← / →** | 4 / 6 | D-pad ← / → | |
| Range: next / previous target flag | **F** / **Shift + F** | | Y | |
| Skip the hole flyover | **Space**, **Esc** | 5 / Enter | A, B | Next (Page Down) |
| Aim 2° | **Shift + ← / →** | 7 / 9 | LB / RB | |
| Landing view on / off (before a shot) | **Z** | 1 | Left stick click | |
| Club longer / shorter | | 8 / 2 | D-pad ↑ / ↓ | Shift + Page Up / Down |
| Mulligan (house rules) | **U** | 0 | X | |
| Scorecard (course) | **S** | . | Y | Back (Page Up) |
| Next hole | **N** | + | | |
| Menu | **M**, **Esc** | | Start | |
| Camera: follow the ball / stay behind it | **C** | − | Right stick click | |
| Settings | | * | Back / Select | |
| Close / cancel | **Esc** | / | B | |
| Random demo shot (after holing out, next hole) | **D** | | | |
| Launch monitor drawer | **L** | | | |
| Physics tuner window | **T** | | | |
| Replay the last shot with the current physics (tuner open) | **F5** | | | |
| Restart hole / flag in or pulled / heat map / grid | **R** / **F** / **H** / **G** | | | |

In the menus and the settings screen, arrows / D-pad move between buttons and fields, Enter / A presses, Esc / B
closes (the settings without saving).

The Godot code is split by topic: `Main.cs` (set-up, the "shot finished" event, saving shots), `Main.Monitor.cs`
(Bluetooth, auto-connect and reconnecting), `Main.Shots.cs` (shots and the range), `Main.Course.cs` (rounds and
turns), `Main.Menus.cs` (main menu and in-game menu), `Main.Controls.cs` (keyboard, numpad, gamepad and clicker),
`Main.Settings.cs`, `Main.DevTools.cs`; and `Hud.cs` (top bar, banner, toasts) with `Hud.Drawer.cs`, `Hud.Range.cs`,
`Hud.Course.cs`, `MainMenu.cs`, `MenuOverlay.cs`, `ShotControls.cs`, `LieView.cs`, `MapOverlay.cs`,
`SettingsPanel.cs` and `PlayersPanel.cs`.
Multiplayer turn order lives in `SquareGspro.Core/Course/Match.cs`.

Carry/total/apex, bounce and roll come from the app's own physics engine (`src/SquareGspro.Physics`, see its README:
aerodynamic flight, bounce and roll over the course ground, and the lie model), not from the monitor, so expect them
to differ somewhat from the Square app and from GSPro. The engine's coefficients and the lie effects are provisional
and not yet validated against measured shots; every one can be adjusted in the physics tuner (the values and their
defaults are listed in `SquareGspro.Core/PhysicsTuning.cs`, and `ActivePhysics` feeds them to every simulation the
app runs). Course rules (lies, roll, holing, penalties) live in
`SquareGspro.Core/Course` and are unit-tested (against hand-made holes in `tests/SquareGspro.Tests/TestCourse.cs`). Trees are scenery only
(balls don't hit them).

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

The data is embedded from `src/SquareGspro.Core/Course/Data/deer-run.osm`. To pick up map edits (for example, after
tracing Deer Run's fairways, ponds and woods on openstreetmap.org), re-download it and rebuild:

```
./tools/fetch-osm-course.ps1
dotnet build src/SquareGspro.Godot
```

Map data © OpenStreetMap contributors, under the [Open Database Licence](https://opendatacommons.org/licenses/odbl/);
the app shows this credit under the scorecard.

Ground elevation comes from the USGS 3D Elevation Program (1 m lidar DEM, public domain), embedded as
`src/SquareGspro.Core/Course/Data/deer-run.dem`; re-download it with `./tools/fetch-elevation.ps1` (same bounding box
as the map). It's resampled to a 2 m grid (`Terrain`), lightly smoothed, and each green is reshaped as a built
surface (its overall tilt capped at 3.5%, a little of its contour kept) so lidar noise doesn't read as bumps.

With elevation, Deer Run plays on real ground:

- The ball flies until it meets the ground, so uphill shots come down short and downhill ones carry further. The HUD
  shows the distance a shot plays ("146 yd to the pin (plays 152)") and the caddie picks clubs by it.
- Landings are judged against the slope (into an upslope checks up, onto a downslope releases), and the ball rolls
  with friction by lie plus gravity along the slope: it runs down hills, stops on gentle slopes, and breaks on greens.
- On the green the default aim is a putt read that allows for the break, and the HUD says how much
  ("23 ft to the pin, aim 2.5 ft left"); demo putts are struck at the read's pace.
- The ground is drawn as one mesh from the same grid, coloured by a coverage texture of the fairways, greens,
  bunkers and water, so the ball sits on what you see.

The test holes stay flat.

The world and UI are built in C# (`RangeWorld`, `CourseWorld`, `ShotPlayer`, `CameraRig`, `Hud`); `Main.tscn` is
just the root. To ship an `.exe`, install Godot's export templates and add a Windows Desktop export preset.

## What gets sent

| Square (device) | GSPro |
|---|---|
| ball speed m/s | `BallData.Speed` mph |
| launch / direction | `VLA` / `HLA` (positive = right) |
| total / back / side spin, spin axis | `TotalSpin`, `BackSpin`, `SideSpin`, `SpinAxis` (**sign flipped**: Square reports negative = curves right) |
| club speed, path, face, attack, dynamic loft | `ClubData.*` (club data is requested after each ball packet; if it doesn't arrive within `ClubMetricsTimeoutMs`, ball data is sent alone) |

Carry distance is not provided by the monitor; GSPro calculates it. Impact location is not forwarded
(scale and sign are unverified).

## Known limits / unverified

- Protocol source: the community reverse-engineering notes in `divotmaker/allsquare` (Omni, firmware
  1.5.8). The original Square (non-Omni) uses different club codes and is not covered.
- Bluetooth connection, device info, battery/uptime/state notifications and disconnects have been run
  against a real Square (hardware 1.5, LM firmware 1.10.29). **Real shots have not been captured yet**;
  shot parsing, club correlation and the GSPro message flow are covered by tests.
- Unverified details: club codes for 5W/7W/4H/3I/LW, left-handed club-face/path signs, the GSPro club
  codes beyond the documented `DR` example (unmapped codes are logged and the monitor's club is left as-is).
- Square publishes no SDK; this is not endorsed by Invant/Square Golf. Check the licence/terms for your use.

## Build from source

Requires the .NET 8 SDK.

```
dotnet test tests/SquareGspro.Tests
dotnet publish src/SquareGspro.Connector -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
```

`SquareGspro.Core` (protocol, GSPro client, shot bridge, ball-flight model) is platform-neutral.
`SquareGspro.Bluetooth` (BLE link, scanner, monitor session) is the only code using the Windows Bluetooth
APIs; the connector and the Godot app both reference it.
