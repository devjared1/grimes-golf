# Golf Physics Engine — Technical Specification & AI Implementation Instructions

## 1. Project Objective

Build a realistic, production-quality golf physics engine that can serve as the simulation core for a golf simulator application.

The engine must accurately simulate golf ball behavior from the moment of club impact through:

1. Club-to-ball collision.
2. Initial ball launch conditions.
3. Aerodynamic flight.
4. Spin, curvature, and wind influence.
5. Landing and ground collision.
6. Bounce and ground roll.
7. Slopes, terrain, and surface interaction.
8. Putting and short-game behavior.
9. Final resting position.

The objective is to produce physically believable and measurable golf trajectories, not arcade-style distance approximations.

The engine should be deterministic, configurable, testable, and designed for integration with different launch monitors and simulator frontends.

### Primary engineering principles

- Use real-world physical equations wherever practical.
- Use numerical integration for ball flight.
- Model drag and lift as functions of ball velocity and spin.
- Distinguish measured launch conditions from calculated launch conditions.
- Account for atmospheric conditions and wind.
- Model different ground surfaces independently.
- Make all important physical coefficients configurable.
- Support calibration against real-world golf shot measurements.
- Avoid hardcoded club-specific distance assumptions.
- Keep the physics engine independent of graphics and UI.

---

## 2. Recommended Architecture

Build the application as independent modules.

```text
GolfPhysicsEngine/
│
├── Core/
│   ├── PhysicsEngine.cs
│   ├── PhysicsConstants.cs
│   ├── Vector3D.cs
│   ├── Quaternion.cs
│   └── SimulationState.cs
│
├── Ball/
│   ├── GolfBall.cs
│   ├── BallState.cs
│   ├── BallProperties.cs
│   └── BallSpin.cs
│
├── Aerodynamics/
│   ├── AerodynamicModel.cs
│   ├── DragModel.cs
│   ├── LiftModel.cs
│   ├── SpinDecayModel.cs
│   └── WindModel.cs
│
├── Impact/
│   ├── ClubProperties.cs
│   ├── ImpactModel.cs
│   ├── LaunchConditions.cs
│   ├── SmashFactorModel.cs
│   └── SpinGenerationModel.cs
│
├── Flight/
│   ├── BallFlightSimulator.cs
│   ├── NumericalIntegrator.cs
│   ├── TrajectoryPoint.cs
│   └── FlightResults.cs
│
├── Ground/
│   ├── GroundCollisionModel.cs
│   ├── BounceModel.cs
│   ├── RollModel.cs
│   ├── SurfaceProperties.cs
│   └── TerrainModel.cs
│
├── Putting/
│   ├── PuttingSimulator.cs
│   └── GreenModel.cs
│
├── Environment/
│   ├── EnvironmentConditions.cs
│   ├── AtmosphericModel.cs
│   └── WeatherConditions.cs
│
├── Calibration/
│   ├── CalibrationProfile.cs
│   ├── CalibrationService.cs
│   └── SimulationComparison.cs
│
├── Integration/
│   ├── LaunchMonitorInput.cs
│   ├── LaunchMonitorAdapter.cs
│   └── SimulationRequest.cs
│
└── Tests/
    ├── PhysicsTests/
    ├── AerodynamicsTests/
    ├── ImpactTests/
    ├── GroundTests/
    └── ReferenceShotTests/
```

The structure is illustrative. Adapt it to the existing repository and technology stack if one exists.

**Recommended implementation:** C# / .NET 10 class library for the core engine, with a separate API or application integration layer. Keep the mathematical core portable so it can eventually be integrated into Unity, Unreal, a desktop application, or a standalone simulator.

---

## 3. Physical Constants and Golf Ball Properties

Use standard golf ball specifications as the default model.

