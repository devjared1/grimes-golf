# ROADMAP 1.2 Online Play: plan

## Status (2026-11-04)

| # | State |
|---|---|
| M0 | Done: the tree was already committed and clean; work is on its own branch. Relay host not chosen yet (a `fly.toml` is ready). |
| M1 | Done and tested: `MatchSnapshot` with `Match.Capture/Restore`, `Match.SetAway`, `HoleRound.Load/PickUpHole/SkipScore`, `CourseFingerprint`, `RangeShot.Source`, `online-round.json` and `GolfData.FileRound`, `TunerWindow.FreezeStartup`. |
| M2 | Built and tested in-process and over real sockets on one machine. **Not deployed.** |
| M3 | Done and tested: 18 holes for all four game types with 2 and 3 PCs stay in step; drops, rejoins, duplicate and off-turn shots, late acks, mulligan races, host leaving, pause and resume. |
| M4-M6 | Written and compiling, **not yet run in Godot**: `Main.Online.cs`, `OnlinePanel.cs`, the guards in `Main.*`, the debug-only switches. Play Online shows only in debug builds. |
| M7 | Not started: needs a friend's Square, an exported build and a deployed relay. |

What's simpler than planned, for now:
- The lobby has no Game Setup page: the host's round uses the game, holes and conditions last chosen under New Game, and the house rules from Settings. The course can be picked in the lobby.
- A dead monitor skips only the player up (a PC that has gone skips all its players).
- The scorecard doesn't mark away players; their picked-up holes show as scores.


## Context

Roadmap 1.2 asks for friends at different locations to play together: the creator generates a lobby, picks the course and settings, and starts once everyone is connected. It is limited to Square launch monitor users, with other monitors possible later. The app has no networking today and must stay fully offline unless someone opens Play Online.

**Decided with you:** small relay server with lobby codes; shared turns where everyone watches each shot; 1-4 players per PC, 4 total.

## Approach

**The host's PC is the only one that simulates. Everyone else animates its result.**

- Every PC keeps its own `Match`. After each shot, the host sends the outcome plus a full `MatchSnapshot`.
- Non-hosts restore the snapshot at impact, then run the existing animation and `FinishShot` code. The existing `_outcome != null` guards keep the result hidden until the ball stops.
- Why not re-simulate on each PC: tuning, wind seed and house rules are all local today, and `Math.*` isn't guaranteed bit-identical across machines. Cup capture is a yes/no decision, so a tiny difference could split the scores for good.
- Use a narrow seam, not a RoundController. Round flow in `Main.Course.cs` stays put, and each change is a guard on `_online`, which is null offline. Core change in `PlayCourseShot` (`Main.Course.cs:356`):

```csharp
var o = auth?.Outcome is { } remote
    ? ApplyRemote(match, auth, remote)   // match.Restore(auth.Snapshot); return remote;
    : match.Play(s, Putter, _aimHeading, _hud.ClubCode);
```

The one snapshot type also covers rejoin, resync, mulligans and host resume, so `HoleRound` needs no separate "apply outcome" path.

## Structure

| Location | Contents |
|---|---|
| `SquareGspro.Core` (no networking) | `Course/MatchSnapshot.cs`, `Match.Capture/Restore/SetAway`, `HoleRound.PickUpHole`, `CourseFingerprint`, `RangeShot.Source`, separate online round save path |
| New `src/SquareGspro.Online` (net8.0, no Godot) | `Protocol/`, `Transport/RelayClient.cs`, `Session/HostSession.cs`, `Session/ClientSession.cs`, `IOnlineGame` |
| New `src/SquareGspro.Relay` (net10.0, no project refs) | About 300 lines of minimal ASP.NET, Dockerfile |
| `SquareGspro.Godot` | New `Main.Online.cs`, `MainMenu.Online.cs`; guards in existing `Main.*` |
| New `tests/SquareGspro.Online.Tests` | Sessions over an in-memory transport, relay via TestServer |

## Key design

**Transport.** `ClientWebSocket` over `wss`, testable under xUnit with no native files in the export. One read loop and one send loop on a bounded channel; inbound messages reach the main thread through `Post`, and wait while `_loading` (which would otherwise drop shots, `Main.Shots.cs:191`). Heartbeat runs off the main thread because course building blocks it. Reconnect uses backoff with a GUID token kept in `AppSettings`.

