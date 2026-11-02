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

- **Top bar** (always shown): Range / Course switch, the course picker, the launch monitor status (ready state and
  battery; click it for the monitor drawer), and the camera mode.
- **Monitor drawer** (`L` or the status chip): every Square it can hear over Bluetooth (with signal strength), Connect
  / Disconnect, the monitor's serial, hardware, firmware (LM / MMI / launcher), uptime, state, ball-in-position
  status and battery, the left-handed setting, and the log.
- **Range**: each shot flies down a 3D range with a camera that follows the ball, trails, landing rings and a
  minimap. The bottom bar shows Carry and Total large, with ball and club speed, smash, launch, direction, apex,
  spin, spin axis, descent, offline, roll and hang time. The right column has the session history and a per-club
  summary (average carry and total, offline spread).
- **Course**: pick a course from the top bar:
  - **Deer Run (Moulton, AL)**, all 18 holes imported from OpenStreetMap: real routing, hole lengths and pars,
    dog-legs, tee, green and bunker shapes, with the course property line as out of bounds. Fairways are mapped for
    holes 1-4; on the others 35-yd fairways are generated along each hole line. Trees are placed between holes,
    not mapped.
  - **Test holes**: a par 4, a par 3 over water and a dog-leg-left par 5 with a creek.

  The course screen shows only what you need to choose the shot: distance to the pin (and what it plays with the
  elevation), the lie and what it does to the strike, the stance slope (or the green read on the green), the club with
  its expected carry from this lie, and where you're aimed. A result card after each shot gives where it finished and
  carry / total / offline / distance left. The hole card shows the stroke and the round score; the scorecard opens with
  `S` and after each hole.

  Each shot is played from where the last one stopped. The app suggests a club for the distance and lie (and switches
  the monitor to it). Lies are tee, fairway, fringe (the collar around greens), rough (a first cut beside the
  fairways, tees and greens), deep rough, bunker and green. Shots from the rough, deep rough, sand and fringe lose
  speed and spin, and sloping lies change the launch (uphill higher, ball above the feet pulls and draws), because a
  monitor reading a ball off a mat can't see the lie. Balls bounce and roll by surface: high-spin wedges check and spin
  back on the greens, the rough grabs, sand stops the ball. Water and out of bounds are stroke-and-distance
  (one-stroke penalty, replay from the previous spot); putts that stop within 2 ft are tapped in. The cup has depth:
  a putt at the right pace drops in, one that's too firm for how far off-centre it is lips out (or skips over if it's
  dead straight). The flag is pulled once you're on the green (`F` to leave it in).
- **HIT SHOT** plays a made-up shot for the selected club (sized to the target on a course), so you can try
  everything without a monitor.
- **Physics tuner** (`T` or **Physics** in the top bar) opens in its own window, so it can sit beside the game or on
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

Run it: open the folder in the Godot 4.7 .NET editor and press Play, or `godot --path src/SquareGspro.Godot`
(build first with `dotnet build src/SquareGspro.Godot`). Options: `-- --autoconnect` connects to the first Square
found, `-- --course` starts in course mode, `-- --hole=N` starts on hole N, `-- --test-holes` picks the test holes.
For development, `--demo-shots=N` hits N demo shots, `--screenshot=file.png` saves the window and quits (after
`--screenshot-delay=seconds`, default 4, or once the demo shots finish), and `--monitor-drawer` opens the drawer.
`--tuner` opens the physics tuner (the screenshot then also saves it as `file-tuner.png`), `--tuner-tab=Lies` (or
`Flight`, or a surface such as `Green`) picks its tab, and `--tune=key=value` (e.g. `--tune=flight.drag=1.2`) changes
one value after the demo shots and replays the last one.

| Key | Action |
|---|---|
| **Space** / **D** | Hit shot (demo); after holing out, next hole |
| **M** | Switch Range / Course |
| **L** | Launch monitor drawer |
| **T** | Physics tuner window |
| **F5** | Replay the last shot with the current physics |
| **C** | Camera: follow the ball / stay behind it |
| **← / →** | Aim 1° (hold Shift for 5°), course mode |
| **S** | Scorecard, course mode |
| **R** / **N** | Restart hole / next hole, course mode |
| **F** | Flag in / pulled while putting (pulled by default), course mode |

Carry/total/apex, bounce and roll come from the app's own physics engine (`src/SquareGspro.Physics`, see its README:
aerodynamic flight, bounce and roll over the course ground, and the lie model), not from the monitor, so expect them
to differ somewhat from the Square app and from GSPro. The engine's coefficients and the lie effects are provisional
and not yet validated against measured shots; every one can be adjusted in the physics tuner (the values and their
defaults are listed in `SquareGspro.Core/PhysicsTuning.cs`, and `ActivePhysics` feeds them to every simulation the
app runs). Course rules (lies, roll, holing, penalties) live in
`SquareGspro.Core/Course` and are unit-tested; the test holes are defined in `TestCourse.cs`. Trees are scenery only
(balls don't hit them).

### Mapped courses (OpenStreetMap)

`OsmCourse.cs` builds a course from OpenStreetMap data: `golf=hole` lines (ref, par, name), `golf=tee`, `green`,
`fairway`, `bunker`, water hazards / `natural=water`, woods, and the `leisure=golf_course` outline (out of bounds).
Holes without a mapped fairway get a generated one; duplicate or missing hole numbers are fixed by routing (the
next tee nearest the last green). What it fixed or generated is printed to the log when the course loads.

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