| Property | Default |
|---|---:|
| Ball mass | 0.04593 kg |
| Ball diameter | 0.04267 m |
| Ball radius | 0.021335 m |
| Cross-sectional area | π × radius² |
| Gravity | 9.80665 m/s² |
| Standard air density | 1.225 kg/m³ |
| Standard temperature | 15°C |
| Standard atmospheric pressure | 101325 Pa |
| Moment of inertia | 2/5 × mass × radius² (solid-sphere approximation) |

Allow different ball models to override mass, diameter, moment of inertia, aerodynamic characteristics, and spin characteristics.

Do not assume every golf ball has identical aerodynamic behavior.

The default model should represent a regulation-sized, regulation-weight golf ball.

---

## 4. Club Impact Physics

The engine must support two different input modes.

### Mode A: Measured launch conditions

This is the preferred mode when a launch monitor provides actual ball measurements.

Inputs:

- Ball speed (mph or m/s).
- Horizontal launch angle.
- Vertical launch angle.
- Backspin (RPM).
- Sidespin or spin-axis angle.
- Optional total spin (RPM).
- Club speed.
- Club path.
- Face angle.
- Attack angle.
- Dynamic loft.
- Impact location.

When ball speed, launch direction, and spin are measured, use those values as the initial conditions for flight simulation.

Do not recalculate or overwrite measured launch conditions.

### Mode B: Calculated club impact

When only club measurements are available, calculate ball launch conditions using an impact model.

Inputs:

- Clubhead speed.
- Club mass.
- Effective clubhead mass.
- Club loft.
- Dynamic loft.
- Club path.
- Face angle.
- Angle of attack.
- Clubhead restitution.
- Impact location.
- Friction coefficient between clubface and ball.
- Ball properties.

Calculate:

1. Ball exit velocity.
2. Vertical launch angle.
3. Horizontal launch direction.
4. Ball speed.
5. Backspin.
6. Sidespin.
7. Spin-axis tilt.
8. Smash factor.

### Impact modeling requirements

Implement an impulse-based collision model.

Account for:

- Conservation of linear momentum.
- Conservation of angular momentum.
- Coefficient of restitution.
- Tangential friction.
- Clubface orientation.
- Spin loft.
- Face-to-path relationship.
- Gear effect.
- Center, heel, and toe impacts.
- Vertical impact location where supported.

Do not rely exclusively on simplified formulas such as ball speed = club speed × fixed smash factor.

Use club-specific properties and calibration profiles.

The impact model should be independently testable and must not be required when actual launch-monitor ball measurements are available.

---

## 5. Ball Flight Physics

This is the most important component of the engine.

The simulator must numerically calculate the ball's position and velocity throughout flight.

### Forces acting on the golf ball

Three primary forces must be modeled:

1. Gravity.
2. Aerodynamic drag.
3. Aerodynamic lift generated by spin.

The governing equation is:

\[
m\frac{d\vec{v}}{dt}
=
\vec{F}_{gravity}
+
\vec{F}_{drag}
+
\vec{F}_{lift}
\]

### Gravity

\[
\vec{F}_{gravity} =
\begin{bmatrix}
0\\
0\\
-mg
\end{bmatrix}
\]

Use a consistent coordinate system throughout the engine.

Recommended:
- X = lateral direction.
- Y = forward direction.
- Z = vertical direction.

### Aerodynamic drag

Calculate drag using:

\[
F_D = \frac{1}{2}\rho C_D A v_{rel}^2
\]

Where:

- ρ = air density.
- CD = drag coefficient.
- A = ball cross-sectional area.
- vrel = ball velocity relative to air.

Drag force must oppose relative airflow.

Important: Do not use one fixed drag coefficient for all conditions.

Implement a configurable aerodynamic coefficient model that accounts for:

- Reynolds number.
- Spin parameter.
- Ball speed.
- Ball construction and aerodynamic profile.

The coefficients should be sourced from published golf-ball aerodynamic research or calibrated against measured flight data.

Use interpolation tables where appropriate rather than assuming a universal analytical coefficient formula.

