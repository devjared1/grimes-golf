namespace GrimesGolf.Core.Course;

/// <summary>
/// How a round is scored. <see cref="Scramble"/> and <see cref="BestBall"/> are team games, and so is
/// <see cref="MatchPlay"/> with four players (best ball of two against two). See <see cref="Match"/>.
/// </summary>
public enum GameType { Stroke, Scramble, BestBall, MatchPlay }

/// <summary>Which of the course's holes a round plays.</summary>
public enum HoleSet { Full18, Front9, Back9, Par3s }

/// <summary>
/// The weather for a round. <see cref="Mild"/> leaves the air and ground as the physics tuning has them; the others
/// set the temperature and humidity, and <see cref="Wet"/> soaks the ground too.
/// </summary>
public enum Weather { Mild, Hot, Cold, Wet }

/// <summary>Height above sea level the air is for: the course's own (from its elevation data), or a set height.</summary>
public enum AltitudeSetting { Course, SeaLevel, Feet2500, Feet5000, Feet7500 }

public enum FairwaySpeed { Slow, Medium, Fast }

public enum GreenFirmness { Soft, Medium, Firm }

/// <summary>
/// Everything chosen on the Game Setup screen for a round: the game, the holes, the weather and the course
/// conditions. Kept in the settings (the last one used) and with each saved round.
/// </summary>
/// <param name="Stimp">Green speed on the stimpmeter, ft (<see cref="Stimps"/>).</param>
public sealed record GameSetup(
    GameType Type = GameType.Stroke, HoleSet Holes = HoleSet.Full18,
    WindStrength Wind = WindStrength.Calm, AltitudeSetting Altitude = AltitudeSetting.Course, Weather Weather = Weather.Mild,
    FairwaySpeed Fairways = FairwaySpeed.Medium, GreenFirmness Greens = GreenFirmness.Medium, double Stimp = 10)
{
    public static readonly GameSetup Default = new();

    /// <summary>The green speeds offered, slow club greens to tournament-fast.</summary>
    public static readonly double[] Stimps = { 8, 9, 10, 11, 12, 13 };

    /// <summary>
    /// The holes this round plays, in order. Front and back nine are holes 1-9 and 10-18 (by number); a course of
    /// nine holes or fewer plays them all either way. Par 3s are every par-3 hole. Never empty: all the holes if none fit.
    /// </summary>
    public IReadOnlyList<Hole> Pick(IReadOnlyList<Hole> holes)
    {
        bool nineOrFewer = holes.Count <= 9;
        var picked = Holes switch
        {
            HoleSet.Front9 when !nineOrFewer => holes.Where(h => h.Number <= 9),
            HoleSet.Back9 when !nineOrFewer => holes.Where(h => h.Number >= 10 && h.Number <= 18),
            HoleSet.Par3s => holes.Where(h => h.Par == 3),
            _ => holes,
        };
        var list = picked.ToList();
        return list.Count > 0 ? list : holes;
    }

    /// <summary>Why <paramref name="type"/> can't be played by this many players (null = it can).</summary>
    public static string? Problem(GameType type, int players) => type switch
    {
        GameType.Scramble or GameType.BestBall when players < 2 => "needs 2 or more players",
        GameType.MatchPlay when players is not (2 or 4) => "needs 2 players (or 4, two against two)",
        _ => null,
    };

    /// <summary>The game actually played by this many players: the chosen one, or stroke play if it can't be.</summary>
    public GameType TypeFor(int players) => Problem(Type, players) == null ? Type : GameType.Stroke;

    /// <summary>The air's height above sea level, m (<paramref name="courseMetres"/> is the course's own).</summary>
    public double AltitudeMetres(double courseMetres) => Altitude switch
    {
        AltitudeSetting.SeaLevel => 0,
        AltitudeSetting.Feet2500 => 2500 * Units.FeetToMetres,
        AltitudeSetting.Feet5000 => 5000 * Units.FeetToMetres,
        AltitudeSetting.Feet7500 => 7500 * Units.FeetToMetres,
        _ => courseMetres,
    };

    /// <summary>
    /// The physics tuning with this round's conditions on top: the altitude; the weather's temperature, humidity and
    /// wetness (mild weather keeps the tuning's); fairways that roll out more or less; softer or firmer greens; and
    /// the green speed. Everything else stays as tuned.
    /// </summary>
    public PhysicsTuning ApplyTo(PhysicsTuning t, double courseAltitudeMetres)
    {
        t = t.With("air.altitude", AltitudeMetres(courseAltitudeMetres) / Units.FeetToMetres);
        t = Weather switch
        {
            Weather.Hot => t.With("air.temperature", 95).With("air.humidity", 40),
            Weather.Cold => t.With("air.temperature", 45).With("air.humidity", 50),
            Weather.Wet => t.With("air.temperature", 58).With("air.humidity", 95).With("ground.wetness", 0.6),
            _ => t,
        };
        // Fairway speed: how far the ball runs out (and a little livelier when it's firm and fast).
        (double rolling, double firm) = Fairways switch { FairwaySpeed.Slow => (1.35, -0.1), FairwaySpeed.Fast => (0.72, 0.1), _ => (1, 0) };
        t = t.With("ground.fairway.rolling", t["ground.fairway.rolling"] * rolling).With("ground.fairway.firmness", t["ground.fairway.firmness"] + firm);
        // Green firmness: soft greens catch the ball and let it dig in; firm ones bounce it on.
        (double firmness, double crater) = Greens switch { GreenFirmness.Soft => (-0.2, 3), GreenFirmness.Firm => (0.2, -3), _ => (0, 0) };
        t = t.With("ground.green.firmness", t["ground.green.firmness"] + firmness).With("ground.green.crater", t["ground.green.crater"] + crater);
        return t.With("ground.green.stimp", Stimp);
    }

    // ---- names, for the setup screen and the round's summary ----

    public static string Name(GameType t) => t switch
    {
        GameType.BestBall => "Best Ball", GameType.MatchPlay => "Match Play", GameType.Scramble => "Scramble", _ => "Stroke Play",
    };

    public static string Name(HoleSet h) => h switch
    {
        HoleSet.Front9 => "Front 9", HoleSet.Back9 => "Back 9", HoleSet.Par3s => "Par 3s", _ => "Full 18",
    };

    public static string Name(AltitudeSetting a) => a switch
    {
        AltitudeSetting.SeaLevel => "Sea level", AltitudeSetting.Feet2500 => "2,500 ft", AltitudeSetting.Feet5000 => "5,000 ft",
        AltitudeSetting.Feet7500 => "7,500 ft", _ => "Course",
    };

    /// <summary>"Stroke Play · Front 9 · Breezy · Wet · stimp 11", leaving out what's at its usual setting.</summary>
    public string Summary(int players)
    {
        var parts = new List<string> { Name(TypeFor(players)), Name(Holes) };
        if (Wind != WindStrength.Calm) parts.Add(Wind.ToString());
        if (Weather != Weather.Mild) parts.Add(Weather.ToString());
        if (Altitude != AltitudeSetting.Course) parts.Add(Name(Altitude));
        if (Fairways != FairwaySpeed.Medium) parts.Add($"{Fairways} fairways");
        if (Greens != GreenFirmness.Medium) parts.Add($"{Greens} greens");
        if (Stimp != 10) parts.Add($"stimp {Stimp:0.#}");
        return string.Join("  ·  ", parts);
    }
}
