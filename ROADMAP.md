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
- [ ] Verify club codes (5W/7W/4H/3I/LW), left-handed face/path signs, impact location
- [ ] Physics calibration: ~10 shots per club compared with the Square app's carry, total and apex; make the
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

- [x] When the app starts, goes to a Main Menu screen with options: New Game, Resume Game (if one was started ), Driving Range, Settings. Also shows launch monitor connection status and version info
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

## 0.9.6

- [x] Mark any roadmap items complete if able
- [x] The Square LM comes with an aligment stick (white). In the Square software and GSPro software, you cannot see your alignment or realign mid round. You have to stop and go to a different screen and come back. Would be nice if this software showed the current alignment (if an alignment has been done during the current session / gameplay) and allowed you to realign mid game.
      (built from Square's protocol; still to try with the real stick)
- [x] **Ball position display:** when the monitor reads the ball, show it on screen (where it sits in the hitting
      zone, and how much of the ball the cameras can see) so a guest can tell why it isn't "Ready". First find out
      what the Square reports: the sensor packet decoded today only says detected / ready (`0x11 0x01`), so a
      recorded session (0.8) is needed to look for position data; if there is none, this needs our own camera
      (the sensor packet does carry the position. The monitor doesn't report its ready limits or how much of the
      ball it sees, so the zone drawn is where balls have gone ready so far; still to check on the real monitor)
- [x] Trees that balls actually hit
- [x] Sound: impact, cup, crowd (made in code; still to judge by ear)

## 0.9.7

- [x] **Home player:** a saved player, loaded every time the app starts; if there isn't one, the app asks for it
      - Name, right / left hand, launch monitor type (detected from the monitor when it connects)
      - Optional club distances: enter the carries you know, the rest are estimated from them. They set the caddie's
        suggestions, the aim line and how far on-screen swings go; launch monitor shots aren't changed
- [x] **Saved guests:** kept after a restart (name and hand only), and picked for a round from the player list
- [x] **Installer:** one per-user installer (`tools/release.ps1`), published as a GitHub release; updates keep saved data
- [x] **New version notice:** a button on the main menu when GitHub has a newer version (can be turned off in Settings)
- [x] **Courses are folders:** a course is a folder of files, not part of the program; Deer Run ships as the first
- [x] **Get More Courses:** download, update and remove courses from New Game. Each download is checked against the
      course list's hash and proven to load before it replaces anything
- [x] **Stricter course check online:** the fingerprint now covers every shape, height and tree, so a moved bunker is noticed
- [x] **Making a course takes one command:** `tools/find-course.ps1`, `make-course.ps1` and `publish-courses.ps1`
- [x] **Payne's Valley** is the first downloadable course: pars match the scorecard, and it's 7,312 yd against 7,370 from the back tees
- [x] **Faster loading:** drawing streams and cart paths took most of the loading time; Deer Run's round now starts in seconds
- [x] **Game Setup fits the screen:** Start Round is always visible, and the choices scroll at large text sizes

## 0.9.8

- [x] **Grimes Golf branding:** a splash screen in place of Godot's, the new logo throughout, and icons for the app,
      installer and desktop shortcut
- [x] **Always opens full screen** in released builds (Settings can make it a window until it's closed)
- [x] **Game Setup on New Game:** under the player list, with no second page
- [x] **Ball position panel hidden for now:** `ShowBallZone` in `Hud.Course.cs` brings it back; the top bar still shows
      when the ball is ready
- [x] **Overview map labels:** the aim's distance no longer covers the flag's
- [x] **New Game at large text:** the course list scrolls, so the launch monitor card stays on screen
- [x] **Licence notices** for Godot, its libraries and .NET are installed with the app
- [x] **Tests in CI:** GitHub Actions runs the tests and builds the game on every pull request and push to `main`
- [x] **Publish course shapes under another name:** `make-course.ps1 -PublicName`; publishing only uploads what changed

## 1.0 — Release checklist

- [ ] Real-shot captures checked in as regression tests; all tests green
- [ ] Physics defaults tuned against my own measured shots
- [ ] A 4-player, 18-hole round at Deer Run with no keyboard use and no crash
- [x] Sessions and rounds saved and viewable after a restart (**History** on the main menu: finished rounds with
      their scorecards, practice by day with averages per club)
- [ ] Monitor disconnects mid-round recover without restarting
- [x] Installed from one exported build, version shown in the app (installer 0.9.7, version in the top bar and menu)

1.0 has no AI features on purpose: it has to work fully offline and with no outside services.

## 1.1 — Party games

- [ ] Closest to the pin, long drive (with a fairway grid), target challenges, HORSE
- [x] 9-hole and "par 3s only" rounds (0.9.5)
- [x] Scramble, best ball and match-play scoring (0.9.5)

## 1.2 - Online Play

- [x] Play with friends from different locations across network. Generate lobby to join. Creator of lobby picks course, sets game settings, and clicks to start game once everyone is connected
- [x] Only for users with Square launch monitor
- [ ] Future: support other launch monitors natively (no 3rd party connector access)
  - [x] A device-neutral monitor layer (`ILaunchMonitorSession`); the Square runs through it unchanged
  - [x] **Garmin Approach R10** straight over Bluetooth (no phone app, connector or subscription), with a Settings
        choice of monitor, tested against a simulated R10 ([protocol notes](docs/garmin-r10-protocol.md))
  - [ ] Verify the R10 on real hardware: pairing from the app, shots against the Garmin Golf app, left-handed signs,
        reconnecting ([checklist](docs/garmin-r10-protocol.md#first-hardware-test))
  - [ ] Send the R10 its distance to the tee once its units are known from a capture
- [ ] *After v1:* **live text and video chat** in online games. Text chat first: small messages through the relay
      it already has, shown over the game and readable from the mat. Video is a much bigger step: it goes peer to
      peer (WebRTC), not through the relay, needs a webcam and a TURN server for PCs behind strict routers, and
      costs bandwidth the shots don't. Voice alone (no video) may give most of the fun for less


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
- [ ] **Other cameras for swing replays:** a webcam or phone (down the line and face on) recording each swing,
      cut to the moment the monitor reports the shot, replayed in slow motion beside the numbers. The Square
      itself doesn't hand over its images (its Bluetooth link only sends measurements), so the video comes from
      cameras we add. That video can then feed the AI coach (pose estimation for posture, tempo and positions).
      Measured on the EasyCam 501: 30 fps is its top rate, and it ran 15 fps in a dim room (MJPEG; 60 fps is
      refused, raw YUY2 is far slower). So half speed at best, enough for tempo and positions but not ball impact
- [ ] **Asking questions about stats:** "average 8-iron carry this month?", "who's won more closest-to-pin games,
      me or Mike?" The model turns the question into a query over the saved data.

## 1.5 — Hands-free voice control

- [ ] Local speech-to-text (Whisper), push-to-talk or a wake word
- [ ] A model with tool use maps speech onto a fixed list of actions: `SetClub`, `NudgeAim`, `NextPlayer`,
      `Mulligan`, `ShowScorecard`, `NextHole`, ... It can only call actions from that list.
- [ ] Spoken confirmation of what it did ("9-iron, aiming 3° left")

## 1.6 — Courses and the world

- [ ] Pick and import any OpenStreetMap course from inside the app, rather than publishing it first with the scripts.
      The map and elevation could move into the app; the trees need Python and hundreds of MB of lidar
- [x] **One course-data command:** `tools/make-course.ps1` makes one folder per course from one bounding box (0.9.7)
- [x] **Where generated courses live:** each is a zip on the `courses` GitHub release, downloaded once into the data
      folder (0.9.7). Zips are about 2-7 MB (Deer Run 7.2 MB), so about 200-700 MB for 100
- [ ] Deer Run's box is far bigger than the course: a tighter one would make its download smaller
- [ ] Online: offer the host's course to a friend who doesn't have it, rather than telling them to get it first
- [x] Trace Deer Run's fairways for holes 5-18 on OpenStreetMap
- [x] Weather presets (calm, breezy, wet); green speed and firmness on the setup screen (0.9.5, Game Setup)
- [ ] **AI import check:** compare the OpenStreetMap import log with the course's published scorecard and flag
      mismatched pars or yardages, or holes that are probably out of order
- [ ] **AI rules help** ("lateral hazard, where do I drop?") once hazards go beyond stroke-and-distance

## 1.7 — DIY overhead launch monitor (research)

A budget alternative to the Square, mounted above the hitting area so left- and right-handers need nothing moved.
It would arrive as one more shot source (`RangeShot.Source`), so the game doesn't change. Research first, build
only if the numbers work out.

### What the cameras have to do

The numbers that decide the hardware (worked out here, not measured):

- **Ball speed:** a driver at 70 m/s (157 mph) moves 14 cm between frames at 500 fps, 28 cm at 250 fps and over a
  metre at 60 fps. Seeing the ball in 4-6 frames over a ~1 m window needs roughly 400-500 fps, or a trick that
  puts several images of the ball in one frame (below).
- **Blur:** to keep the ball sharp to about 1 mm at 70 m/s, each exposure (or strobe flash) must be about 15 µs or
  shorter. That needs a global-shutter sensor (a rolling shutter smears and leans a fast ball) and bright infrared
  light flashed only for those microseconds; room lighting is far too dim.
- **Launch angle from above:** one camera looking down sees direction and speed well but height badly. Two cameras
  a known distance apart (stereo), calibrated, give the ball's position in 3D and so launch angle properly.
- **Spin:** a driver at 2,500 rpm turns 30° between frames at 500 fps; a wedge at 10,000 rpm turns 120°, too far
  to track markings reliably. So spin needs either strobe flashes a fraction of a millisecond apart or a much
  faster camera, plus markings on the ball (a printed line or logo) that can be followed.
- **Mounting:** commercial overhead units want a ceiling of about 9 ft (2.7 m), 10 ft is more comfortable, with the
  cameras slightly in front of the ball looking back at it. The rig has to survive a mishit.

Commercial overhead monitors, for scale (2026 prices as listed by retailers, not checked with makers): NVISAGE N1
about $4,995, Laon VTrack about $5,000, Uneekor EYE XR about $7,000, TruGolf Apogee about $8,000, Foresight Falcon
about $15,000. Each uses two to four high-speed cameras and infrared light.

### Three options

Prices are rough part costs in USD from retailer listings (November 2026), before a mount, cables and a ball
marking tool. Check current prices before buying.

#### $ — Raspberry Pi 5 and Pi global-shutter cameras, about $200-350

- **Cameras:** two Raspberry Pi Global Shutter Cameras (Sony IMX296, 1.6 MP mono-capable, about $50-65 each; the
  InnoMaker mono version needs no soldering) with small C/CS-mount lenses (about $10-25 each).
- **System:** one Raspberry Pi 5 (about $80) drives both cameras on its two camera ports, does all the processing
  and sends each shot over the network, the way the Square sends it over Bluetooth. An infrared LED bank (about
  $12) and an LED driver (about $8) on a 24 V supply (about $15) make the strobe.
- **Data engine:** the camera runs about 60 fps at full frame, far too slow to film the ball. Instead the first
  camera watches the teed ball and spots it moving; the second takes one long exposure while the strobe fires
  several times, so one frame holds several sharp images of the ball a known time apart. Speed, direction and
  launch come from where those images sit; spin from how the ball's markings turn between them. This is how
  [PiTrac](https://github.com/PiTracLM/PiTrac), an open-source DIY monitor, works (it reports ball speed, launch
  angles and 3-axis spin; [LaunchTrac](https://github.com/r87-e/LaunchTrac) is a cheaper offshoot claiming under
  $200 in parts).
- **Pros**
  - Cheapest by far, and every part is easy to buy.
  - Self-contained like the Square: the game PC only receives numbers, so it costs no frame rate.
  - Working prior art: PiTrac has already solved triggering, strobe timing and spin from strobed images on this
    exact hardware.
  - Strobed images capture spin a fraction of a millisecond apart, better than a 500 fps camera manages.
- **Cons**
  - PiTrac says itself it is young and only just works; its developers report trouble finding the ball in
    overlapping images at the cameras' low resolution. Expect to tune, not just build.
  - Its geometry isn't designed for a ceiling: an overhead version is our own work to design and calibrate.
  - Almost no club data: one strobed frame after impact says little about the club, and nothing films the swing.
  - Only one usable image of each shot, so no slow-motion replays (impact snapshots, yes).
  - The high-current strobe driver and its timing are the fiddliest part to build.
  - Reusing PiTrac's code means following its licence: check it before copying anything into this MIT project.

#### $$ — two USB3 machine-vision cameras on the game PC, about $900-1,300

- **Cameras:** two mono USB3 cameras with the Sony IMX287 sensor (0.4 MP, 720×540, about 525 fps at full frame,
  more with a smaller window), about $320-375 each from makers such as Vision Datum or Daheng (Basler's equivalent
  lists at €379). C-mount lenses about $100-165 each. A bank of infrared LEDs on a home-built pulse driver (about
  $100-150, not yet priced part by part), fired with the cameras by a small microcontroller, so every exposure gets
  a flash of about 15 µs. See "Parts for the $$ build" below.
- **System:** the game PC does the processing. Each camera sends about 200 MB/s at full frame and speed, so two
  need USB3 ports on separate controllers (or a plug-in USB3 card), and active USB3 cables to reach the ceiling.
  Record into a rolling memory buffer and keep only the frames around the shot, triggered when the ball moves.
- **Data engine:** a few to ten frames of ball flight per camera, triangulated in 3D from the two calibrated
  cameras: speed, launch, direction and (with a marked ball) spin for full shots; club path, attack angle and face
  from the club head in the frames before impact. Written in C# with OpenCV (OpenCvSharp) and the camera maker's
  SDK, in this repository, behind `RangeShot.Source`.
- **Pros**
  - Real high-speed film of every shot: several frames of the ball and the club, with no strobe tricks needed.
  - The best value for data: stereo 3D, club data and spin are all within reach with one rig.
  - The same frames give the impact snapshots and slow-motion replays (1.4) the Square can't share.
  - Hardware trigger lines keep the two cameras and the lights exactly in step.
  - Can be built in stages: one camera first (speed and direction), the second when the first works.
  - No extra computer, and the code lives in this repository with its tests.
- **Cons**
  - Heavy on the game PC: two camera streams, image processing and the 3D world at once; a weaker PC may need a
    lower game graphics setting, and the host PC requirements go up.
  - USB3 is fussy over distance and on shared controllers; long cables need active ones.
  - Camera SDKs are mostly Windows and vendor-specific, which ties the code to one camera maker unless it goes
    through a common layer (GenICam).
  - 0.4 MP is coarse for the club face and for reading ball markings from ceiling height; needs lenses chosen
    for a small field of view.
  - High-spin wedges (120° of turn between frames) are still at the edge of what 500 fps can track.
  - Four to six times the cost of the $ option.

#### $$$ — three or four cameras and a dedicated processor, about $2,500-4,000

- **Cameras:** a stereo pair of fast cameras for the ball (IMX287 at 525-600 fps, as in $$) plus one or two
  higher-resolution cameras for the club and impact (for example IMX273, 1.6 MP at about 250 fps, about $330-405
  each), or brand-name equivalents (Basler, FLIR/Teledyne) for better drivers and support. A stronger infrared
  strobe bank to light a bigger area, with a hardware trigger shared by all cameras.
- **System:** a separate processor beside the cameras, either an NVIDIA Jetson Orin Nano Super (about $249 list,
  often $350-400 in stores; two camera ports plus USB3) or a small PC with a graphics card, which sends finished
  measurements to the game over the network as the Square does. GigE cameras with power over Ethernet are an
  alternative to USB3: one cable each to the ceiling, over longer runs.
- **Data engine:** the $$ engine plus a dedicated club pipeline (head tracking, face angle and impact location
  from the high-resolution views), and machine-learning detection on the processor's graphics chip for unmarked
  balls and clubs. This is roughly how the commercial four-camera units work.
- **Pros**
  - Closest to a commercial overhead monitor: full ball and club data, including impact location.
  - The game PC does nothing extra; the rig works with any PC, including a guest's, like the Square.
  - Lots of video for the AI coach (1.4): several angles, higher resolution.
  - Room to grow: more cameras or faster models without changing the game.
- **Cons**
  - Approaching the price of a budget commercial overhead monitor (about $5,000) once the mount, cabling and
    lighting are done, without its accuracy guarantee or support.
  - By far the most software: synchronising four cameras, calibrating them together, a club pipeline and a
    trained model, plus a second machine to keep updated.
  - Jetson development is its own toolchain (Linux, CUDA), separate from the Windows game.
  - More cameras means more calibration that has to stay true if the ceiling mount gets knocked.

### Recommendation

Start with **$$**, one camera first. It's the cheapest rig that actually films the ball at speed, so it tests the
hard question (can we measure a shot as well as the Square?) without strobe tricks or a second computer, and every
part carries on into a $$$ build if the numbers work out. Meanwhile read PiTrac's code for its triggering and spin
methods: if they prove robust, a **$** build is the route to a monitor friends can copy cheaply. Leave **$$$**
until the $$ rig has shown, shot by shot against the Square, that the data engine is accurate.

### Parts for the $$ build

Chosen from retailer listings and datasheets in November 2026. The sums here are worked out, not measured; nothing
has been bought or tested yet.

- **Camera: Daheng MER2-041-528U3M** ($319 at RMA Electronics): IMX287 mono, global shutter, 720×540, 528 fps, about
  13 cm of ball travel per frame at 70 m/s.
  - Don't buy an "-L" version: on the sister 608 model, -L means no I/O at all. The 8-pin I/O cable is also needed.
  - A reseller lists 52 µs as its shortest exposure (2.6 px of blur at 70 m/s), so the flash has to set the
    sharpness: about a 100 µs exposure with a 15 µs flash inside it.
  - The 528 manual couldn't be opened, so its output timing is unchecked. The plan assumes a microcontroller
    triggers both cameras and the flash.
  - Worth pricing instead: the MER2-041-608U3M-HS, with 608 fps and exposures down to 1 µs, which makes flash timing
    much easier.
- **Lens: Computar M1214-MP2** (12 mm f/1.4, 2/3", about $97-166, 30.5 mm filter thread). Lock the focus under the
  infrared light with the filter on, and put the long side of the sensor along the target line. At 2.4 m from lens
  to ball:

  | Focal length | Field of view | Ball size | Fit |
  |---|---|---|---|
  | 8 mm | 1.49 × 1.12 m | 21 px | Too coarse |
  | **12 mm** | **0.99 × 0.75 m** | **31 px** | **Best balance** |
  | 16 mm | 0.75 × 0.56 m | 41 px | Better for spin, but fewer frames |

  With the cameras about 0.5 m apart, launch angle error should be about 0.25°, assuming quarter-pixel tracking and
  good calibration. Spin is the main risk: a 3-4 mm ball marking is only 2-3 px wide.
- **Light: two 100 W COB LED modules** with lenses of about 30°.
  - Start with 730 nm, as PiTrac does: the sensor is about 1.7× more sensitive there than at 850 nm (going by the
    sister model's response chart), but it shows a dim red glow. Use 850 nm if that matters more than the signal.
  - Check the real power: PiTrac's 730 nm part draws about 100 W, while the Lumimax L100W45-IR850 is sold as 100 W but
    is rated 3 A at 14-16 V, about 45 W. Its pulse rating wasn't found: get it from the maker before going above 3 A.
  - A constant-current LED driver is too slow to make the flash. Use a capacitor bank, a MOSFET with a gate driver,
    and a pulse-limit circuit so a fault can't leave the LED on.
  - Estimate: one 850 nm module at about 7.5 A, f/1.4 and 15 µs gives about 2,000 electrons per pixel on a white ball,
    10-20% of full scale before gain (assumes 35% LED efficiency; published full-well figures for this sensor
    disagree). Usable but tight, and dark clubs will be much dimmer.
  - Ready-made strobes cost too much for this build: the XR256 runs about $450-2,800, and the listings seen have a
    10° beam, too narrow for this field of view.
  - Safety: 850 nm is almost invisible but strong enough to hurt eyes at close range, so don't look into it on the
    bench.
- **Filter:** 700 nm longpass for 730 nm light, or an 850 nm filter for 850 nm light, on the lens's 30.5 mm thread.
- **Ruled out: SVPRO USB webcam-style cameras**, even several of them. The global-shutter models found top out at
  90-210 fps (33 cm or more of ball travel per frame), and frame rates don't add up without a hardware trigger: a 1 ms
  mismatch between two cameras is 7 cm of ball travel. They usually have no strobe output either. One could still be a
  cheap swing-video camera for the AI coach (1.4). The specific listing checked (B0H15FHSQZ) couldn't be opened, and no
  SVPRO camera with an IMX287 was found.

### Tasks

- [ ] **Cameras:** buy one camera with its I/O cable, one lens, one filter and one LED module (see "Parts for the $$
      build"); on the bench, confirm the frame rate, exposure, flash sync and how bright a ball looks from 2.4 m; then
      the second set for stereo
- [ ] **System:** a rolling frame buffer on the game PC, triggered by the ball moving (frame difference), keeping a
      few frames before and after impact; measure the CPU and USB load during a round
- [ ] **Ball and club data engine:** calibrate the cameras, find the ball in each frame, fit its path in 3D and
      work out speed, launch, direction and spin (from the ball's markings, or estimated from launch and club data
      if spin can't be seen). Club path and face from the club head in the frames before impact. Check every number
      against the Square shot by shot, using the recorded sessions (0.8)
- [ ] **PiTrac review:** read its licence, triggering and spin code, and decide whether a $ build is worth it
- [ ] **Bonus:** the same cameras give the impact snapshots and slow-motion replays the Square can't share

## Later / nice-to-have

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