### Aerodynamic lift / Magnus effect

Golf ball spin creates aerodynamic lift perpendicular to the relative airflow and spin axis.

Calculate lift using:

\[
F_L = \frac{1}{2}\rho C_L A v_{rel}^2
\]

The direction of lift must be calculated using the cross product of the spin vector and relative airflow direction, with a consistent sign convention.

The lift coefficient should depend on the spin parameter:

\[
S = \frac{r\omega}{v_{rel}}
\]

Where:

- r = ball radius.
- ω = angular velocity in radians per second.
- vrel = relative airspeed.

Support both backspin and tilted spin axes.

### Spin and curvature

The engine must reproduce:

- Backspin-induced lift.
- Draw curvature.
- Fade curvature.
- Hook and slice behavior.
- Spin-axis tilt.
- Spin decay throughout flight.

Use the actual three-dimensional spin vector rather than treating backspin and sidespin as completely independent forces.

The spin-axis orientation should influence both lift direction and trajectory curvature.

### Spin decay

Spin should decrease over time based on aerodynamic torque and ball properties.

Do not simply apply an arbitrary percentage reduction per second without a physical model or calibration.

Support separate rotational decay parameters if empirical measurements indicate different behavior under different flight conditions.

### Wind

Wind must be modeled as an environmental velocity vector.

Calculate aerodynamic forces from:

\[
\vec{v}_{rel} = \vec{v}_{ball} - \vec{v}_{wind}
\]

Support:

- Headwind.
- Tailwind.
- Crosswind.
- Quartering wind.
- Wind at varying heights.
- Optional gusts and turbulence.

Wind direction must be represented in world coordinates.

Allow a wind profile to define wind speed and direction at different altitudes.

### Atmospheric conditions

Calculate air density using environmental conditions.

Inputs:

- Temperature.
- Atmospheric pressure.
- Relative humidity.
- Altitude.

Humidity and altitude should influence air density.

Do not use altitude as an arbitrary distance multiplier. Its effect should come through atmospheric properties.

---

## 6. Numerical Integration

Use a numerical integrator to calculate flight progression.

Recommended initial implementation:

**Fourth-order Runge-Kutta (RK4).**

Use a fixed simulation timestep of approximately:

\[
\Delta t = \frac{1}{240}\text{ seconds}
\]

Make the timestep configurable.

The integrator should calculate:

- Position.
- Linear velocity.
- Angular velocity.
- Aerodynamic forces.
- Acceleration.

At each integration step:

1. Calculate relative air velocity.
2. Calculate Reynolds number and spin parameter.
3. Determine drag and lift coefficients.
4. Calculate aerodynamic forces.
5. Calculate gravity.
6. Calculate total acceleration.
7. Update velocity.
8. Update position.
9. Update spin.
10. Record trajectory data.

Use sufficient numerical precision to avoid accumulated error.

The simulation must terminate flight when the ball contacts the terrain surface.

Implement adaptive timestep support as a future optimization, not as a requirement for the first release.

---

## 7. Ball Flight Results

The engine must return a complete trajectory, not just final carry distance.

Each trajectory point should include:

```json
{
  "time": 0.0,
  "position": {
    "x": 0.0,
    "y": 0.0,
    "z": 0.0
  },
  "velocity": {
    "x": 0.0,
    "y": 0.0,
    "z": 0.0
  },
  "speed": 0.0,
  "spinRate": 0.0,
  "spinAxis": {
    "x": 0.0,
    "y": 0.0,
    "z": 0.0
  }
}
```

Final shot results must include:

| Metric | Description |
|---|---|
| Carry distance | Horizontal distance to first landing |
| Total distance | Carry plus bounce and roll |
| Apex height | Maximum height above launch surface |
| Time of flight | Time from launch to first landing |
| Ball speed | Initial ball speed |
| Launch angle | Vertical launch angle |
| Launch direction | Horizontal launch direction |
| Backspin | Initial backspin |
| Total spin | Initial spin magnitude |
| Spin axis | Initial spin-axis orientation |
| Landing angle | Ball descent angle at landing |
| Landing speed | Speed immediately before landing |
| Landing position | Three-dimensional landing coordinates |
| Roll distance | Distance traveled after first landing |
| Final position | Final resting coordinates |

