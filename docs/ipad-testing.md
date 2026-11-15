# Trying Grimes Golf on an iPad

How to put the test build on your own iPad from a Windows PC, with a free Apple ID and no Mac, and what to check
once it's there. It takes about half an hour the first time and five minutes after that.

This is a first test build, not a finished app: the screens were made for a mouse and keyboard, a Square connects over
Bluetooth but that hasn't been tried with a real one yet, and the Garmin R10 isn't supported on iPad. Finding out what
works is the point.

## Contents

- [What you need](#what-you-need)
- [1. Download the build](#1-download-the-build)
- [2. Set up the PC (once)](#2-set-up-the-pc-once)
- [3. Install it on the iPad](#3-install-it-on-the-ipad)
- [4. Let the iPad run it (once)](#4-let-the-ipad-run-it-once)
- [The limits of a free Apple ID](#the-limits-of-a-free-apple-id)
- [What to check](#what-to-check)
- [Sending back the logs](#sending-back-the-logs)
- [If something goes wrong](#if-something-goes-wrong)

## What you need

- An iPad with an **A12 chip or newer** on **iPadOS 15 or later**: iPad (8th generation, 2020) or newer, iPad Air
  (3rd generation) or newer, iPad mini (5th generation) or newer, or an iPad Pro from 2018 on. It won't install on
  anything older.
- A Windows PC and a cable from it to the iPad.
- An Apple ID. A free one is enough. Sideloadly asks for its password to sign the app with Apple; if you'd rather not
  type your main one into another program, make a second Apple ID for this at [account.apple.com](https://account.apple.com).
- About 400 MB free on the iPad.

## 1. Download the build

1. Sign in to GitHub and open the repository's **Actions** tab.
2. Pick **iPad build** on the left, then the newest run with a green tick.
3. At the bottom of the run's page, under **Artifacts**, click **GrimesGolf-unsigned-ipa**. It downloads as a zip
   (about 85 MB).
4. Unzip it. Inside is `GrimesGolf-unsigned.ipa`: that's the app.

To make a fresh build first, press **Run workflow** on the same page and wait about 8 minutes. Downloads are kept
for 90 days.

## 2. Set up the PC (once)

Sideloadly needs Apple's own installers of iTunes and iCloud, **not the Microsoft Store versions**.

1. If iTunes or iCloud came from the Microsoft Store, uninstall them (Settings > Apps > Installed apps).
2. Install iTunes from Apple: [apple.com/itunes/download/win64](https://www.apple.com/itunes/download/win64).
3. Install iCloud from Apple, using the link on [sideloadly.io](https://sideloadly.io) (under the Windows download:
   "web version" of iCloud). You don't need to sign in to it.
4. Install Sideloadly from [sideloadly.io](https://sideloadly.io) (the 64-bit Windows download).
5. Plug the iPad into the PC, unlock it, and tap **Trust** when it asks whether to trust this computer. Open iTunes
   once and check the iPad shows up there, then close it.

## 3. Install it on the iPad

1. Open Sideloadly with the iPad plugged in and unlocked. The iPad's name appears under **iDevice**.
2. Drag `GrimesGolf-unsigned.ipa` onto the big IPA button on the left.
3. Type your Apple ID under **Apple ID** and press **Start**.
4. Type the password when asked, and the six-digit code Apple sends to your devices.
5. Wait for **Done** at the bottom (a minute or two). Grimes Golf is now on the iPad's home screen, but won't open
   yet.

## 4. Let the iPad run it (once)

On the iPad:

1. **Trust yourself as the developer.** Settings > General > VPN & Device Management > tap your Apple ID under
   Developer App > **Trust**.
2. **Turn on Developer Mode** (iPadOS 16 and later). Settings > Privacy & Security > scroll to the bottom >
   **Developer Mode** > on. The iPad restarts; after it does, unlock it and tap **Turn On** when it asks.
   (Developer Mode only appears in Settings after an app has been installed this way.)
3. Open Grimes Golf. The first start takes a little longer while it unpacks the course.

## The limits of a free Apple ID

- **The app stops opening after 7 days.** Install it again the same way (steps 3.1 to 3.5) and it carries on; your
  settings and rounds are kept as long as you don't delete the app first. Sideloadly can also renew it for you if
  it's left running on the PC with the iPad on the same Wi-Fi.
- **Three apps at a time.** A free Apple ID can have three apps installed this way on a device.
- These go away with the paid Apple Developer account and TestFlight, which is the next step.

## What to check

Turn on **Settings > Display > Show the frame rate** first. It shows the frame rate in the top-left corner of the
view and writes it to the log every half minute, so the numbers come back with the logs.

Work down the list and note what happens; "it crashed here" is as useful as "fine".

| # | Check | What to note |
|---|---|---|
| 1 | **It starts** | Does it reach the main menu? How long does that take? Anything odd on the way (black screen, wrong way up)? |
| 2 | **Main menu** | Do New Game, Driving Range, History and Settings open and close by tapping? |
| 3 | **Driving range** | Tap **HIT SHOT** a few times. Does the ball fly and the numbers fill in? Does changing club, the power slider and the shot shape work by touch? |
| 4 | **A course hole** | New Game > Deer Run > start. Does the hole load (how long?), does the fly-over play, can you hit, aim with the arrows and by tapping the map, and finish a hole? |
| 5 | **Frame rate** | On the first tee, read the frame rate at each **Graphics quality**: Low, Medium, High, Ultra (Settings > Display; wait ten seconds after each change). Then watch it during a ball flight. Does the iPad get hot? |
| 6 | **Text and layout** | Is anything too small to read or to tap, cut off at the edges, or hidden by the rounded corners or the home bar? Try **Text size** Large and Extra large. A screenshot of anything wrong helps (top button + volume up). |
| 7 | **Touch** | What can't you do without a keyboard? Expected so far: the physics tuner, replaying a shot, the flag in/out and skipping the fly-over have only keys. Do drop-down lists, sliders and scrolling lists work with a finger? Does typing a player's name bring up the keyboard? |
| 8 | **Saving** | Play two holes, then close the app completely (swipe it away) and open it again. Does **Resume Game** bring the round back on the right hole with the right scores? Does History list the range shots? |
| 9 | **Sound** | Club, ball and cup sounds; does the silent switch or volume behave as you'd expect? |
| 10 | **Coming back to it** | Switch to another app for a minute and come back. Still running? |
| 11 | **A Square** | See [Connecting a Square](#connecting-a-square) below. |

If there's time, two extras (the first once you've done check 5):

- **The other renderer.** Run the workflow again with **Renderer: mobile**, install that build over the top, and
  repeat check 5. Mobile is lighter and may look a little different; the comparison decides which the iPad keeps.
- **Online play.** Host or join a lobby with the Windows app (both need the same version).

## Connecting a Square

Needs a real Square Omni, and the Square phone app closed (a Square talks to one device at a time). The Garmin R10
isn't supported on iPad yet; **Auto** and **Square** both look for a Square.

1. Switch the Square on, and open Grimes Golf. The first time it scans, iOS asks whether the app may use Bluetooth:
   tap **Allow**. (If you tapped Don't Allow: Settings > Grimes Golf > Bluetooth.)
2. Open the monitor list (the Bluetooth button on the HUD, or Settings > Monitor) and see whether the Square shows up
   and connects. Note how long that takes.
3. On the driving range with the Square connected: change club (does the Square follow it?), hit a few shots and
   compare the numbers with what the Square shows. Does the battery level show?
4. Close the app and open it again: does auto-connect find the Square by itself?
5. Switch the Square off, then on again: does it reconnect?

Whatever happens, `logs/godot.log` has the story, including the `Connecting to the Square...`, `Connected`, `Device:`
and `Shot raw:` lines. A Square that never shows up, or connects and then goes quiet, is a finding: send the log.

## Sending back the logs

The app's data folder shows in the iPad's **Files** app:

1. Open **Files** > **Browse** > **On My iPad** > **Grimes Golf**.
2. The useful files are `logs/godot.log` (everything the app said this run, including the device line and the
   frame rates; the runs before it are beside it), `crash.log` (errors, if there were any) and `settings.json`.
3. Press and hold a file (or **Select** several) > **Share** > Mail, AirDrop or Save to iCloud Drive.

Send the log from the run where something went wrong: a new one starts each time the app opens.

With the iPad plugged in, the same folder is also in iTunes on the PC (the iPad's page > **File Sharing** > Grimes
Golf).

## If something goes wrong

| What happens | Try |
|---|---|
| Sideloadly doesn't see the iPad | Unlock the iPad and tap Trust; check iTunes sees it; make sure iTunes and iCloud are Apple's installers, not the Store's |
| "Unable to install" or an error about the device | The iPad is older than the build allows (see [What you need](#what-you-need)), or it's full |
| "Maximum number of apps" or of app IDs | The free limits: delete another app installed this way, or wait a week |
| The app's icon is there but it won't open ("Untrusted Developer") | Step 4.1 |
| It says Developer Mode is required | Step 4.2 |
| It opened last week and now closes straight away | The 7 days are up: install it again |
| It opens to a black screen or closes during start-up | That's a finding: send `logs/godot.log` and `crash.log` if they're there, and say which iPad and iPadOS version |
