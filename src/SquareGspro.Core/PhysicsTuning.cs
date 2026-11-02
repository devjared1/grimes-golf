using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SquareGspro.Core.Course;
using SquareGspro.Physics;

namespace SquareGspro.Core;

/// <summary>
/// One adjustable physics value: where it's shown (<see cref="Group"/>, then <see cref="Section"/>), its range and
/// step, and its default (the engine's built-in value, so default tuning plays exactly like the untuned engine).
/// </summary>
public sealed record TuningParameter(
    string Key, string Group, string Section, string Label, string Unit, double Default, double Min, double Max, double Step,
    string Help = "")
{
    /// <summary>Decimal places implied by <see cref="Step"/>.</summary>
    public int Decimals => Step >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(Step) - 1e-9);

    public string Format(double value) => value.ToString("F" + Decimals, CultureInfo.InvariantCulture);
    public double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, Min, Max) : Default;
}

/// <summary>
/// A full set of physics tuning values (immutable). Every value is one of <see cref="Parameters"/>; missing values are
/// the defaults. Saved as JSON: <c>{ "name": "...", "values": { "flight.drag": 1.0, ... } }</c>.
/// <para>
/// All defaults are the engine's own provisional, uncalibrated values; see the physics README.
/// </para>
/// </summary>
public sealed class PhysicsTuning
{
    public const string Flight = "Flight", Ground = "Bounce & roll", Lies = "Lies";

    /// <summary>Course surfaces with their own bounce and roll settings, in display order (key prefix, title, lie).</summary>
    public static readonly (string Key, string Title, Lie Lie)[] SurfaceKinds =
    {
        ("fairway", "Fairway", Lie.Fairway), ("fringe", "Fringe", Lie.Fringe), ("rough", "Rough", Lie.Rough),
        ("deepRough", "Deep rough", Lie.DeepRough), ("sand", "Sand", Lie.Bunker), ("green", "Green", Lie.Green),
    };

    /// <summary>Lies with a strike penalty (key prefix, title, engine lie).</summary>
    public static readonly (string Key, string Title, LieKind Kind)[] LieKinds =
    {
        ("fringe", "Fringe", LieKind.Fringe), ("rough", "Rough", LieKind.Rough), ("deepRough", "Deep rough", LieKind.DeepRough),
        ("sand", "Sand", LieKind.Sand),
    };

    public static IReadOnlyList<TuningParameter> Parameters { get; } = BuildParameters();
    private static readonly Dictionary<string, TuningParameter> ByKey = Parameters.ToDictionary(p => p.Key);

    private readonly Dictionary<string, double> _values;

    private PhysicsTuning(string name, Dictionary<string, double> values)
    {
        Name = name;
        _values = values;
    }

    public static PhysicsTuning Defaults { get; } = new("Defaults", Parameters.ToDictionary(p => p.Key, p => p.Default));

    public string Name { get; }

    public static TuningParameter Parameter(string key) => ByKey[key];

    public double this[string key] => _values[key];

    public PhysicsTuning With(string key, double value)
    {
        var values = new Dictionary<string, double>(_values) { [key] = ByKey[key].Clamp(value) };
        return new PhysicsTuning(Name, values);
    }

    public PhysicsTuning Named(string name) => new(name, _values);

    /// <summary>These values with every parameter of <paramref name="group"/> (all groups if null) back at its default.</summary>
    public PhysicsTuning Reset(string? group = null, string? section = null)
    {
        var values = new Dictionary<string, double>(_values);
        foreach (var p in Parameters)
            if ((group == null || p.Group == group) && (section == null || p.Section == section)) values[p.Key] = p.Default;
        return new PhysicsTuning(Name, values);
    }

    public bool IsDefault(string key) => Math.Abs(_values[key] - ByKey[key].Default) < ByKey[key].Step / 2;
    public int ChangedCount => Parameters.Count(p => !IsDefault(p.Key));

    /// <summary>Values that differ between <paramref name="before"/> and this.</summary>
    public IEnumerable<(TuningParameter Parameter, double Before, double After)> ChangesFrom(PhysicsTuning before) =>
        Parameters.Where(p => Math.Abs(before[p.Key] - this[p.Key]) >= p.Step / 2).Select(p => (p, before[p.Key], this[p.Key]));