Additional outputs:

- Maximum lateral deviation.
- Maximum height.
- Trajectory samples.
- Flight curvature.
- Bounce count.
- Ground-contact events.
- Total elapsed time.

---

## 8. Ground Collision and Bounce Physics

Landing must transition the simulation from airborne flight to ground interaction.

The collision system must account for:

- Incoming velocity.
- Incoming spin.
- Ground normal.
- Surface restitution.
- Tangential friction.
- Surface firmness.
- Surface moisture.
- Ball-ground interaction.

### Collision response

Calculate normal velocity and tangential velocity relative to the ground surface.

Use a coefficient of restitution to determine the outgoing normal velocity.

Use tangential friction and rotational interaction to calculate post-impact velocity and spin.

The ball should not simply stop or continue rolling at an arbitrary percentage of landing speed.

### Surface types

Implement configurable surface profiles.

| Surface | Expected behavior |
|---|---|
| Fairway | Moderate bounce and roll |
| Green | Shorter roll and relatively controlled bounce |
| Rough | Increased energy loss and reduced roll |
| Deep rough | Significant energy loss |
| Sand | High energy dissipation |
| Fringe | Intermediate behavior |
| Hard ground | Higher restitution |
| Soft ground | Lower restitution |
| Wet ground | Surface-dependent friction and energy loss |

Each surface should define:

- Restitution coefficient.
- Static friction.
- Dynamic friction.
- Rolling resistance.
- Firmness.
- Moisture.
- Surface roughness.

These properties must be configurable and calibrated.

### Multiple bounces

Support multiple collisions until the ball transitions into rolling or comes to rest.

Do not use a fixed bounce count.

---

## 9. Ground Roll Physics

After the ball lands, calculate its rolling behavior.

Rolling physics must account for:

- Initial landing velocity.
- Ground friction.
- Rolling resistance.
- Ball rotation.
- Terrain slope.
- Surface type.
- Gravity.

Calculate the acceleration of the ball along the ground surface.

For an idealized rolling sphere:

\[
a = \frac{5}{7}g\sin(\theta)
\]

This is an idealized relationship for a solid sphere rolling without slipping on an incline. It is a reference case, not a universal golf-ball roll model.

For actual golf-ball behavior, use rolling resistance and slip dynamics appropriate to the selected surface.

The model must support:

- Flat fairways.
- Uphill lies.
- Downhill lies.
- Side slopes.
- Uneven terrain.
- Variable terrain elevation.

The ball must naturally decelerate and stop when its kinetic energy is exhausted or when terrain conditions prevent further movement.

---

## 10. Terrain and Elevation

Build a terrain interface that allows the engine to query:

- Ground elevation.
- Surface type.
- Ground normal.
- Slope.
- Friction properties.

Support an initial heightmap-based terrain implementation.

Terrain should be independent of the physics calculations.

The engine should support arbitrary terrain implementations through an interface.

Example:

```csharp
public interface ITerrainProvider
{
    double GetElevation(double x, double y);

    Vector3D GetSurfaceNormal(double x, double y);

    SurfaceProperties GetSurfaceProperties(double x, double y);
}
```

The ball must interact with the actual terrain rather than assuming a flat ground plane.

---

## 11. Putting Physics

Implement putting as a separate simulation mode that uses the same underlying ball and terrain physics.

Inputs:

- Initial putt speed.
- Initial direction.
- Ball position.
- Green slope.
- Green speed / Stimpmeter value.
- Green friction.
- Terrain elevation.
- Optional grain direction.

Calculate:

