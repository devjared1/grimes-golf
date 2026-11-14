# Grimes Golf

A golf simulator for the garage: hit real balls into a net with a **Square Golf Omni** or **Garmin Approach R10**
launch monitor and play real courses in 3D, or practise on a driving range. It has its own ball-flight physics, hot-seat play for up to
four players, team games, and (in development) online rounds with friends at their own simulators.

Built with Godot 4.7 (.NET) and C#. Windows only, because it talks to the monitor over Windows Bluetooth LE.

> This is an independent project, not made or endorsed by Invant / Square Golf or by Garmin. Neither publishes an
> SDK; the monitor protocols come from community reverse-engineering work (see [Credits](#credits-and-data-licences)).

## Contents

- [Features](#features)
- [System requirements](#system-requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Online play](#online-play)
- [Building from source](#building-from-source)
- [Project layout](#project-layout)
- [Documentation](#documentation)
- [Known limitations](#known-limitations)
- [Roadmap](#roadmap)
- [Credits and data licences](#credits-and-data-licences)
- [Licence](#licence)

## Features

- **Square Golf Omni over Bluetooth LE**: finds and connects to the monitor by itself, follows the player's club and
  hand, and reconnects automatically if the link drops.
- **Garmin Approach R10 over Bluetooth LE** (new, not yet tested on a real R10): connects straight to the R10, with no
  phone app, connector or subscription, and its shots play exactly like the Square's. See the
  [user guide](docs/user-guide.md#garmin-approach-r10) for pairing it.
- **No monitor needed to try it**: a power slider with draw / fade and spin choices hits shots from the screen.
- **Real courses**: Deer Run (Moulton, AL), all 18 holes from OpenStreetMap, on 1 m lidar elevation, with its
  ~9,900 real trees at their measured height and spread.
- **Own physics engine**: aerodynamic flight, wind, altitude and weather, bounce and roll by surface, slopes,
  sloping and rough lies, putts that break and lip out. Every value can be adjusted in a physics tuner.
- **Games for 1-4 players**: stroke play, scramble, best ball and match play; full 18, front or back 9, or par 3s;
  house rules for mulligans, gimmes and double-par pick-ups.
- **Made for guests**: large text options, full screen for a TV or projector, and a whole round can be played with a
  gamepad, wireless numpad or presenter clicker.
- **Nothing lost**: the round is saved after every shot and resumes after a crash; every shot and scorecard is kept
  per player.
- **Online play** (in development): a relay server with 5-character lobby codes lets friends at different houses
  play one round together.

The full tour of the screens, controls and options is in the [user guide](docs/user-guide.md).

## System requirements

The minimums below come from what the app is built against. Nothing has been benchmarked yet, so performance
figures are guidance, not tested limits.

### To play (every PC)

| | Required | Notes |
|---|---|---|
| Operating system | Windows 10 version 2004 (build 19041) or later, 64-bit; Windows 11 | The Bluetooth code uses the Windows 10 19041 APIs. |
| Graphics | A GPU with Vulkan 1.0 support | Godot's Forward+ renderer. On integrated graphics, use the Low or Medium graphics setting. |
| Display | Any size | Laid out for 1600×900 and scales with the window; full screen works on TVs and projectors. |
| Bluetooth | Bluetooth 4.0 (LE) or later, built in or a USB adapter | Only needed with a launch monitor. Don't pair a Square in Windows settings; a Garmin R10 is paired once, the first time it connects. |
| Launch monitor | Square Golf Omni or Garmin Approach R10 | Optional offline (shots can be hit from the screen). The original Square (non-Omni) isn't supported. R10 support is new and untested on hardware. |
| .NET | Nothing to install | The exported build includes the .NET runtime. |
| Input | Mouse and keyboard, or a gamepad, numpad or presenter clicker | |

### To play online

Online play is still in development and only appears in debug builds (see [Online play](#online-play)). When it's
on, both roles need everything above, plus:

| | Host (creates the lobby) | Guest (joins with a code) |
|---|---|---|
| Launch monitor | Square Golf Omni or Garmin R10, connected | Square Golf Omni or Garmin R10, connected |
| App version | The build every guest uses (versions must match exactly) | The host's build |
| Relay | Its address. A relay has to be deployed once, by anyone in the group ([how](docs/deploying-the-relay.md)) | The address and the 5-character lobby code, from the host |
| Network | Outbound internet to the relay (port 443 for a `wss://` relay). No port forwarding. | The same |
| Connection quality | A steady connection for the whole round: the host's PC plays every shot, so if it drops the guests wait (after 2 minutes they're offered the main menu, scores kept) | Can drop and rejoin: if their turn comes while they're gone, their players are skipped after 60 s, and play again from the next hole once back |
| PC kept on | Until the round ends. A paused round can be carried on within 12 hours. | Only while playing |
| Players at the PC | 1-3 | 1-3 (at most 4 across the whole lobby) |
| Chooses | Course, game type, holes, conditions, house rules; its physics tuning is used by everyone | Nothing: plays with the host's settings and tuning |

Each shot sends about 16 KB (an estimate), so bandwidth isn't a concern.

### To run the relay server

A small always-on machine with Docker, or a [Fly.io](https://fly.io) account; 256 MB of memory is plenty for a few
friends. See [Deploying the relay](docs/deploying-the-relay.md).

### To build from source

- Windows 10/11 to run and export the game (the code compiles anywhere, but Bluetooth and the export need Windows)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the game and its libraries)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the relay server and the online tests)
- [Godot 4.7.2, .NET edition](https://godotengine.org/download), plus its export templates to make a release build
- PowerShell for the scripts in `tools/`; Python 3 with numpy and laspy only to re-make the tree data

## Installation

Download `GrimesGolf-Setup-<version>.exe` from the [releases page](https://github.com/devjared1/grimes-golf/releases)
and run it. It needs no administrator password. To update, run the new version's installer over the old one; the
main menu says when there's a new one. The installer isn't code-signed, so Windows may warn about it: choose
**More info**, then **Run anyway**.

To build one from source instead:

```powershell
./tools/export.ps1
```

This exports the Windows build to `dist\Grimes Golf` and puts a **Grimes Golf** shortcut on the desktop. Pass
`-Godot "C:\path\to\Godot_v4.7.2-stable_mono_win64.exe"` if Godot isn't on the `PATH`, and `-NoShortcut` to skip the
shortcut. To make the installer as well, see [Publishing a release](docs/development.md#publishing-a-release).

Settings, saved rounds, shots and downloaded courses live in `%APPDATA%\Square Range 3D` (the app's earlier name,
kept so existing data stays where it is). Updating or uninstalling doesn't touch them.

## Quick start

1. Turn on the Square Golf Omni and **close the Square phone or tablet app**: only one app can hold the connection.
   Don't pair the monitor in Windows Bluetooth settings; it doesn't pair. With a **Garmin R10**, close the Garmin Golf
   app and turn the phone's Bluetooth off instead; the first time, put the R10 in pairing mode (see the
   [user guide](docs/user-guide.md#garmin-approach-r10)).
2. Start Grimes Golf. It opens on the main menu and connects to the monitor by itself (the status card at the
   bottom of the menu shows when it's ready).
3. Add the players in **Settings** (1-4, with names and left- or right-handed).
4. Pick **Driving Range** to warm up, or **New Game** to choose a course and game, then **Start Round**.
5. Hit the ball. With no monitor, set the power and press **HIT SHOT** (`Space`).

The [user guide](docs/user-guide.md) covers the course screen, game types, house rules, the controls for gamepad,
numpad and clicker, and the physics tuner.

## Online play

Friends at different houses play one round through a small relay server. The host's PC plays every shot; the others
are sent each outcome with the whole match after it and animate that, so scores can't drift apart.

**Status:** the relay, the networking and the game integration are built and tested in code, but not yet played
between two houses. Until then **Play Online only appears in debug builds**, and the release build stays fully
offline. The design and progress are in the [online play plan](docs/online-play-plan.md).

To try it now, deploy a relay ([guide](docs/deploying-the-relay.md)) or run one locally, then run two copies of the
game on one PC as described in the [development guide](docs/development.md#online-play-on-one-pc).

## Building from source

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

To run the game, open `src/GrimesGolf.Godot` in the Godot 4.7 .NET editor and press Play, or:

```bash
godot --path src/GrimesGolf.Godot
```

Command-line switches for development, the code layout of the game, and how to refresh the course data are in the
[development guide](docs/development.md).

## Project layout

| Path | What it is |
|---|---|
| `src/GrimesGolf.Godot` | The game (Godot 4.7, C#): worlds, camera, HUD, menus, input, online lobby |
| `src/GrimesGolf.Core` | Platform-neutral game logic: courses, rounds, scoring, house rules, the Square and Garmin R10 protocols, the device-neutral monitor interface, saved data |
| `src/GrimesGolf.Physics` | The golf physics engine: impact, flight, wind, bounce, roll, putting ([README](src/GrimesGolf.Physics/README.md)) |
| `src/GrimesGolf.Bluetooth` | The Windows Bluetooth LE scanner and links to the Square and the R10 (the only Windows-specific code) |
| `src/GrimesGolf.Online` | Online play: the relay connection, the messages, the host and guest sessions |
| `src/GrimesGolf.Relay` | The relay server (.NET 10, standalone), with a Dockerfile and `fly.toml` |
| `tests/` | xUnit tests for each of the above |
| `tools/` | The release build, and the scripts that find, make and publish courses |
| `installer/` | The Windows installer script |
| `docs/` | User guide, development guide, relay deployment, design documents |

## Documentation

- [User guide](docs/user-guide.md): screens, game types, house rules, controls, the physics tuner, saved data
- [Development guide](docs/development.md): running from source, command-line switches, code layout, course data, testing online play
- [Garmin R10 protocol notes](docs/garmin-r10-protocol.md): how the app talks to the R10, its sources, open questions and the first hardware test
- [Deploying the relay](docs/deploying-the-relay.md): Fly.io or any Docker host
- [Online play plan](docs/online-play-plan.md): design and status of roadmap 1.2
- [Physics engine specification](docs/physics-engine-spec.md) and the [physics README](src/GrimesGolf.Physics/README.md)
- [Roadmap](ROADMAP.md)

## Known limitations

- **Real shots haven't been captured yet.** Bluetooth connection, device info, battery, state and disconnects have
  been run against a real Square (hardware 1.5, LM firmware 1.10.29); shot parsing is covered by tests.
- **Unverified monitor details:** club codes for 5W / 7W / 4H / 3I / LW, the left-handed club face and path signs,
  and impact location.
- **The Garmin R10 hasn't been tried on a real one yet.** Its support is built from community reverse-engineering
  and tested against a simulated R10; pairing from the app, the handshake on current firmware and the left-handed
  signs need a real device (the [protocol notes](docs/garmin-r10-protocol.md#open-questions) list them). The app
  doesn't send the R10 its distance to the tee yet.
- **Physics isn't calibrated.** Carry, total and roll come from the app's own engine, not the monitor, and haven't
  been validated against measured shots, so expect them to differ from the Square app. Every value can be adjusted
  in the physics tuner.
- **Trees are scenery**: balls don't hit them yet.
- **Online play** hasn't been played between two houses yet (see above).

## Roadmap

See [ROADMAP.md](ROADMAP.md). Next up: proving it on real hardware (0.8), the 1.0 release checklist, party games
(1.1) and online play (1.2).

## Credits and data licences

- Map data © [OpenStreetMap](https://www.openstreetmap.org/copyright) contributors, under the
  [Open Database Licence](https://opendatacommons.org/licenses/odbl/). The app shows this credit under the scorecard.
- Elevation and tree data: [USGS 3D Elevation Program](https://www.usgs.gov/3d-elevation-program) lidar, public
  domain.
- Sky and ground textures: Poly Haven and ambientCG, CC0 (see
  [assets/CREDITS.md](src/GrimesGolf.Godot/assets/CREDITS.md)).
- Square Golf protocol: the community reverse-engineering notes in `divotmaker/allsquare`.
- Garmin Approach R10 protocol: ported from [gsp-r10-adapter](https://github.com/mholow/gsp-r10-adapter) by mholow
  (MIT, notice in [NOTICE](NOTICE)), cross-checked against [10over](https://github.com/divotmaker/10over)
  (Apache-2.0 / MIT), whose protocol bytes are also used as test fixtures.
- Built with [Godot Engine](https://godotengine.org) (MIT).

## Licence

The code is released under the [MIT Licence](LICENSE). The map, elevation and texture data listed above keep their
own licences: in particular, the OpenStreetMap data is under the ODbL, which asks for the credit and for changes to
the data itself to be shared alike.