    public string ToJson()
    {
        var values = new JsonObject();
        foreach (var p in Parameters) values[p.Key] = Math.Round(this[p.Key], p.Decimals + 2);
        var root = new JsonObject { ["name"] = Name, ["values"] = values };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads saved tuning. Unknown keys are ignored, missing ones get their defaults, and values are clamped to range.</summary>
    public static PhysicsTuning FromJson(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Not a tuning file.");
        var values = Parameters.ToDictionary(p => p.Key, p => p.Default);
        if (root["values"] is JsonObject saved)
            foreach (var (key, node) in saved)
                if (ByKey.TryGetValue(key, out var p) && node is JsonValue v && v.TryGetValue<double>(out var d)) values[key] = p.Clamp(d);
        string name = root["name"] is JsonValue n && n.TryGetValue<string>(out var s) && s.Length > 0 ? s : "Tuning";
        return new PhysicsTuning(name, values);
    }

    private static List<TuningParameter> BuildParameters()
    {
        var list = new List<TuningParameter>();
        void Add(string key, string group, string section, string label, string unit, double def, double min, double max, double step, string help = "") =>
            list.Add(new TuningParameter(key, group, section, label, unit, def, min, max, step, help));

        Add("flight.drag", Flight, "Aerodynamics", "Drag", "×", 1, CalibrationProfile.MinimumAeroScale, CalibrationProfile.MaximumAeroScale, 0.01,
            "Scales the drag coefficient. More drag: lower, shorter flights, steeper landings.");
        Add("flight.lift", Flight, "Aerodynamics", "Lift", "×", 1, CalibrationProfile.MinimumAeroScale, CalibrationProfile.MaximumAeroScale, 0.01,
            "Scales the lift from backspin (Magnus). More lift: higher apex, longer carry for spinny shots, more curve.");
        Add("flight.spinDecay", Flight, "Aerodynamics", "Spin decay", "×", 1, CalibrationProfile.MinimumSpinMomentScale, CalibrationProfile.MaximumSpinMomentScale, 0.05,
            "How fast the air slows the ball's spin in flight. Faster decay: less lift late in the flight, flatter descent.");
        Add("flight.dragSpeedTilt", Flight, "Aerodynamics", "Drag tilt with speed", "", 0, -CalibrationProfile.MaximumDragReynoldsSlope, CalibrationProfile.MaximumDragReynoldsSlope, 0.01,
            "Positive: drag grows for fast balls (drivers) and shrinks for slow ones (wedges). Pivots on a mid-iron.");

        Add("air.temperature", Flight, "Air", "Temperature", "°F", 59, 20, 110, 1, "Warmer air is thinner: the ball flies further.");
        Add("air.altitude", Flight, "Air", "Altitude", "ft", 0, -1000, 12000, 50, "Higher courses have thinner air: about 2% more carry per 1000 ft.");
        Add("air.humidity", Flight, "Air", "Humidity", "%", 0, 0, 100, 1, "Humid air is slightly lighter (a small effect).");

        Add("wind.speed", Flight, "Wind", "Wind speed", "mph", 0, 0, 40, 0.5, "Measured 10 m (33 ft) up, like a weather report.");
        Add("wind.from", Flight, "Wind", "Wind from", "°", 0, 0, 355, 5,
            "Direction the wind blows from. On the range 0° is straight into your face and 90° from the right; on a course 0° is from the north.");
        Add("wind.gusts", Flight, "Wind", "Gusts", "+mph", 0, 0, 20, 0.5, "Gusts on top of the wind speed, varying over a few seconds.");
        Add("wind.shear", Flight, "Wind", "Height profile", "", 0.143, 0, 0.4, 0.005,
            "How much weaker the wind is near the ground (power-law exponent). 0 = the same at every height; 0.14 is open short grass.");

        foreach (var (key, title, lie) in SurfaceKinds)
        {
            var s = CourseGround.DefaultSurfaceFor(lie);
            string k = "ground." + key;
            Add(k + ".bounce", Ground, title, "Bounciness", "×", s.RestitutionScale, 0, 2, 0.01,
                "Scales how much upward speed the ball keeps on each bounce.");
            Add(k + ".crater", Ground, title, "Dig-in (crater)", "°", s.CraterAngleDegrees, 0, 45, 0.5,
                "How far a landing ball pushes into the turf, tilting the bounce back toward the golfer. More: the ball checks and spins back.");
            Add(k + ".firmness", Ground, title, "Firmness", "", s.Firmness, 0, 1, 0.01,
                "0 soft … 1 firm (0.5 nominal). Firmer: livelier bounces, shallower craters, more turf grab.");
            Add(k + ".friction", Ground, title, "Friction", "", s.DynamicFriction, 0, 1.5, 0.01,
                "Sliding friction between ball and turf: how hard the ground grabs a skidding or spinning ball.");
            Add(k + ".grab", Ground, title, "Spin grab", "", s.TangentialRestitution, 0, 0.9, 0.01,
                "Turf returning the ball's spin on impact (tangential restitution). With backspin this pulls the ball back.");
            Add(k + ".skid", Ground, title, "Skid on landing", "m/s", s.ImpactSkidSpeed, 0, 40, 0.5,
                "How much a fast, spinning ball skids through a hard landing instead of being grabbed at once (the slip speed at which "
                + "bounce friction halves; 0 = no skid). Lower: hops forward first and checks on a later bounce; 0: grabs on the first touch.");
            Add(k + ".drag", Ground, title, "Turf drag on landing", "", s.ImpactDrag, 0, 1, 0.01,
                "How much pace a landing ball loses ploughing through the turf, without losing spin (a fraction of how hard it lands). "
                + "More: shorter hops; low, spinning wedges stop sooner.");
            if (lie == Lie.Green)
                Add("ground.green.stimp", Ground, title, "Green speed (stimp)", "ft", 10, 6, 15, 0.5, "Stimpmeter reading. Higher is faster.");
            else
                Add(k + ".rolling", Ground, title, "Rolling resistance", "g", s.RollingResistance, 0.02, 2, 0.005,
                    "How fast a rolling ball slows, as a fraction of gravity. Lower: longer roll-out.");
        }
        Add("ground.wetness", Ground, "Conditions", "Wetness", "", 0, 0, 1, 0.05,
            "0 dry … 1 saturated, everywhere. Wet ground: deader bounces, deeper craters, more rolling resistance, less grab.");

        foreach (var (key, title, kind) in LieKinds)
        {
            var r = LieModel.Default.ResponseFor(kind);
            string k = "lie." + key;
            Add(k + ".speed", Lies, title, "Ball speed", "%", r.BallSpeedFactor * 100, 40, 110, 0.5, "Ball speed kept from this lie (100 = as measured).");
            Add(k + ".spin", Lies, title, "Spin", "%", r.SpinFactor * 100, 10, 150, 1, "Spin kept from this lie. Grass between club and ball cuts spin (a flier).");
            Add(k + ".launch", Lies, title, "Launch change", "°", r.LaunchChangeDegrees, -5, 10, 0.1, "Change to the launch angle from this lie.");
        }
        var m = LieModel.Default;
        Add("lie.slope.launch", Lies, "Slopes", "Launch per degree of slope", "°/°", m.SlopeLaunchFactor, 0, 1.5, 0.05,
            "Uphill lies add loft, downhill lies take it off: launch change per degree of slope along the aim.");
        Add("lie.slope.spin", Lies, "Slopes", "Spin per degree uphill", "%/°", m.SlopeSpinPerDegree * 100, 0, 5, 0.1,
            "Spin added per degree uphill (removed downhill).");
        Add("lie.sidehill.start", Lies, "Slopes", "Sidehill start line", "×", m.SidehillStartFactor, 0, 1.5, 0.05,
            "How much of the face turn on a sidehill lie the start line follows (ball above the feet starts left for a right-hander).");
        Add("lie.sidehill.curve", Lies, "Slopes", "Sidehill curve", "×", m.SidehillAxisFactor, 0, 4, 0.1,
            "Spin-axis tilt per degree of face turn on a sidehill lie (ball above the feet draws).");
        Add("lie.slope.max", Lies, "Slopes", "Steepest stance", "°", m.MaximumSlopeDegrees, 5, 30, 1, "Slopes steeper than this count as this steep.");
        return list;
    }
}

/// <summary>
/// The physics settings in play, built from the current <see cref="PhysicsTuning"/>. Everything in the app that
/// simulates a shot reads its ball, air, wind, surfaces and lie model from here, so a new tuning applies from the
/// next shot (a ball already moving keeps the numbers it was struck with). Main thread only.
/// </summary>
public static class ActivePhysics
{
    private static PhysicsSettings _settings = PhysicsSettings.From(PhysicsTuning.Defaults);

