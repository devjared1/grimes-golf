# Roadmap

Personal project for the garage simulator, not for public release. The bar for 1.0: **a friend can walk in, hit
balls or play a round with me, and never see a console, a command-line flag or a stack trace.**

## Housekeeping (before anything else)

- [x] Put the project under git (`.gitignore` for `bin/`, `obj/`, `.godot/`), first commit
- [x] Split `Main.cs` and `Hud.cs` (45-47 KB each) before multiplayer lands in them. While splitting, add a single
      "shot finished" event carrying the full shot context (launch data, club, lie, stance, plays-like distance,
      result, player, match state). Multiplayer, saving data and every AI feature below hook into it.

## 0.8 — Prove it on real hardware

- [ ] Raw BLE packet recorder (timestamped capture file) and a replay mode that feeds a capture back into the app
- [ ] Turn captured sessions into regression tests
- [ ] Verify club codes (5W/7W/4H/3I/LW), left-handed face/path signs, impact location (and whether to forward it to GSPro)
- [ ] End-to-end GSPro session through the connector with real shots
- [ ] Physics calibration: ~10 shots per club compared with the Square app / GSPro carry, total and apex; make the
      tuned set the shipped defaults
- [ ] *Stretch, AI:* **tuning assistant.** Give a model the captured shots, the reference numbers and the current
      tuning JSON; it suggests which values to change, and I check them with `--tune` and replay. Dev-only, an
      assistant to the tuner, not a replacement.

## 0.9 — Garage-ready

- [x] **Hot-seat multiplayer, 1-4 players:** names, handedness, one ball each, farthest from the hole plays next,
      monitor switches club and hand for each player, a scorecard column per player (needs one round state per player)
      (a scorecard *row* per player, in the existing hole-across layout)
- [x] **House rules:** mulligans per round, a gimme distance you can set, max-strokes pickup (double par)
- [x] **Saving data:** sessions, shots and scorecards saved per player (JSON)
- [x] **Settings screen:** players, course, auto-connect, remembered choices, house rules; no command-line flags needed
- [x] **Packaged build:** Godot Windows export preset, version shown in the top bar, one desktop shortcut
- [x] **Easy for guests:** large text readable from the mat or on a projector, a clear "monitor reconnecting"
      message, play with a gamepad, presenter clicker or wireless numpad
      (gamepad / numpad / clicker mappings and the reconnect path still to try with the real devices)
- [x] **Crash safety:** crash log file, a round that resumes where it left off

## 0.9.1 - Additional Garage-ready features

- [x] When Square Range starts, goes to a Main Menu screen with options: New Game, Resume Game (if one was started ), Driving Range, Settings. Also shows launch monitor connection status and version info
      - New Game: opens to a list of courses with course stats and quick description
      - Driving Range: jumps straight into the driving range
      - Settings: shows screen for setting configurations
- [x] Replace the spacebar shot meter
      - Instead have a slider for shot power, 1% - 100%
      - An option for Draw, Straight, Fade shot shape
      - An option for Low, Medium, High spin rate
