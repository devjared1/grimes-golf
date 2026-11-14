# Garmin Approach R10 protocol notes

How Grimes Golf talks to a Garmin Approach R10 over Bluetooth LE, and what is and isn't known. Garmin publishes no
SDK; everything here comes from community reverse-engineering, listed below. The code is in
`src/GrimesGolf.Core/Garmin` (platform-free, unit-tested) and `src/GrimesGolf.Bluetooth/R10BleLink.cs` (Windows).

**Status:** implemented from the sources and tested against a simulated R10 (`FakeR10Device`), not yet against
real hardware. Points that only a real device can settle are under [Open questions](#open-questions).

## Sources

| Source | Licence | Used for |
|---|---|---|
| [mholow/gsp-r10-adapter](https://github.com/mholow/gsp-r10-adapter) (C#) | MIT | The reference: its direct-Bluetooth mode's UUIDs, handshake bytes, acknowledgement layout, request order and `LaunchMonitor.proto`. Ported, with its notice in [NOTICE](../NOTICE). |
| [divotmaker/10over](https://github.com/divotmaker/10over) (the `tenover` crate, Rust) | Apache-2.0 / MIT | Cross-check: `docs/WIRE.md` and `docs/SEQUENCE.md` (MultiLink, GFDI, CRC, compressed headers, signs), and the golden protobuf bytes in `src/wire_golden.rs` used as test fixtures. |
| [jasonlandreth/GarminR10-BLE-Connector](https://github.com/jasonlandreth/GarminR10-BLE-Connector) (C#) | Apache-2.0 | Cross-check only: its protocol code and schema derive from gsp-r10-adapter, so it doesn't count as independent. |
| [kjhholt-alt/r10-bridge](https://github.com/kjhholt-alt/r10-bridge) (Python) | MIT | Read only. Its `docs/PROTOCOL.md` describes a 4-byte length and CRC-32, which contradicts both working implementations; its own tests say it has no captures yet. Not relied on. |
| [gaselcode/Garmin-Approach-R10-on-Linux](https://github.com/gaselcode/Garmin-Approach-R10-on-Linux) (Python/Rust) | none | Read only, no code taken. Uses channel 0 (`2810`/`2820`) and reads shots at fixed byte offsets, which doesn't match the framing below; not relied on. |
| [Garmin's Approach R10 owner's manual](https://www8.garmin.com/manuals/webhelp/GUID-E2BBF6BE-4276-436F-B697-59ABEFD61933/EN-US/Approach_R10_OM_EN-US.pdf) | — | Pairing mode, LED meanings, placement (for the user guide). |

## Layers

```
protobuf message (WrapperProto)
14-byte protobuf header            request id, offset, total, length
GFDI frame                         length u16, type u16, payload, CRC-16/ARC u16
COBS, between 0x00 bytes
MultiLink handle byte              first byte of every write and notification
BLE GATT                           20-byte writes and notifications
```

All integers are little-endian.

### GATT

| What | UUID | Notes |
|---|---|---|
| MultiLink service | `6a4e2800-667b-11e3-949a-0800200c9a66` | Everything below goes through it |
| Notify / write | `6a4e2812-…` / `6a4e2822-…` | The pair gsp-r10-adapter uses (the app's first choice) |
| Notify / write, channel 0 | `6a4e2810-…` / `6a4e2820-…` | The pair tenover uses; the app falls back to it |
| Measurement service | `6a4e3400-…` | `3401` measurement, `3402` control point, `3403` status: subscribed and logged, not understood |
| Battery | `0x180F` / `0x2A19` | Level 0-100, read and notify |
| Device Information | `0x180A`: `0x2A24` model, `0x2A25` serial, `0x2A28` firmware | Read once |

The R10 advertises as **"Approach R10"**, with Garmin manufacturer data (company `0x0087`) whose first two bytes are
the product number, **3622** (tenover). It only advertises while nothing is connected to it, and it must be
**bonded** (paired) with the host: both C# references require it to be paired in Windows first; tenover bonds through
the OS. The app pairs it itself if Windows hasn't (`DeviceInformationCustomPairing`, accepting the "just works"
confirmation), falling back to asking the user to pair in Windows Settings.

### MultiLink registration

Before anything else the host registers for the GFDI service. gsp-r10-adapter writes this to the write characteristic:

```
00                       handle (none yet)
00                       REGISTER
00 00 00 00 00 00 00 00  transaction id (u64)
01 00                    service 1 = GFDI
00
```

and waits for `00 01 <transaction u64> 01 00 <status> <handle> 00 00` on the notifier. The handle (typically 1-9)
is then the first byte of every write and is stripped from every notification. gsp-r10-adapter then writes
`<handle> 00` (a lone delimiter); the app does the same. tenover writes REGISTER to the notify characteristic instead
(transaction 1), which the R10 also accepts.

### COBS and GFDI frames

Each GFDI frame is COBS-encoded and sent between `0x00` bytes, cut into 19-byte pieces each behind the handle byte
(20-byte writes, for the default MTU). Frames can be split across notifications or share one.

```
length u16   the whole frame, length and CRC included
type u16     message type
payload
crc u16      CRC-16/ARC (poly 0xA001 reflected, init 0) over everything before it; check("123456789") = 0xBB3D
```

tenover documents a **compressed header** some Garmin devices switch to once the host says it speaks protocol 150:
byte 2 is `type - 5000` and byte 3 a transaction id with its top bit set. The app never says 150, so it shouldn't see
it, but the parser reads both forms.

| Type | Name | Direction | The app |
|---|---|---|---|
| 5000 | Acknowledgement: `type u16, status u8, …` | both | Sends one for every message received; logs a non-zero status |
| 5024 | Device information | R10 → host | ACKs with `[00]`, logs protocol, product and software version (not the unit id) |
| 5050 | Configuration (capability bitmap) | both | ACKs with `[00]` |
| 5011 / 5012 | FIT definition / data | R10 → host | ACKs with `[00]` |
| 5043 | Protobuf request | both | Its requests; the R10's alerts (states, shots, errors) come as 5043 too |
| 5044 | Protobuf response | both | Answers to its requests, matched by request id |

A 5043/5044 payload starts with a 14-byte header: request id u16, offset u32, total length u32, length u32 (the
R10's messages always fit in one: offset 0, length = total). Each is acknowledged as gsp-r10-adapter does:
`5000 | type | 00 | request id | 00 × 7`.

### Handshake and arming

The sequence the app follows (gsp-r10-adapter's):

1. Connect, pair if need be, subscribe to the notifier (and the measurement, status, control point and battery
   characteristics).
2. MultiLink REGISTER; wait up to 10 s for the handle.
3. ACK the R10's 5024 and 5050 (waiting up to 5 s for the 5050).
4. Protobuf requests, each waiting up to 5 s for its 5044: **wake-up**, **status**, **tilt**, then **subscribe** to
   launch-monitor alerts (alert type 8).
5. The R10 is armed (state Waiting) and pushes its state changes, shots and errors as alert notifications. It
   re-arms itself after every shot: no re-arm command is needed.

While connected the app asks for the status every 30 s (two unanswered and it drops the link and reconnects, as it
does for a Square), and sends wake-up again whenever the R10 drops to Standby (at most every 10 s). It de-duplicates
shots by shot id, because the R10 sends a shot again if it doesn't get the acknowledgement in time.

tenover does a fuller GFDI handshake: it answers 5024 with host information (protocol 150, so compressed headers
follow), and after ACKing the R10's 5050 sends its own 5050 with capability bit 30 (SwingSensor) set, subscribing
before waking. If a real R10 ignores the app's requests after the handshake, that's the alternative to try.

### Protobuf

`WrapperProto { EventSharing event = 30; LaunchMonitorService service = 38; }` (`R10Proto.cs` has the field numbers
for everything the app reads; gsp-r10-adapter's `LaunchMonitor.proto` is the full schema). The app encodes and
decodes these by hand (no protobuf library): requests are a few bytes each, e.g. wake-up is `b2 02 02 1a 00`.

- Requests: `service.status_request (1)`, `wake_up_request (3)`, `tilt_request (5)`, `event.subscribe_request (1)`.
- Replies: `service.status_response (2) { state }`, `wake_up_response (4) { 0 woken, 1 already awake, 2 failed }`,
  `tilt_response (6) { roll, pitch }`, `event.subscribe_response (2)`.
- Alerts: `event.notification (3) { type (1) = 8, AlertDetails (1001) { state (1), metrics (2), error (3),
  tilt_calibration (4) } }`.

### Shot data

`Metrics { shot_id (1), shot_type (2: 0 practice, 1 normal), ball_metrics (3), club_metrics (4), swing_metrics (5) }`

| Field | R10 unit and sign | In `LaunchMonitorInput` | Measured? |
|---|---|---|---|
| `ball_speed` (3.3) | m/s | `BallSpeedMph` (× 2.2369) | Measured (radar) |
| `launch_angle` (3.1) | degrees, up | `LaunchAngleDegrees` | Measured |
| `launch_direction` (3.2) | degrees, + = right | `LaunchDirectionDegrees` | Measured |
| `total_spin` (3.5) | rpm | `TotalSpinRpm` | Depends on `spin_calculation_type` (3.6): 3 = measured (marked ball); 0 ratio, 1 ball flight, 2 other = **estimated** by the R10 |
| `spin_axis` (3.4) | degrees, **+ = tilted left** (ball curves left) | `SpinAxisDegrees`, **negated** (+ = curves right); null when there's no spin | As spin |
| backspin / sidespin | not sent | null (the adapter derives them) | — |
| `club_head_speed` (4.1) | m/s | `ClubSpeedMph` | Measured |
| `club_angle_face` (4.2) | degrees, + = right (open) | `FaceAngleDegrees` | Measured when tracked |
| `club_angle_path` (4.3) | degrees, + = right (in to out) | `ClubPathDegrees` | Measured when tracked |
| `attack_angle` (4.4) | degrees, + = up | `AttackAngleDegrees` | Measured when tracked |
| dynamic loft, impact location, smash | not sent | null | — |
| `swing_metrics` (5) | ms timestamps since boot | not used | — |

- The spin axis sign is settled by tenover's capture of a draw (face 3.9°, path 16.8°, wire tilt +22.2: a face
  closed to the path curves left) and agrees with gsp-r10-adapter negating it for GSPro.
- On a slow swing the R10 sends face, path and attack angle all exactly 0 (tenover). The app treats all three at 0
  as not tracked and leaves them null; one non-zero means a real (square) 0.
- Shots without ball speed or launch angle can't be flown and are skipped with a log line.
- Every shot is logged as the R10 sent it (its units and signs) for comparing with the Garmin Golf app, with a note
  when the spin was estimated.

### States, errors, battery

`State.StateType`: 0 Standby, 1 InterferenceTest, 2 **Waiting** (ready for a shot), 3 Recording, 4 Processing,
5 Error. The app's Ready light is green only in Waiting.

`Error { code: 1 overheating, 2 radar saturation, 3 platform tilted; severity: 0 warning, 1 serious, 2 fatal;
deviceTilt { roll, pitch } }`. Serious and fatal errors show on the Ready light and as a toast; warnings are logged.

Battery comes from the standard Battery Service; the R10 doesn't report a charging state there.

## Open questions

Only a real R10 can settle these. The [checklist for the first test](#first-hardware-test) covers them.

1. **Pairing from the app.** Does `DeviceInformationCustomPairing` with "confirm only" bond the R10 while it's in
   pairing mode (solid blue)? Does Windows then hold the link open itself, or reconnect it on its own and stop the
   R10 advertising?
2. **Channel pair.** gsp-r10-adapter uses `2812`/`2822`, tenover `2810`/`2820`. The app tries `2812`/`2822` first.
3. **Handshake.** Is gsp-r10-adapter's minimal handshake (ACK only, no host 5050) still enough on current firmware,
   or does it need tenover's host information and SwingSensor capability?
4. **Face and path for left-handed golfers.** Assumed to stay + = right (the radar's frame, not the golfer's).
5. **Tee distance.** Garmin says accuracy improves with the exact monitor-to-tee distance set. That's
   `ShotConfigRequest.tee_range`, but gsp-r10-adapter sends metres (from feet) and tenover documents yards, and the
   other fields' units (temperature, humidity as a fraction) disagree too. The app doesn't send it until a capture
   of the Garmin Golf app settles the units.
6. **Measurement service** (`3401`/`3403`): what its notifications carry. They're logged raw for working out.
7. **Practice vs normal shots** (`shot_type`): both are played; what makes the R10 call a shot "practice" isn't known.

## First hardware test

Run a debug build from source (or the release) with the R10 charged, and the phone's Bluetooth off.

1. **Pair**: R10 off, hold the button to solid blue. Open the drawer: does it list "Approach R10"? Does Connect pair
   it (any Windows prompt?) and reach "Armed - hit away"? If not, pair in Windows Settings and try again; note which
   worked.
2. **Connect**: the drawer shows model, serial, firmware, state Waiting and the battery; the Ready light is green.
3. **Shots**: at least 5 each with a wedge, a 7 iron and a driver, some straight, some drawn and faded, one deliberate
   slow chip (club not tracked). Note the Garmin Golf app's numbers for the same shots if possible (reconnect it
   between sessions: only one can be connected) and compare with the log lines `R10 shot …` and the shot screen:
   speed, launch, direction **side**, spin, spin axis **side**, club speed, path, face, attack.
4. **Left-handed**: a few shots with Left-handed golfer ticked: are path and face still sensible?
5. **Sleep**: leave it 10+ minutes without a shot: does it wake again by itself when it nods off?
6. **Reconnect**: turn the R10 off mid-session (or unplug the USB Bluetooth adapter), wait for the banner, turn it
   back on: does it reconnect by itself and re-arm?
7. **Battery**: does the level match the Garmin app's?
8. **Errors**: tilt it well off level: does "Monitor tilted" show?
9. **Send back**: `%APPDATA%\Square Range 3D\monitor-logs\r10-*.log` (raw traffic, no serial) and
   `%APPDATA%\Square Range 3D\logs\godot.log`, with notes on anything that differed.

## Turning a capture into fixtures

Each line of a `monitor-logs` file is one GFDI frame, decoded from COBS, with its direction: `<- 5043: 2200B313…`.
The hex is the whole frame (length, type, payload, CRC), so `Gfdi.ParseFrame(Convert.FromHexString(hex))` reads it
back and `Gfdi.ParseProtobuf` gives the protobuf for `R10Proto.Decode`. Add real shots to
`tests/GrimesGolf.Tests/GarminR10Tests.cs` alongside the tenover fixtures, with the Garmin Golf app's numbers as the
expected values. Frames that carry the device's identity (5024 device information, FIT and file transfers,
authentication, and their acknowledgements) are left out of the capture.

## Fallback: E6 Connect

If direct Bluetooth proves unworkable, the Garmin Golf app can send shots over the local network to a simulator that
pretends to be E6 Connect (a TCP server speaking JSON; gsp-r10-adapter's `src/connections/R10Connection.cs` and
r10-bridge's `e6_server.py` do this). That keeps a phone running the Garmin Golf app in the loop, which is what this
work set out to avoid, so it isn't built; it would feed the same `LaunchMonitorInput` pipeline.