- Initial acceleration and deceleration.
- Uphill and downhill behavior.
- Break from left to right.
- Break from right to left.
- Combined slope effects.
- Final resting location.
- Distance traveled.
- Time to stop.

### Green speed

The Stimpmeter value should influence calibrated green rolling resistance.

Do not simply multiply putting distance by the Stimpmeter number.

Develop a calibration relationship between Stimpmeter measurements and the rolling-resistance model.

Support variable green speeds across different areas of the putting surface.

The putting simulator must also handle:

- Flat putts.
- Uphill putts.
- Downhill putts.
- Sidehill putts.
- Double-breaking putts.
- Short putts.
- Long putts.

---

## 12. Environmental Simulation

Provide an environment configuration object.

Example:

```json
{
  "temperatureCelsius": 20,
  "pressureHpa": 1013.25,
  "relativeHumidity": 50,
  "altitudeMeters": 150,
  "wind": {
    "speedMph": 10,
    "directionDegrees": 270,
    "gustSpeedMph": 14
  }
}
```

Support the following conditions:

- Standard atmospheric conditions.
- High altitude.
- Cold temperatures.
- Hot temperatures.
- High humidity.
- Low humidity.
- Headwind.
- Tailwind.
- Crosswind.

The environment must be passed into the simulation rather than accessed through global variables.

---

## 13. Launch Monitor Integration

Build a standardized launch-monitor input interface.

The physics engine should not depend on a particular launch-monitor manufacturer.

Example:

```csharp
public class LaunchMonitorInput
{
    public double? BallSpeedMph { get; set; }

    public double? ClubSpeedMph { get; set; }

    public double? LaunchAngleDegrees { get; set; }

    public double? LaunchDirectionDegrees { get; set; }

    public double? BackspinRpm { get; set; }

    public double? SidespinRpm { get; set; }

    public double? TotalSpinRpm { get; set; }

    public double? SpinAxisDegrees { get; set; }

    public double? ClubPathDegrees { get; set; }

    public double? FaceAngleDegrees { get; set; }

    public double? AttackAngleDegrees { get; set; }

    public double? DynamicLoftDegrees { get; set; }
}
```

Use nullable properties because not all monitors provide every measurement.

Implement a validation layer that determines whether enough information exists to run the requested simulation mode.

Important requirements:

- Preserve original launch-monitor measurements.
- Record measurement provenance.
- Distinguish measured, derived, and estimated values.
- Normalize units before simulation.
- Reject invalid or physically impossible inputs.
- Never silently replace measured values with estimated values.

Create manufacturer-specific adapters as separate modules.

The core engine should work with any device that can supply normalized launch data.

---

## 14. Physics Calibration System

A realistic engine requires calibration against measured data.

Build calibration into the original architecture rather than treating it as an afterthought.

Support calibration profiles for:

- Ball model.
- Club model.
- Aerodynamic characteristics.
- Drag coefficient.
- Lift coefficient.
- Spin decay.
- Bounce.
- Rolling resistance.
- Putting behavior.

### Reference measurements

Use real-world shot data from reliable sources.

Compare simulated and measured:

- Ball speed.
- Launch angle.
- Spin rate.
- Carry distance.
- Apex height.
- Flight time.
- Landing angle.
- Total distance.

### Calibration workflow

1. Load reference shot data.
2. Run the simulation using the same initial conditions.
3. Compare simulated and measured results.
4. Calculate error metrics.
5. Identify the parameters responsible for the discrepancy.
6. Adjust calibration parameters.
7. Rerun the simulation.
8. Save the calibration profile.
9. Validate against independent reference shots.

Implement an optimization routine for parameter calibration, but do not allow calibration to produce physically unreasonable coefficients simply to minimize distance error.

Use separate training and validation datasets.

---

## 15. Performance Requirements

The engine should be capable of running real-time simulation without unnecessary computational overhead.

Requirements:

- Deterministic results for identical inputs.
- No dependence on frame rate.
- No unnecessary memory allocations inside the simulation loop.
- Efficient vector calculations.
- Configurable simulation precision.
- Support multiple concurrent simulations.
- Support batch simulation for testing and calibration.
- Thread-safe immutable configuration profiles.

Store trajectory data efficiently.

Allow the application to choose the trajectory sampling frequency independently of the internal physics timestep.

The physics timestep may be 240 Hz while the frontend receives fewer trajectory points.

---

## 16. API Design

Expose a simple simulation interface.

Example:

```csharp
public interface IGolfPhysicsEngine
{
    FlightResults SimulateShot(
        SimulationRequest request);

    PuttingResults SimulatePutt(
        PuttingRequest request);

    IReadOnlyList<FlightResults> SimulateBatch(
        IEnumerable<SimulationRequest> requests);
}
```

Example simulation request:

```json
{
  "inputMode": "MeasuredLaunch",
  "ball": {
    "model": "StandardGolfBall"
  },
  "launch": {
    "ballSpeedMph": 165,
    "launchAngleDegrees": 12.5,
    "launchDirectionDegrees": 0,
    "backspinRpm": 2600,
    "spinAxisDegrees": 0
  },
  "environment": {
    "temperatureCelsius": 20,
    "altitudeMeters": 150,
    "relativeHumidity": 50,
    "windSpeedMph": 0,
    "windDirectionDegrees": 0
  },
  "simulation": {
    "timeStepSeconds": 0.0041666667,
    "maximumFlightTimeSeconds": 20,
    "enableBounce": true,
    "enableRoll": true
  }
}
```

The values above are example inputs, not a prescribed performance benchmark.

---

## 17. Testing and Validation

Create a comprehensive automated test suite.

### Mathematical tests

- Vector operations.
- Cross products.
- Dot products.
- Coordinate transformations.
- Unit conversions.
- Angular velocity conversions.
- Numerical integration accuracy.

### Physics tests

- Ball launched vertically.
- Ball launched horizontally.
- Ball with zero spin.
- Ball with backspin.
- Ball with tilted spin axis.
- Headwind.
- Tailwind.
- Crosswind.
- High altitude.
- Cold air.
- Hot air.
- Flat landing.
- Uphill landing.
- Downhill landing.
- Hard surface.
- Soft surface.
- Fairway roll.
- Green roll.

### Expected behaviors

| Test | Expected behavior |
|---|---|
| Zero wind | Symmetrical conditions produce no wind-induced lateral deviation |
| Backspin | Produces aerodynamic lift |
| Increased backspin | Changes lift according to calibrated aerodynamic model |
| Tilted spin axis | Produces lateral curvature |
| Headwind | Changes relative airflow and flight behavior |
| Tailwind | Changes relative airflow and flight behavior |
| Crosswind | Changes trajectory through aerodynamic forces |
| Higher altitude | Reduced air density under otherwise comparable conditions |
| Soft landing surface | Generally reduces bounce and roll |
| Uphill terrain | Reduces forward roll |
| Downhill terrain | Increases forward roll |
| Increased rolling resistance | Reduces roll distance |
| Flat green | Produces predictable putting behavior |

### Regression testing

Create a fixed dataset of reference golf shots.

Every change to the physics engine must be tested against this dataset.

Track changes in:

- Carry.
- Total distance.
- Apex.
- Flight time.
- Landing angle.
- Lateral deviation.

Flag unexpected regression.

---

## 18. Implementation Phases

Build the engine incrementally. Do not attempt to implement every subsystem simultaneously.

### Phase 1 — Core physics and ball flight

Implement:

- Physics constants.
- Vector mathematics.
- Ball properties.
- Launch conditions.
- Gravity.
- Drag.
- Lift.
- Spin axis.
- Spin decay.
- RK4 numerical integration.
- Trajectory generation.
- Flight results.

Deliverable: A working ball-flight engine that accepts measured launch conditions and returns a complete trajectory.