**Relay.** Keeps lobbies in memory and never parses game payloads. 5-character codes. A lobby lasts while its host is connected, up to 12 h, plus 10 min after the host drops. Friends-only limits: 256 KB per frame, 4 PCs per lobby, 100 lobbies, per-IP join rate. Logs counts only. A relay restart is survivable: everyone reconnects and the host re-creates the same code. Suggested host: one small Fly.io machine, though any Docker host works.

**Shot flow.**
1. The monitor fires and `ShowShot` passes the shot to the session. Off-turn shots don't count and show a toast. Monitor shots have no turn check today (`Main.Monitor.cs:161-165`).
2. The on-turn PC sends `ShotRequest{Id, Seq, Player, Shot, Club, Heading, Source}` and resends with the same `Id` after a network blip, so a real ball isn't lost.
3. The host validates seat, turn, sequence and finite numbers, then runs the unchanged `PlayCourseShot`.
4. The host broadcasts `ShotResult{..., Outcome, Snapshot}`, and every PC starts animating on receipt.
5. Each PC reveals the result at its own `FinishShot`, then sends `ShotShown`.
6. The host sends `TurnOpen{Seq, Snapshot}` once all connected PCs have reported, or at flight time + 6 s.

Paths are sent as float32 bytes, about 16 KB for a 15 s shot (estimate). `ShotFinished` isn't usable on the wire: it fires at impact and carries the whole `Hole`.

**Aim.** The on-turn PC owns aim, club and power, and others see them live, at most 10 updates a second. Camera, flag, heat map and scorecard stay local.

**Rules.**
- Gimme, pickup and scramble pick are decided on the host and arrive in the snapshot. Picks stay automatic, as in hot-seat.
- Mulligan: only the PC that owns `LastToPlay` can request it, and the window closes when the next shot is accepted.
- Next hole: the host's timer runs and doesn't pause for menus.

**Physics consistency.** `RoundConfig` carries course fingerprint, `GameSetup`, `HouseRules`, tuning JSON, seats and start time. Non-hosts apply the host's tuning for aim preview and putt reads, then restore their own on leaving. Wind comes from the snapshot, replacing the `DateTime.Now` seed that would differ per PC. The tuner and replay are off for everyone online.

**Lobby and versions.** Protocol and app version must match exactly, with a clear message when they don't. The course fingerprint is checked after load. Start is enabled only when every PC is connected with a live monitor and `GameSetup.Problem` is null. Names are validated and de-duplicated, and the host can reorder seats because seat order sets teams (`Match.cs:85`).

**Square-only gate.** An honest client-side check: `_monitorLive` reported in `Hello`. Don't gate on the serial, which can be null. Add `RangeShot.Source` (`Screen, Monitor, Demo, Dev`) and `OnMonitorShot(RangeShot)` as the seam for future monitors, but no `ILaunchMonitor` until something needs it.

**In-game gating online.**
- HIT SHOT and Demo no longer hit; the button reads "Hit on your monitor".
- Hidden: replay, tuner, previous/next/restart hole, add/remove players, restart game.
- End Game becomes Leave (client) or Pause/End (host).
- Add a turn line, a "Waiting for Bob" ready state, and a separate online banner.

**Disconnects.**
| Event | Behaviour |
|---|---|
| Player drops, on turn | 60 s countdown; host can skip sooner; then marked away and the turn moves on |
| Player drops, not on turn | Play continues; the countdown starts if their turn arrives |
| Monitor drops | Existing auto-reconnect; blocks only when on turn, 120 s, then the same skip |
| Host drops | Clients hold with a banner and can always leave; after 120 s, offered Main Menu with their scores safe |
| Host closes | Pause for later, or End for everyone; clients are told either way |
| Rejoin | Same code and token; host sends a full `Sync` |
| Host resume | `online-round.json`, and clients get a Rejoin button for 12 h |

Mid-round joining by new PCs isn't in v1, because `Regroup` remaps player indexes.

## Traps to handle

Checked in the code:
- **Tuning overwrite:** applying host tuning triggers `_saveStartupIn` (`TunerWindow.cs:531`), which would overwrite a friend's `startup.json`. Add a freeze flag.
- **Offline save deleted:** completing a round deletes `current-round.json` (`GolfData.cs:152`), so online rounds need their own path.
- **Scramble deadlock:** `Pick` needs every member to have hit (`Match.cs:141`), so absent players need a `SetAway` check.

Reported by the design pass, to confirm during implementation:
- `Match.Resume` force-picks a part-way scramble, so never use it online.
- `Hud.SetPlayerClub` only raises `ClubChanged` on a change, and mirroring a remote club would write to the local monitor. Gate the write and force a sync when the turn becomes local.
- The banner slot is shared with monitor messages (`Main.Monitor.cs:145,179`).
- `Name[..1]` throws on an empty name (`Main.Course.cs:382`).
- `LastPick` is a ValueTuple, which System.Text.Json skips.