    /// <summary>Raised after <see cref="Apply"/> changes the tuning.</summary>
    public static event Action? Changed;

    public static PhysicsTuning Tuning => _settings.Tuning;
    /// <summary>Increases with every change (for caches of results that depend on the physics).</summary>
    public static int Version { get; private set; }

    public static PhysicsSettings Current => _settings;
    public static BallProperties Ball => _settings.Ball;
    public static EnvironmentConditions Environment => _settings.Environment;
    /// <summary>The same air with no wind (for typical-carry estimates, which don't know the shot's direction).</summary>
    public static EnvironmentConditions StillAir => _settings.StillAir;
    public static LieModel LieModel => _settings.LieModel;
    public static SurfaceProperties SurfaceFor(Lie lie) => _settings.SurfaceFor(lie);

    public static void Apply(PhysicsTuning tuning)
    {
        if (ReferenceEquals(tuning, _settings.Tuning)) return;
        _settings = PhysicsSettings.From(tuning);
        Version++;
        Changed?.Invoke();
    }
}

/// <summary>Engine inputs built from a <see cref="PhysicsTuning"/>: ball (aerodynamics), air and wind, lie model and course surfaces.</summary>
public sealed record PhysicsSettings(
    PhysicsTuning Tuning, BallProperties Ball, EnvironmentConditions Environment, EnvironmentConditions StillAir,
    LieModel LieModel, IReadOnlyDictionary<Lie, SurfaceProperties> Surfaces)
{
    private static readonly ConcurrentDictionary<double, double> StimpRolling = new();

    public SurfaceProperties SurfaceFor(Lie lie) => Surfaces.GetValueOrDefault(lie) ?? Surfaces[Lie.Fairway];

    public static PhysicsSettings From(PhysicsTuning t)
    {
        double drag = t["flight.drag"], lift = t["flight.lift"], decay = t["flight.spinDecay"], tilt = t["flight.dragSpeedTilt"];
        var ball = drag == 1 && lift == 1 && decay == 1 && tilt == 0
            ? BallProperties.Standard
            : BallProperties.Standard with { Aerodynamics = new ScaledAerodynamicModel(DefaultAerodynamics.Provisional, drag, lift, decay, tilt) };

        var air = new AtmosphericConditions
        {
            TemperatureCelsius = (t["air.temperature"] - 32) * 5 / 9,
            AltitudeMetres = t["air.altitude"] * UnitConversion.FeetToMetres,
            RelativeHumidityPercent = t["air.humidity"],
        };
        double windMs = t["wind.speed"] * UnitConversion.MphToMetresPerSecond, gustMs = t["wind.gusts"] * UnitConversion.MphToMetresPerSecond;
        IWindField wind = windMs <= 0 && gustMs <= 0 ? Wind.Calm : new PowerLawWindProfile(windMs, t["wind.from"], Exponent: t["wind.shear"]);
        if (gustMs > 0) wind = new GustingWind(wind, windMs, windMs + gustMs, t["wind.from"]);
        var still = air == AtmosphericConditions.Standard ?EnvironmentConditions.Standard : new EnvironmentConditions { Atmosphere = air };
        var environment = wind == Wind.Calm ? still : still with { Wind = wind };

        var responses = new Dictionary<LieKind, LieResponse>(LieModel.Default.Responses);
        foreach (var (key, _, kind) in PhysicsTuning.LieKinds)
            responses[kind] = new LieResponse(t[$"lie.{key}.speed"] / 100, t[$"lie.{key}.spin"] / 100, t[$"lie.{key}.launch"]);
        var lies = new LieModel(responses, t["lie.slope.launch"], t["lie.slope.spin"] / 100, t["lie.sidehill.start"], t["lie.sidehill.curve"], t["lie.slope.max"]);

        double wet = t["ground.wetness"];
        var surfaces = new Dictionary<Lie, SurfaceProperties>();
        foreach (var (key, _, lie) in PhysicsTuning.SurfaceKinds)
        {
            string k = "ground." + key;
            var s = lie == Lie.Green ? Green(t["ground.green.stimp"]) : CourseGround.DefaultSurfaceFor(lie);
            // One friction value: static friction keeps its margin over dynamic.
            double friction = t[k + ".friction"];
            s = s with
            {
                RestitutionScale = t[k + ".bounce"], CraterAngleDegrees = t[k + ".crater"], Firmness = t[k + ".firmness"],
                StaticFriction = s.StaticFriction + (friction - s.DynamicFriction), DynamicFriction = friction,
                TangentialRestitution = t[k + ".grab"], ImpactSkidSpeed = t[k + ".skid"], ImpactDrag = t[k + ".drag"], Moisture = wet,
            };
            if (lie != Lie.Green) s = s with { RollingResistance = t[k + ".rolling"] };
            surfaces[lie] = s;
        }
        // Tees play as fairway; water and out of bounds stop the ball, so their surface never matters.
        surfaces[Lie.Tee] = surfaces[Lie.Fairway];
        return new PhysicsSettings(t, ball, environment, still, lies, surfaces);
    }

    /// <summary>The default green at a stimp reading (its rolling resistance is calibrated once per reading, then cached).</summary>
    private static SurfaceProperties Green(double stimp)
    {
        var template = CourseGround.DefaultSurfaceFor(Lie.Green);
        if (stimp == 10) return template;
        double rolling = StimpRolling.GetOrAdd(stimp, s => GreenModel.Surface(s).RollingResistance);
        return template with { Name = $"Green (stimp {stimp:0.#})", RollingResistance = rolling };
    }
}