### Phase 2 — Environmental physics

Implement:

- Wind vectors.
- Atmospheric density.
- Temperature.
- Pressure.
- Humidity.
- Altitude.
- Variable wind profiles.

Deliverable: Environmental conditions influence trajectories through actual aerodynamic calculations.

### Phase 3 — Ground interaction

Implement:

- Ground collision.
- Bounce.
- Friction.
- Rolling resistance.
- Surface properties.
- Terrain elevation.
- Final resting position.

Deliverable: Complete simulation from impact through final resting position.

### Phase 4 — Club impact model

Implement:

- Club properties.
- Impulse-based collision.
- Smash factor.
- Launch conditions.
- Spin generation.
- Face-to-path effects.
- Gear effect.

Deliverable: Ability to calculate ball launch conditions from club measurements.

### Phase 5 — Putting

Implement:

- Green surface.
- Slope.
- Rolling resistance.
- Stimpmeter calibration.
- Putt trajectory.
- Break.
- Final resting position.

Deliverable: Realistic putting simulation.

### Phase 6 — Calibration and optimization

Implement:

- Reference datasets.
- Calibration profiles.
- Automated comparison.
- Parameter optimization.
- Regression testing.
- Performance optimization.

Deliverable: A calibrated physics engine with measurable accuracy.

---

## 19. Development Standards

Follow these implementation rules throughout the project.

1. Use SI units internally. Convert mph, feet, yards, inches, and RPM at system boundaries.
2. Use double precision for physics calculations.
3. Keep physics independent of rendering and UI.
4. Use dependency injection where appropriate.
5. Make physics parameters configurable.
6. Avoid magic numbers.
7. Document equations and their assumptions.
8. Include unit tests for every physics subsystem.
9. Use interfaces for external dependencies.
10. Avoid unnecessary abstractions that complicate numerical calculations.
11. Maintain deterministic simulation behavior.
12. Provide logging and diagnostic output for failed simulations.
13. Include validation for every public simulation request.
14. Never silently fall back to arbitrary approximations.
15. Clearly identify any empirical model that requires calibration.

---

## 20. Definition of Done

The initial production-ready engine must:

- Accept measured launch-monitor data.
- Simulate a complete three-dimensional ball trajectory.
- Calculate drag and lift dynamically.
- Model backspin and spin-axis tilt.
- Support wind and atmospheric conditions.
- Detect terrain collision.
- Calculate bounce and roll.
- Support configurable surface properties.
- Return carry and total distance.
- Return a complete trajectory.
- Produce deterministic results.
- Pass automated physics tests.
- Provide documented APIs.
- Include calibration support.
- Run independently of any graphical application.

The club-impact model and putting subsystem should be completed in their designated implementation phases.

**Critical acceptance criterion:** The engine must be validated against independent real-world golf shot data. A simulation that produces plausible-looking trajectories but has not been measured against actual shots must not be represented as a validated realistic golf physics engine.

---

## 21. Instructions to the AI Development Agent

Before writing code:

1. Inspect the existing repository and identify its programming language, architecture, conventions, and testing framework.
2. Determine whether this is a new project or an addition to an existing simulator.
3. Identify existing launch-monitor integrations.
4. Produce a short implementation plan.
5. Identify any missing physical reference data or empirical coefficients.
6. Establish unit conventions and coordinate-system conventions.

Then implement Phase 1 completely.

Do not generate the entire project as one large code dump.

For each phase:

- Implement working code.
- Build the project.
- Run automated tests.
- Fix compilation errors.
- Validate numerical behavior.
- Document completed functionality.
- Report any assumptions or approximations.
- Identify the next phase.

Do not mark a phase complete until its tests pass.

When a physical coefficient is unknown, use an explicitly configurable provisional model and document the limitation. Do not invent scientific accuracy.

The final objective is a physically grounded, extensible golf simulation engine that can serve as the foundation of a commercial-quality golf simulator application.