Support dates: all client projects target net8.0, whose support I recall ending on 2026-11-10, next week. That's why the internet-facing relay targets .NET 10. Clients stay on net8.0 for this work; upgrading depends on what Godot 4.7.2 supports, so check that separately.

## Milestones

No online UI is visible until M7. Sizes are relative.

| # | Scope | Main files | Size |
|---|---|---|---|
| M0 | Commit the 62 uncommitted entries in logical commits, confirm tests pass, branch `online-play`, choose a relay host | None new | 1 |
| M1 | Snapshot, away, pickup, fingerprint, shot source, save path, tuner freeze | `Match.cs`, `HoleRound.cs`, `MatchSnapshot.cs`, `RangeShot.cs`, `GolfData.cs`, `TunerWindow.cs` | 2 |
| M2 | Relay and transport, deployed | `SquareGspro.Relay/*`, `Transport/*`, `SquareGsproConnector.sln` | 3 |
| M3 | Protocol and sessions, no Godot | `Protocol/*`, `Session/*`, `IOnlineGame` | 5 |
| M4 | Lobby through to everyone on the first tee | `MainMenu.Online.cs`, `MainMenu.cs`, `MainMenu.Setup.cs`, `Main.Online.cs`, `Main.Menus.cs`, `AppSettings.cs` | 3 |
| M5 | Real play on a fake monitor | `Main.Course.cs`, `Main.Shots.cs`, `Main.Monitor.cs`, `Main.Controls.cs`, `Main.cs`, `Main.DevTools.cs`, `Hud*` | 5 |
| M6 | Resilience UI and host resume | `Main.Online.cs`, `GolfData.cs`, overlays | 4 |
| M7 | Internet test with a friend's Square, enable the menu, docs | `MainMenu.cs`, `tools/export.ps1`, `README.md`, `ROADMAP.md` | 2 |

**Order against the unfinished 1.0 list.** M1-M3 can start now because they don't change app behaviour.
- Before M5: complete the 4-player, 18-hole no-crash round, since online inherits every hot-seat bug.
- Before M7: monitor reconnect, one exported build with its version shown, and the 0.8 club-code and left-handed checks if a friend is left-handed.
- Doesn't block: physics calibration (host tuning is shared), the 0.8 recorder (`sessions/*.jsonl` can drive the fake monitor), and 1.1 party games (not offered online in v1).

## Defaults you may want to change

1. A skipped player scores `max(strokes, 2 × par)` on that hole, even with the double-par house rule off.
2. Waits: 60 s for a dropped player, 120 s for a dropped monitor, 120 s for a missing host.
3. No on-screen slider fallback if a monitor dies mid-round, because of the Square-only rule; that player gets skipped.

## Verification

- **M1:** a mid-scramble snapshot survives a JSON round trip; restoring on a second `Match` then advancing gives the same results; existing hot-seat tests pass unchanged.
- **M2:** tests for create, join, forward, rejoin, hostless expiry, limits and relay restart.
- **M3:** scripted 18 holes for all four game types with 2-3 sessions, with equal final states; covers drops, late acks, duplicate and out-of-turn shots, and mulligan races.
- **M4-M6:** dev-only flags, active only under `OS.IsDebugBuild()`, so they stay out of the release export: `--relay`, `--data`, `--no-bluetooth`, `--fake-monitor[=session.jsonl]`, `--auto-play`.
  - Run two instances on one PC against a local relay, for all game types.
  - Soak-test 18 holes with `--auto-play`.
  - Kill each process and the relay at every phase, including mid-flight and during loading.
  - Confirm the offline save and `startup.json` are untouched afterwards.
  - Run a scripted offline hot-seat round before and after M5.
- **M7:** a full 18 holes between two houses with two real Square monitors, using the exported build, with no keyboard, console or stack trace.

## Known limits

- This plan hasn't been compiled or run. Fly.io behaviour and the .NET support date come from memory and need checking at M0/M2.
- Testing online end to end needs a friend's Square; until M7 the fake monitor stands in.
- There's no auto-update, so friends must install the same build, and an unsigned exe will trigger SmartScreen.
- The relay is this project's first always-on public service, with a small hosting bill.
- Update `ROADMAP.md:117`'s offline note at M7: 1.0 stays offline, and only Play Online uses the internet.
