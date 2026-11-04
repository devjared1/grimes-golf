# GrimesGolf.Physics: golf physics engine

A deterministic, UI-independent golf ball physics engine (.NET 8 class library, no dependencies): club impact,
aerodynamic flight, wind and atmosphere, ground bounce and roll on arbitrary terrain, and putting. It implements the
specification in [`docs/physics-engine-spec.md`](../../docs/physics-engine-spec.md). Tests are in `tests/GrimesGolf.Physics.Tests`.

> **Validation status.** The engine has been compared against TrackMan PGA Tour *averages* only (12 clubs). Those
> are averages, not individual measured shots, and the default lift-curve shape was chosen after looking at them.
> **It has not been validated against independent real-world shot data** and must not be described as a validated
> realistic engine until it is (see [Calibration](#calibration-and-validation)).

## Quick start

```csharp
var engine = new GolfPhysicsEngine(diagnostics: Console.WriteLine);

// Mode A: measured ball launch (preferred).
FlightResults shot = engine.SimulateShot(new SimulationRequest
{
    Input = new LaunchMonitorInput { BallSpeedMph = 165, LaunchAngleDegrees = 12.5, LaunchDirectionDegrees = 0,
                                     BackspinRpm = 2600, SpinAxisDegrees = 0 },
    Environment = new EnvironmentConditions
    {
        Atmosphere = new AtmosphericConditions { TemperatureCelsius = 20, AltitudeMetres = 150, RelativeHumidityPercent = 50 },
        Wind = Wind.Uniform(10 * UnitConversion.MphToMetresPerSecond, fromDegrees: 270),
    },
    Terrain = new FlatTerrain(Surfaces.Fairway),
});
Console.WriteLine($"{shot.CarryYards:0} carry, {shot.TotalYards:0} total, apex {shot.ApexYards:0} yd");

// Mode B: launch calculated from club delivery.
var fromClub = engine.SimulateShot(new SimulationRequest
{
    InputMode = SimulationInputMode.ClubImpact, Club = ClubProperties.SevenIron,
    Input = new LaunchMonitorInput { ClubSpeedMph = 90, AttackAngleDegrees = -4.3, DynamicLoftDegrees = 23.1,
                                     ClubPathDegrees = 0, FaceAngleDegrees = 0 },
});

// Putting.
var green = new FlatTerrain(GreenModel.Surface(stimpFeet: 11));   // create surfaces once and reuse them
PuttingResults putt = engine.SimulatePutt(new PuttingRequest { Terrain = green, BallSpeed = 2.0, DirectionDegrees = 0 });
```

Invalid requests throw `SimulationValidationException` listing every problem. Warnings (assumed values,
inconsistent inputs) and diagnostics are returned in `FlightResults.Diagnostics` and sent to the sink.

## Conventions

| | |
|---|---|
| Units | SI inside (m, s, kg, rad, rad/s). Convert at the boundary with `UnitConversion`; `LaunchMonitorInput` takes mph/degrees/rpm. |
| World frame | X right, Y forward, Z up. Terrain and wind are in this frame. |
| Aim | `SimulationOptions.AimHeadingDegrees`: clockwise from +Y. Launch direction is relative to it. |
| Signs | Launch direction, club path, face angle: + = right. Spin axis and sidespin: + = ball curves right. Attack: + = up. Impact location: + = toward the toe / above face centre. |
| Spin vector | Backspin for a ball going +Y points along +X (lift = ω × v is upward). |
| Wind | Direction the wind blows *from*, clockwise from +Y: 0° headwind, 180° tailwind, 90° from the right. |
| Ball position | Ball centre. It rests one radius above the ground along the normal. |
| Precision | `double` throughout. Deterministic: same inputs give bit-identical results, including in batches. |

## Layout

| Folder | Contents |
|---|---|
| `Core` | `Vector3D`, `BallState`, RK4 `NumericalIntegrator`, constants and unit conversions |
| `Ball` | `BallProperties` (mass, size, inertia, aero model), `LaunchConditions` (with per-value provenance) |
| `Aerodynamics` | `IAerodynamicModel` + `TabulatedAerodynamicModel`, `AerodynamicForces`, wind fields |
| `Atmosphere` | `AtmosphericConditions` → `AirProperties` (density, viscosity) |
| `Flight` | `BallFlightSimulator`, `SimulationOptions`, `FlightResults`, `TrajectoryPoint` |
| `Ground` | `SurfaceProperties` + presets, `ITerrainProvider` (flat, plane, function, heightmap), `BounceModel`, `RollModel` |
| `Impact` | `ClubProperties` + presets, `ClubDelivery`, `ImpactModel` |
| `Putting` | `GreenModel` (Stimpmeter calibration), `PuttingSimulator` |
| `Calibration` | `ReferenceShots`, `CalibrationProfile`, `CalibrationService` |
| `Integration` | `LaunchMonitorInput` + `LaunchMonitorAdapter`, `SimulationRequest`, `GolfPhysicsEngine` |

Manufacturer-specific adapters (e.g. Square Golf → `LaunchMonitorInput`) belong outside this library.

## Game rules hook and use in the app

`SimulationOptions.Observer` / `PuttingRequest.Observer` (`IGroundObserver`) is told about every ground impact and
every ground step and can stop the ball there (`SimulationEnd.Stopped`). This lets callers apply rules the physics
doesn't know (water, out of bounds, bunkers, the cup) at the exact step they happen, without the engine knowing
about courses.

The app uses the engine through `GrimesGolf.Core`:

* `BallFlight` (range mode): shots on flat fairway, putts on a flat stimp-10 green.
* `CourseGround`: the hole's terrain and lies as an `ITerrainProvider`. Course (X forward, Z right, Y up) maps to
  engine (y, x, z), and headings are the same angle in both frames. Lies map to surfaces: tee/fairway → `Fairway`,
  fringe → `Fringe`, rough (first cut) → `Rough`, deep rough → `DeepRough`, bunker → `Sand`, green → stimp-10 green.
* `HoleRound`: flight, bounce and roll from the engine, with an observer applying the course rules. Full shots are
  first adjusted by `LieModel` for the ball's lie and stance slope (`HoleRound.StanceAt`; tees count as level).
  Putts and lip-outs run through `PuttingSimulator`, and the putt read rolls trial putts through it too, so the read
  matches the physics. Path points use the bottom of the ball as height (`CourseGround.ToFlightPoint`).
* `Caddie`: club suggestions use each club's carry from the current lie (`Caddie.CarryFrom`).

## Models

**Flight.** m dv/dt = mg + F_D + F_L, I dω/dt = τ, integrated with classical RK4 at a fixed step (default 1/240 s).
The step where the ball reaches the ground is bisected to find the contact time. Trajectory sampling
(`TrajectorySampleIntervalSeconds`) is independent of the physics step.

* v_rel = v − v_wind; Re = ρ v d / μ; S = r|ω⊥|/v (ω⊥ = spin perpendicular to the airflow).
* F_D = −½ρ C_D(Re, S) A v² v̂; F_L = ½ρ C_L(Re, S) A v² (ω⊥ × v̂)/|ω⊥ × v̂|. The full 3D spin vector sets the lift
  direction, so backspin, curvature and tilted axes are one mechanism. The spin axis stays fixed in space, as a
  gyroscope's does.
* Spin decay: τ = −½ρ C_M A v² r ω̂ with C_M = k·S.

**Atmosphere.** Station pressure from sea-level pressure and altitude (ISA barometric formula) unless given.
Moist-air density ρ = (p − e)/(R_d T) + e/(R_v T) with Buck (1981) saturation vapour pressure. Sutherland
viscosity. Altitude acts only through density.

**Wind.** `UniformWind`, `PowerLawWindProfile` (v ∝ h^α), `TabulatedWindProfile` (layers), and `GustingWind`
(deterministic seeded gusts; a simple stand-in, not a turbulence spectrum).

**Bounce** (after Penner 2002). The effective normal is tilted back by the crater angle
θ_c = θ_ref (v/18.6)(θ_in/44.4°). Normal restitution e(v_n) = a + b v_n + c v_n² (Penner's green values).
Tangential impulse: grip if |J_stick| ≤ μ_s J_n, otherwise slide with μ_d. When gripping, the contact leaves
slipping at −e_t times its incoming slip, where e_t is the turf's tangential restitution (0 = rolling contact). This
models ball and turf storing shear and returning it as backspin. Spin changes through the contact torque. Bounces
repeat until the outgoing normal speed is below `RollTransitionNormalSpeed`. There is no fixed bounce count.

Two turf effects shape the first, hard landing on clean lies (`ImpactSkidSpeed`, `ImpactDrag` in `SurfaceProperties`):
* **Skid.** Both frictions are scaled by 1/(1 + v_t/v_skid), with v_t the ball's speed along the (tilted) surface; its
  spin doesn't count. A fast landing skids through the impact, keeping much of its backspin, and the turf grips on
  the next, slower touch. Full shots hop forward 1-2 times, then check, spin back or release, depending on spin.
  Without it (v_skid = 0) a green grabs a full iron or wedge on its first touch and throws it straight back.
* **Turf drag.** The ball loses a share of its speed along the ground, pushed back through its centre so its spin is
  unchanged: the full share when it lands at least 12 m/s into the ground, proportionally less on gentler bounces.
  Low, spinning wedges skip, then stop, because they reach the next touch slow enough for their backspin to win.

Check and spin-back come mostly from the crater, helped by turf drag. A deeper crater (a softer, more receptive
green) turns the ball's forward speed into the normal impulse without costing spin. Friction alone can't do that: in
a rigid-ball impulse it removes contact-point spin 2.5× as fast as forward speed. So `GreenModel.Surface(stimp,
firmness: …)` decides whether wedges spin back (the default, 0.4) or release (firmer greens).

**Lie** (`LieModel`). A launch monitor reads the ball off a mat, so the measured launch is adjusted for the lie the
ball would have been struck from:
* Per-lie speed and spin factors and a launch change (rough: −5% speed, −30% spin, +1°; deep rough −20%/−50%/+3°;
  sand −12%/−30%/+1.5°; fringe −1%/−5%).
* Uphill/downhill: launch changes by 0.8 × the slope angle and spin by 1.5% per degree.
* Sidehill: a face of loft L soled on a lie tilted by φ points off line by δ = atan(tan L · sin φ). Ball above the
  feet turns it left for a right-hander; wedges are affected far more than drivers. The start line moves by 0.8 δ
  and the spin axis by −2 δ.

Adjusted values are marked `ValueSource.Adjusted`, and `LieAdjustment.Summary` describes the change. Putts aren't
adjusted.

**Roll.** Coulomb sliding with torque until the contact point stops slipping, then rolling:
a = (g_t + a_air)/(1 + k) − μ_r(v) g cosθ v̂ (+ grain). With μ_r = 0 this is the ideal 5/7 g sinθ (tested). The
ball rests when it's slow and the slope can't overcome rolling resistance. Air drag applies during the roll.

**Impact.** 3D impulse between a free rigid club head (mass, CG depth/height, MOIs) and the ball. Linear and
angular momentum are conserved. COR (with off-centre loss) on the normal, and Coulomb friction with tangential
restitution on the face. Gear effect, off-centre speed loss and face-to-path start/curve follow from the solution:
the start line is about 88 % face, 12 % path.

**Putting.** The same roll model, started on the ground (optionally skidding). The green's rolling resistance is
calibrated numerically so the engine's own roll of a Stimpmeter ball (1.83 m/s exit) covers the Stimp distance.
Variable green speed comes from the terrain's surface map.

## Provisional coefficients (calibrate before relying on them)

| Item | Where | Basis |
|---|---|---|
| C_D, C_L tables (Re × S) | `DefaultAerodynamics` | Smooth fits that follow published trends (Bearman & Harvey 1976; Smits & Smith 1994). Lift shape chosen against tour averages. |
| Spin-decay slope k = 0.012 | `DefaultAerodynamics` | Gives a few % spin loss per second (order commonly reported). Not fitted to measured decay. |
| Surface presets other than the green | `Surfaces` | Judgement scalings of Penner's green, giving the expected ordering only. |
| Green friction 0.5, tangential restitution 0.3, firmness 0.4 | `GreenModel` | Chosen so a tour PW spins back ~3 yd on a receptive green, a 7-iron stops and a low-spin chip releases. |
| Fairway tangential restitution 0.1; hard ground restitution ×1.25 | `Surfaces` | Judgement. |
| Skid 5 m/s (green, fringe), 7 m/s (fairway); turf drag 0.3 (green, fringe), full at 12 m/s | `GreenModel`, `Surfaces` | Judgement, against how full shots are seen to land: PW hops ~1-3 yd then checks, 7-iron hops and releases, high wedges pop up and spin back, low spinning wedges skip and stop. No turf drag on fairways (it cut drives' roll). |
| Lie responses and slope factors | `LieModel.Default` | Judgement, comparable to what golf simulators apply; not measured. |
| Firmness/moisture sensitivities | `SurfaceProperties` | Judgement. |
| Grain | `SurfaceProperties.GrainStrength` | Uncalibrated (default 0). |
| Stimpmeter exit speed 1.83 m/s | `GreenModel` | Approximation of the ramp physics. |
| Club presets (COR, friction, tangential restitution, effective mass) | `ClubProperties` | Tuned so tour-average delivery gives tour-average ball speed, launch and spin. The driver's negative e_t absorbs face bulge/roll, which isn't modelled. |

## Calibration and validation

`CalibrationService` compares simulations with `ReferenceShot`s, giving per-shot errors and RMS. It fits a
`CalibrationProfile` (drag/lift/spin-moment scales plus a Reynolds tilt of drag) by bounded Nelder-Mead with a prior
toward no adjustment, on a training set, and reports a separate validation set. Bounds and a published-range check
on the fitted coefficients stop it from returning unphysical values. Profiles round-trip through JSON and also carry
restitution and rolling-resistance scales.

Results against the TrackMan averages (alternating training/validation split):

| | carry RMS | apex RMS | landing RMS |
|---|---|---|---|
| Default model, training half | 7.6 yd | 2.7 yd | 2.7° |
| Default model, validation half | 1.9 yd | 2.7 yd | 2.7° |
| 4-parameter fit, validation half | 4.9 yd | 2.5 yd | 1.3° |

With so few shots the fit doesn't beat the default on the weighted validation error, so **the engine ships
uncalibrated** (`CalibrationProfile.None`). The largest single miss is the driver (about 18 yd short of 275). To
validate properly, collect individually measured shots (launch + carry + apex + descent + flight time, with
weather), calibrate on a training subset, and report on the rest. Do the same for bounce/roll (measured run-outs)
and greens (Stimp vs. measured putts).

`ReferenceShotTests.RegressionBaseline` pins carry, apex, landing, total and lateral for fixed shots. Update it
only for intended behaviour changes.

## Known limitations

* The ball stays in contact over crests during the roll (no ground-curvature lift-off). Spin about the surface
  normal is dropped once the ball is rolling.
* The shaft and the face's bulge and roll are not modelled in the impact.
* Air density is uniform over the shot (density at launch altitude).
* Heightmap normals come from central differences of the bilinear surface.