- [x] Gameplay screen redesign to be more user friendly and less crowded
      - Hole Info: Hole #, Par, Hole Total Distance
      - Current stroke, Current Score
      - Hole overview with current ball location shown, line from ball to flag, distance to flag
      - Mulligan button
      - Menu button
            - Settings, Prev Hole, Next Hole, Add / Remove Player, Restart Game, End Game
      - Small window with picture of ball and lie conditions. (Ball sitting on fairway/rough/deep rough/sand/green/etc..)
            - Lie Angles (Left/Right, Up/Down)
            - Shot Penalty Percentage
      - Distance to the pin display with elevation difference (i.e. To The Pin: 153 yds up 2')
      - Ready / Not Ready indicator
            - If launch monitor connected, shows Ready when prev shot is finished and ball is recognized by LM
            - No launch monitor, shows Ready when prev shot is finished

## 0.9.2

- [x] Driving Range: aim left / right and toggle through flags to aim at
- [x] Add "Small" text size option
- [x] The line in the Hole Overview window needs to show where the current shot is being aimed at and should move when the aim is moved left/right, less power / more power
- [x] Don't show shot results or next shot details until shot is finished
- [x] Hole flyover
- [x] Zoom to landing spot
- [x] Auto go to next hole after 5 seconds when hole is complete
- [x] Shot shape and spin reset after each shot

## 0.9.3

- [-] (moved to 0.9.4) Fix holed ball location (see screenshot)
- [x] Left/right aim in smaller increments
- [x] Zoom to landing refactor: should be a pre-shot option that zooms down to where current aim is not an in-flight thing

## 0.9.4

- [x] Fix holed ball location (see screenshot, "C:\Users\devja\Pictures\Screenshot 2026-11-02 225646.png")
- [x] Redesign in-game UI (attached)
- [x] Loading screen while course loads on New/Resume game
- [x] What is best way to get accurate tree (single tree, tree group (2-5), or dense tree area) location data
      Answer: lidar. OpenStreetMap has almost nothing here (2 wood polygons, no single trees). The same USGS 3DEP
      lidar the elevation comes from has first returns (tops of trees) as well as bare earth: canopy height =
      first-return surface minus ground. Local maxima in the canopy height give single trees with their height and
      crown size; trees whose crowns touch are grouped: 2-5 a group, 6+ dense woods. Done in
      `tools/fetch-canopy.ps1` (USGS EPT point tiles, 2020 survey); Deer Run has 62,555 trees in
      `deer-run.trees.csv.gz`, and the ~9,900 in and around the course are planted from it at their measured
      height and crown size (needed before "Trees that balls actually hit", 1.6)
- [x] Animated text when hole is completed for score on hole
- [x] Wind physics

## 0.9.5

- [x] Put some space between the top black navigation bar and display elements at the top of the screen
- [x] Gameplay Settings Config Screen (New Game > Game Setup)
      - Ability to adjust settings:
            - Game type: Stroke, Scramble, Best Ball, Match Play
            - # of holes to play: Full 18, Front 9, Back 9, Par 3s
            - Environment Settings: Wind, Altitude, Weather
            - Course Settings: Fairway Speed, Green Firmness, Green Stimp
- [x] Smooth the outer edge lines between surface types (see attached screenshot)

## 1.0 — Release checklist

- [ ] Real-shot captures checked in as regression tests; all tests green
- [ ] Physics defaults tuned against my own measured shots
- [ ] A 4-player, 18-hole round at Deer Run with no keyboard use and no crash
- [ ] Sessions and rounds saved and viewable after a restart
- [ ] Monitor disconnects mid-round recover without restarting
- [ ] Installed from one exported build, version shown in the app

1.0 has no AI features on purpose: it has to work fully offline and with no outside services.

## 1.1 — Party games

- [ ] Closest to the pin, long drive (with a fairway grid), target challenges, HORSE
- [x] 9-hole and "par 3s only" rounds (0.9.5)
- [x] Scramble, best ball and match-play scoring (0.9.5)

## 1.2 - Online Play

- [ ] Play with friends from different locations across network. Generate lobby to join. Creator of lobby picks course, sets game settings, and clicks to start game once everyone is connected
- [ ] Only for users with Square launch monitor. Future: support other launch monitors natively (no 3rd party connector access)


## 1.3 — AI caddie and commentary

Hooks into the "shot finished" event through one `Commentator` service; the game doesn't know an AI exists.

- [ ] **Talking caddie:** `Caddie.cs` still picks the club by its rules; the model explains the choice out loud
      ("152 to the pin, plays 158 uphill, ball above your feet so it'll want to draw: smooth 7, aim right edge")
- [ ] **Personalities** players can choose: serious tour caddie, sarcastic buddy, British TV announcer
- [ ] **Shot commentary and trash talk** using the match situation ("Jake's 40 back and now in the bunker")
- [ ] **Round recap:** best shot, worst hole, who choked on 18; shown at the end and on the phone scoreboard
- [ ] Text-to-speech output
- [ ] Speed: start the request at impact, stream the speech, target it landing while the ball is still in the
      air or rolling (about 3-6 s). Fast, small model.

## 1.4 — Practice tools and AI coach

- [ ] Report of each club's average carry and total, with the gaps between clubs
- [ ] Dispersion ellipses on the range, a combine-style test, comparing sessions
- [ ] Per-player stats over time, CSV export
- [ ] **AI coach:** after a range session it reads path, face, spin axis, attack angle and carry/direction spread,
      and explains patterns ("7-iron misses are face-closed-to-path, so the hooks are a face problem").
      Code calculates the stats; the model only explains them. A larger model is fine here (it runs rarely).
- [ ] **Asking questions about stats:** "average 8-iron carry this month?", "who's won more closest-to-pin games,
      me or Mike?" The model turns the question into a query over the saved data.

## 1.5 — Hands-free voice control

- [ ] Local speech-to-text (Whisper), push-to-talk or a wake word
- [ ] A model with tool use maps speech onto a fixed list of actions: `SetClub`, `NudgeAim`, `NextPlayer`,
      `Mulligan`, `ShowScorecard`, `NextHole`, ... It can only call actions from that list.
- [ ] Spoken confirmation of what it did ("9-iron, aiming 3° left")

## 1.6 — Courses and the world

- [ ] Pick and import any OpenStreetMap course from inside the app (no script + rebuild)
- [x] Trace Deer Run's fairways for holes 5-18 on OpenStreetMap
- [ ] Trees that balls actually hit
- [x] Weather presets (calm, breezy, wet); green speed and firmness on the setup screen (0.9.5, Game Setup)
- [ ] Sound: impact, cup, crowd
- [ ] **AI import check:** compare the OpenStreetMap import log with the course's published scorecard and flag
      mismatched pars or yardages, or holes that are probably out of order
- [ ] **AI rules help** ("lateral hazard, where do I drop?") once hazards go beyond stroke-and-distance

## Later / nice-to-have

- Merge the GSPro connector into Range 3D as a "GSPro mode" (one app to launch)
- Shot replays and a highlight camera ("shot of the day")
- Support for the original (non-Omni) Square
- **Live scoreboard on phones:** a local web page friends open to see scores and their shot stats

## Rules for every AI feature

- **Fails quietly:** with no internet or a slow response, show the normal rule-based text. Never hold up a shot.
- **Code decides, the model explains:** the physics, rules, club choice and statistics stay in tested code.
- **Can be switched off:** one setting turns every AI feature off; the app works exactly as at 1.0.
- **Local where it's good enough:** speech-to-text runs on the PC; commentary and coaching use a cloud model.
- **Cost:** a 4-player round is a few hundred short requests (pennies with a small model); the API key lives in a
  local settings file kept out of git.
