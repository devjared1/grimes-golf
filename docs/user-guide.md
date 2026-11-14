# User guide

How to play Grimes Golf: the screens, the games, the rules and the controls. For installing it and what a PC
needs, see the [README](../README.md).

## Contents

- [Connecting the launch monitor](#connecting-the-launch-monitor) ([Garmin Approach R10](#garmin-approach-r10))
- [Main menu and in-game menu](#main-menu-and-in-game-menu)
- [Hitting shots](#hitting-shots)
- [Camera](#camera)
- [Driving range](#driving-range)
- [Playing a course](#playing-a-course)
- [Game setup](#game-setup)
- [Wind](#wind)
- [Lies, penalties and putting](#lies-penalties-and-putting)
- [Multiplayer and house rules](#multiplayer-and-house-rules)
- [Settings](#settings)
- [Controls](#controls)
- [Physics tuner](#physics-tuner)
- [Saved data](#saved-data)
- [Playing online](#playing-online)

## Connecting the launch monitor

Two launch monitors work, each straight over Bluetooth with no other app, connector or subscription: the **Square
Golf Omni** and the **Garmin Approach R10** (the R10 is new: see [its own section](#garmin-approach-r10)). The app
looks for both unless **Settings > Launch monitor** says which one.

**Square Golf Omni**: turn it on and close the Square phone or tablet app (only one app can hold the connection).
Don't pair the monitor in Windows Bluetooth settings: it doesn't pair, and the app connects to it directly.

**Connecting**: with auto-connect on (the default) the app connects by itself, to the monitor used last time, or
to the strongest one it hears if that one isn't found within 8 s. If the link drops, a big banner says
"Launch monitor disconnected — reconnecting…" and it keeps retrying (every few seconds) until it's back; Disconnect
in the drawer stops that. The main menu, the drawer and the status chip's tooltip say which monitor is connected.

**Aligning with the stick** (Square only): the **Align** button (beside the club, once a monitor is connected) or **Align with
Stick** in the menu, any time between shots, even mid round. Lay the monitor's white stick on the mat pointing at
your target. The panel shows its angle live; pick **Use This Alignment** once it's within 5°, then take the stick
away. The button then shows the alignment you set ("Aligned R1.2°", with the time on its tooltip). It's only known
for this session, and is forgotten if the monitor restarts. Cancel leaves the alignment as it was.

**Ball position** (hidden for now; the top bar still shows when the ball is ready): while the monitor sees a ball, a small panel shows where it is, from above, and whether it's on
a tee or the mat, so you can tell why it isn't Ready. The green box is where balls have gone ready so far, as a
guide: the monitor doesn't report its exact limits.

**Monitor drawer** (`L` or the status chip): every supported monitor it can hear over Bluetooth (with signal
strength), Connect / Disconnect, the monitor's details (a Square's serial, hardware, firmware (LM / MMI / launcher),
uptime, state and ball-in-position status; an R10's model, serial, firmware and state), its battery, the left-handed
setting, and the log.

When a new player is up, the Square is switched to their hand and the club suggested for their shot. (The R10 has
no club setting: it measures whatever is swung.)

### Garmin Approach R10

The app talks to the R10 the way the Garmin Golf app does, so it needs neither the Garmin Golf app nor a Garmin
Golf subscription. This support is new and has only been tested against a simulated R10 so far, so please report
what happens with a real one (see [Sending a log](#sending-a-log)).

**First time: pair it with the PC.** The R10 only lets one device connect, and only shows up when nothing else is
connected to it.

1. On your phone, close the Garmin Golf app and turn the phone's Bluetooth off (or move the phone away), so the phone
   doesn't connect to the R10 first.
2. With the R10 **turned off**, hold its button until the status light turns **solid blue**: it's in pairing mode.
   (Flashing blue means it's trying to reconnect to the phone it knows.)
3. In Grimes Golf, open the drawer (`L`). The R10 appears as **Approach R10**; with auto-connect on it connects by
   itself, otherwise pick it and **Connect**. The first time, Windows pairs with it (accept if Windows asks). If the
   app says it can't pair, pair it once in **Windows Settings > Bluetooth & devices > Add device > Bluetooth** while
   the light is solid blue, then connect from the app.

After that, just turn the R10 on (and keep the phone from grabbing it): the app finds it and connects, and wakes it
if it has gone to sleep.

**Setting it up** (from Garmin's owner's manual):

- Put the R10 on the floor **6 to 8 ft (1.8 to 2.4 m) behind the ball**, on its tripod, level and steady. On a
  hitting mat, its bottom edge must sit above the mat.
- Indoors, the ball must be **at least 8 ft (2.4 m) from the net**, with room for high and wayward shots and for the
  swing.
- Point the red **alignment line** on top through the ball at your target (a club laid on the floor between them
  helps). The ball can be up to 1 ft either side of that line, in a 2 x 2 ft area starting 6 ft from the R10. Its
  angles are measured from the line it's pointing along.
- The light **flashes green** when it's ready for a shot, is **solid red** while it's busy, **flashes white** when
  asleep and **flashes red** on an error. The app's Ready light follows it ("Ready — hit!", "Measuring…").

**What's different from a Square**: no ball sensor (Ready means the R10 is armed, not that it sees a ball), no
alignment stick (the Align button is hidden: line it up as above), and no club setting. Its spin is usually
estimated from the launch rather than read off the ball (unless you use a marked ball); the log says so for each
shot. Club path, face angle and attack angle come through when it tracked the club; on a slow swing it can't, and
the shot is played from the ball numbers alone.

**Troubleshooting**:

- **It isn't found**: it's connected to something else. Close the Garmin Golf app, turn the phone's Bluetooth off,
  turn the R10 off and on, and press **Scan** in the drawer.
- **"Windows can't pair" or "didn't answer the handshake"**: remove the R10 in Windows Settings > Bluetooth & devices,
  put it back in pairing mode (solid blue, see above) and connect again.
- **"Monitor tilted — level it"**: move it to a level, steady surface, more in line with the ball.
- **"Radar overloaded"**: something in front of it reflects too much (a metal frame, a close wall). Move it or the
  obstruction, and keep to the distances above.
- **Directions look off**: the alignment is out. Hit a short, straight wedge: if the app shows it starting right, the
  R10 is aimed left of your target line (and the other way round). Turn it a little and try again.
- **It keeps dropping**: keep the PC's Bluetooth adapter in sight of the R10 and within a few metres; USB adapters at
  the back of a PC can be blocked by the case.

### Sending a log

Every R10 session saves its raw Bluetooth traffic to `monitor-logs\r10-<date>-<time>.log` in the data folder
(`%APPDATA%\Square Range 3D`), and the drawer's log (also in `logs\godot.log` there) has a line per shot with the
numbers exactly as the R10 sent them. The raw log leaves out the messages that carry the R10's serial number. Send
both files (and what the Garmin Golf app showed for the same shots, if you can) with any report.

## Main menu and in-game menu

**Main menu** (where the app opens, under the Grimes Golf Simulator logo): **New Game** (a list of courses with
their holes, par, length and a short description, and the **Player List** on the right, where changes apply straight
away: **Add** picks a saved guest or adds a new player, with **Game Setup**, below, under it; then **Start Round**), **Resume Game** (shown when a round is unfinished, with its course, game, hole and players), **Driving
Range**, **History** (below), **Settings** and **Quit**, plus the
launch monitor's status (with a button for the monitor drawer) and the version. New Game and Resume Game show a
**loading screen** (the logo, the course's logo and what's being loaded) while the course map, elevation and 3D
world are built.

**History** (main menu) shows what's been saved, read again each time it opens. **Rounds** lists the finished
rounds, newest first, each with its scorecard, every player's score against par (only the holes they played count
for someone who joined part-way) and the house rules it was played with. **Practice** lists the days shots were hit,
on the range or a course, with each player's averages per club: shots, carry, total, longest and ball speed.
Averages come from launch monitor shots only; on-screen swings are counted but left out, and so are putts. Arrows or
the D-pad move through the list, and Esc or gamepad B closes it.

**Top bar** (in play): the logo and version, "Driving range" on the range, the **Ready / Not ready** light in the
middle (green once the last shot has finished and, with a monitor connected, the monitor sees a ball ready to hit),
the player chip (with 2+ players: whose turn it is, or on the range who the shots are saved under; click it there
for the next player), the launch monitor status (ready state and battery; click it for the monitor drawer) and
**Menu** (`M`, `Esc` or gamepad Start).

**In-game menu**: on a course, Scorecard, Settings, Previous Hole, Next Hole, Restart Hole, Add / Remove Players
(players can join on the current hole, where they start from the tee and earlier holes don't count for them, or
leave), Restart Game and End Game (keep the round for Resume Game, or discard it); on the range, Settings, Clear
Session and Main Menu. Both have the camera mode, the launch monitor drawer and the physics tuner.

## Hitting shots

**Hitting from the screen** (no monitor needed): a **power** slider (1-100% of the club's full carry; for the
putter, of the roll shown beside it), **shape** (Draw / Straight / Fade: starts right and curves back left for a
right-hander, or the opposite) and **spin** (Low / Medium / High: lower flight with more roll, or higher with more
stopping), then **HIT SHOT** (`Space`). On a course the power is set for each new shot (the distance it plays, from
that lie; putts get the pace from the green read) and can be changed (`↑ / ↓`, Shift = 5%). Shape and spin go
back to Straight and Medium after every shot. These controls go away while a monitor is connected, and come back
if it drops.

**Results wait for the ball**: the shot's numbers (range) and the result card, the next shot's distance, lie and
club (course) only appear once the ball has stopped.

## Camera

**Camera**: in Follow mode (`C`) it chases the ball until it stops. Before a shot, **Zoom** (`Z`) takes it down
the aim line to look at where the ball will land with the aim and power set; it follows aim and power changes,
and comes back behind the ball when pressed again or when the shot is hit. Before the first tee
shot on each hole it flies down the hole from behind the tee to the green (`Space` or `Esc` skips it; can be
turned off in Settings).

## Driving range

**Range**: each shot flies down a 3D range with a camera that follows the ball, trails, landing rings and a
minimap. Aim left and right (`← / →`, the ‹ › buttons, or click on the minimap), or step through the target
flags (50-300 yd) to aim straight at one (`F` / `Shift+F`, gamepad Y, or the target button); a line on the range
and the minimap shows where the shot is aimed and how far the power set carries it. The bottom bar shows Carry and Total large, with ball and club speed, smash, launch, direction, apex,
spin, spin axis, descent, offline, roll and hang time. The right column has the session history and a per-club
summary (average carry and total, offline spread).

## Playing a course

Pick a course with **New Game**:

- **Deer Run (Moulton, AL)**, all 18 holes imported from OpenStreetMap: real routing, hole lengths and pars,
  dog-legs, tee, green and bunker shapes, with the course property line as out of bounds. Fairways are mapped for
  holes 1-4; on the others 35-yd fairways are generated along each hole line. The trees are the real ones, where
  they stand and as tall and wide as they are, measured from lidar.

**Get More Courses** on that screen lists the courses you can download, with their holes, par, length and download
size. Press **Download** and the course joins the New Game list; **Update** appears when a newer version of one is
published, and **Remove** deletes a downloaded course. It needs the internet only while you're using it. A course
you've downloaded plays offline like any other. To play a course online, everyone needs the same version of it.

The course screen is laid out round the ball, with the panels kept to the edges. Top left, the hole badge: the
course logo, hole number, par and length, the stroke being played and the score (to par, "thru 4"); under it whose
turn it is and the Mulligan button. Top right, the hole overview: the hole from above with the ball ringed in the
player's colour, a line from it to where the shot is aimed (it swings with the aim and stretches or shrinks with
the power) with that distance, and the flag with its distance (click or drag on the map to aim); under it, the
wind. Down the left, the shot's numbers (until the next shot): its launch (ball and club speed, launch, direction,
spin and spin axis) as soon as it's struck, then its result (carry, total, offline, roll, apex, descent and the
distance left) once the ball stops. Bottom left,
the lie card: the lie, the side and up / down slope angles, and the shot penalty (how much shorter the club
carries from there than from a clean, level lie); on the green, the green read with the flag, heat map and grid
buttons instead. Bottom right, the shot panel: distance to the pin with the rise or fall to it ("153 yds", "up 2'",
and what it plays), the club (its tooltip gives the carry from here), the aim, power, shape, spin and HIT SHOT.
With a monitor connected the swing does that work: the shot panel holds the distance, the club and the lie card,
and with more than one player a small leaderboard takes the bottom left. The aim line then runs out to the club's
carry from this lie (your club distances, or the typical ones), whatever the distance to the pin; `↑ / ↓` move it a
yard further or shorter (Shift = 5 yd). Aim with `← / →` or by clicking the map.
A line across the top appears after a shot only when there's something to say: holed, a penalty, relief, off a tree, who's
up next, the next hole (where the ball lies and how far it has left are on the lie card and the numbers); holing out announces the
score ("BIRDIE!") across the screen; the scorecard opens with `S`, from the menu, and after each hole. Once
everyone has finished a hole the next one starts by itself after 5 seconds (counting down on the NEXT HOLE button;
`Space` goes straight away, and the count pauses while a menu is open).

## Game setup

**Game Setup** (on New Game, under the Player List; it scrolls if large text leaves it too little room; every choice is a row of buttons, so a gamepad picks each with one press,
and the last setup is remembered):
- **Game type**: **Stroke Play**; **Scramble** (everyone in a team hits from the same spot, the best shot is picked
  for them: holed first, then fewest strokes, then nearest the pin from the better lie, and the whole team plays
  the next one from there; the result line says whose ball they're playing); **Best Ball** (everyone plays their
  own ball, each hole counts the team's best score); **Match Play** (hole by hole: the lower score wins the hole,
  the badge reads "2 UP" / "AS" / "1 DN" for whoever's up, the scorecard adds a row of who won each hole and the
  match's standing, "Ann wins 3 & 2"). With four players the teams are players 1 and 2 against 3 and 4; with two
  or three, a scramble or best ball is everyone together against par. Match play needs two players (or four, best
  ball two against two); a game that doesn't suit the players is greyed out, and plays as stroke play if picked.
- **Holes**: Full 18, Front 9 (holes 1-9), Back 9 (10-18) or Par 3s (just the par-3 holes), with the count, par and
  length shown. The badge and scorecard show the real hole numbers.
- **Environment**: wind (below); altitude (the course's own height from its elevation data, sea level, or 2,500 /
  5,000 / 7,500 ft: thinner air carries further); weather (Mild as tuned; Hot 95 °F; Cold 45 °F; Wet: rain falling
  round the camera under an overcast sky, and soft, wet ground).
- **Course**: fairway speed (Slow / Medium / Fast: how far the ball runs out), green firmness (Soft / Medium / Firm:
  approach shots check or release) and green speed (stimp 8-13).

The conditions are applied on top of the physics tuning while a round is played (the tuner still shows and saves
its own values; the range plays them as tuned), and are saved with the round, so Resume Game carries on with the
same game, holes and weather. The in-game menu shows them under its title.

## Wind

**Wind** (Game Setup: Calm, Light 2-7 mph, Breezy 7-13 mph or Windy 13-21 mph, with gusts on top): each round has a
prevailing wind that shifts a little from hole to hole. It blows full shots in the air (stronger higher up, as over
open grass); putts roll in still air. The wind panel shows its speed and an arrow for which way it blows across the
shot ("into, from the left"), the flag on the green streams with it, and the plays-like distance and club
suggestion allow for it (about 1% longer per mph into the wind, half that downwind).

## Lies, penalties and putting

**Trees are in play** on courses with surveyed trees (Deer Run). A trunk stops the ball and throws it back. A ball
through a crown may get through or may meet a branch, more likely the farther it travels inside and in thick woods;
a branch takes most of its speed and it drops near the tree. The result line says "Off a tree". The driving range's
trees are scenery.

Each shot is played from where the last one stopped. The app suggests a club for the distance and lie (and switches
the monitor to it). Lies are tee, fairway, fringe (the collar around greens), rough (a first cut beside the
fairways, tees and greens), deep rough, bunker and green. Shots from the rough, deep rough, sand and fringe lose
speed and spin, and sloping lies change the launch (uphill higher, ball above the feet pulls and draws), because a
monitor reading a ball off a mat can't see the lie. Balls bounce and roll by surface: high-spin wedges check and spin
back on the greens, the rough grabs. Sand takes the pace off: a ball rolling into a bunker runs on into the sand as
far as its pace takes it (it can't roll out again over the lip), and one landing in it makes a low hop and runs a
little, or buries near its pitch mark if it comes in steep and fast. Water and out of bounds are stroke-and-distance
(one-stroke penalty, replay from the previous spot); putts that stop inside the gimme circle (drawn round the hole)
are tapped in. The
cup has depth: a putt at the right pace drops in, one that's too firm for how far off-centre it is lips out (or
skips over if it's dead straight). The flag is pulled once you're on the green (`F` to leave it in).

With elevation, Deer Run plays on real ground:

- The ball flies until it meets the ground, so uphill shots come down short and downhill ones carry further. The HUD
  shows the distance a shot plays ("146 yd to the pin (plays 152)") and the caddie picks clubs by it.
- Landings are judged against the slope (into an upslope checks up, onto a downslope releases), and the ball rolls
  with friction by lie plus gravity along the slope: it runs down hills, stops on gentle slopes, and breaks on greens.
- On the green the aim starts straight at the hole, and the lie card's read says how much to allow for the break
  ("2.5 ft left"); with **Auto-align putts** (house rules) it starts along the read instead. Demo putts are struck at
  the read's pace.
- The ground is drawn as one mesh from the same grid, coloured by a coverage texture of the fairways, greens,
  bunkers and water, so the ball sits on what you see.


## Multiplayer and house rules

**Hot-seat multiplayer, 1-4 players** (set up in Settings): each player has their own ball, name, colour and
handedness. Everyone tees off in honour order (lowest score on the last hole first; player order on the first),
then whoever is farthest from the hole plays next. The hole card says "ANN TO PLAY" in her colour with everyone's
score, the other balls sit on the course with their names over them (and as dots on the minimap), and the result
card says who's up next. When the turn changes the monitor switches to that player's hand and suggested club. The
scorecard has a row per player (and per team in the team games).

**House rules** (Settings): mulligans per player per round (**Mulligan** on the hole card, `U`, numpad 0 or
gamepad X takes back the last shot of the round, penalty included, once the ball has stopped and before anyone
else hits; not a holed one), the gimme circle (4, 6, 8, 10 or 12 ft round the hole, drawn on the green), picking up
at double par, **Auto-align putts** (the aim on the green follows the read; off, it's at the hole), and
**Auto-putting** (nobody putts: once the ball stops on the green the hole is finished with 1 putt inside the gimme
circle, 2 inside a second circle twice as wide, drawn too, and 2 or 3 beyond it; double par still caps it). The
default is 2 mulligans, a 6 ft gimme circle and double-par pick-up, with both putting aids off. Player and rule
changes apply from the next round ("Save and start a new round" starts one now).

**The round is saved after every shot.** If the app is closed or crashes, **Resume Game** on the main menu
carries on with it, with the same players. **Restart Game** in the menu (or **New round** on the scorecard)
starts again.

## Settings

**Home player**: the app asks who's playing the first time it starts (**Not now** skips it, and it asks again next
time), and loads that player every time after. Give a name and a hand; the launch monitor type is read from the
monitor when it connects. Under **Club distances (optional)**, enter the carries you know in yards: the grey numbers
are estimates for the rest, worked out from yours. The distances set the caddie's club suggestions, the aim line and
how far swings from the screen go. Shots from a launch monitor are never changed, and guests use the typical
distances. Edit all of this from **Settings**.

**Saved guests** (Settings): friends you play with often, kept after the app closes (name and hand only). Add one
with **+ New guest**, or **Save** on their row in the players list; press **Save** at the bottom of Settings to keep
the change (**Save** on a row of New Game's Player List keeps them at once). The app still starts with just the home
player: put a guest in the round with **Add** on New Game's Player List, or **+ Saved player** in Settings or under
Add / Remove Players during a round. A player you don't save lasts until the app closes.

**Settings** (main menu, the in-game menu, numpad `*` or gamepad Back): the home player, players (1-4, name and left-handed),
house rules, auto-connect, the launch monitor to look for (**Auto**, any supported one; or only a Square Golf Omni
or only a Garmin Approach R10) and an optional exact **device name** (as listed under Devices nearby, for when there
are several), text size (Small / Normal / Large / Extra large: smaller for a laptop, bigger for
reading from the mat or a projector), full screen (the app always opens full screen; untick this for a window until you close it), graphics quality (Low / Medium / High / Ultra: anti-aliasing,
shadow detail and reach, bounced light and reflections in the water, how far 3D grass reaches round the camera, and soft focus on grass right in front of a low camera;
High is the default), the camera's hole flyover, sound effects (club, ball, cup and crowd; turn them off if you'd rather just hear your own strike), and a "Ready" chime when the launch monitor sees a ball and is ready for the shot (on by default, separate from the other sounds). The course last played and the camera mode are remembered too, so no command-line switches are
needed. The whole UI also scales up with the window (a full-screen projector shows it bigger), and switches to a
compact layout when large text leaves less room.

**Without a keyboard**: a gamepad, a wireless numpad or a presenter clicker can play a whole round (table below).

## Controls

| Action | Keyboard | Numpad | Gamepad | Clicker |
|---|---|---|---|---|
| HIT SHOT; after the hole, next hole | **Space** | 5 / Enter | A | Next (Page Down) |
| Power 1% / 5% (with a monitor: aim 1 / 5 yd further or shorter) | **↑ / ↓**, Shift + ↑ / ↓ | | | |
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

## Physics tuner

**Physics tuner** (`T` or **Physics Tuner** in the menu) opens in its own window, so it can sit beside the game or on
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
`src/GrimesGolf.Godot` when run from Godot); "Use these values at start-up" keeps the current values for the next
launch. Wind direction is relative to the target on the range and a compass bearing on a course.

Carry, total, apex, bounce and roll come from the app's own physics engine, not from the monitor, so expect them to
differ somewhat from the Square app. The engine's coefficients and the lie effects are provisional and not yet
validated against measured shots.

## Saved data

Saved data lives in `%APPDATA%\Square Range 3D` (the app's earlier name, kept so existing data stays put): `settings.json`, `current-round.json` (the round in
progress: every player's scorecard and ball, and whose turn it is), `rounds\` (finished rounds, the scorecards of
everyone who played), `sessions\` (every shot, range and course, with the player's name, one JSON line each, a
file per app session; a shot taken back with a mulligan is written again marked `"Mulligan": true`), `crash.log`
(errors and unhandled exceptions), `logs\` (Godot's own log, with everything the drawer's log shows) and
`monitor-logs\` (a Garmin R10's raw Bluetooth traffic, a file per connection). Shots are saved from the app's "shot finished"
event, which carries everything about a shot (launch data, club, player, lie, stance, plays-like distance, result,
everyone's standing). Development runs that hit shots by themselves (`--demo-shots`, `--hole`, `--hit-at`,
`--tune`) don't save or resume anything.

## Playing online

Online play is in development and only appears in debug builds for now (see the
[README](../README.md#online-play)). In those builds:

1. Everyone connects their launch monitor (a Square Golf Omni or a Garmin R10) first: the round can't start until
   every PC's monitor is live.
2. The **host** picks **Play Online**, enters the relay's address (it's remembered) and picks **Host a Lobby**. The
   lobby shows a 5-character code to give to the others.
3. Each **guest** picks **Play Online**, enters the same relay address and the code, and picks **Join**. Their
   players (from their own Settings) appear in the host's lobby.
4. The host picks the course and can move players up the list: the seat order is the teeing order, and with teams
   players 1 and 2 play 3 and 4. The game, holes and conditions are the ones last chosen under New Game, and the
   house rules come from the host's Settings.
5. The host picks **Start the Round** once everyone is connected with a live monitor.

During the round only the PC whose player is up can hit; a ball hit on any other monitor doesn't count, and a
message says whose turn it is. Everyone sees every shot, the aim of the player up, and the same scorecard. HIT SHOT,
demo shots, the physics tuner, replays and jumping between holes are off.

- **A guest drops out:** if their turn comes, there's a 60-second countdown (the host can skip sooner), then their
  players are skipped and pick up each hole (double par, or the strokes already taken if more) until they're back.
  A launch monitor that disconnects gets 120 seconds.
- **The host drops out:** the guests wait, with a banner; after 2 minutes they're offered the main menu, with their
  scorecard kept.
- **Pausing:** the host can pause the round from the menu and carry it on later from Play Online (within 12 hours);
  guests get a **Rejoin** button.
- Each PC keeps its own players' shots, and every PC files the scorecard when the round ends.
