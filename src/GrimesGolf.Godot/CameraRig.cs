using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// Main camera. Rests at a "home" pose behind the ball looking along the aim line; in Follow mode it chases the
/// ball (after letting it go for a second or two), holds on it after it stops, then glides back home. Before a shot, <see cref="SetLandingView"/> takes it
/// down the aim line to look at where the ball will land. <see cref="Flyover"/> flies down a hole from behind the
/// tee to the green.
/// </summary>
public partial class CameraRig : Camera3D
{
    private const double HoldSeconds = 2.5;
    private const float FovDegrees = 55;
    /// <summary>Landing view: how far short of the landing spot (wu) and how high above it the camera sits.</summary>
    public const float LandingBack = 38, LandingUp = 20;
    /// <summary>Flyover: height above the ground (wu), how far ahead it looks (wu), speed (wu/s) and how long it holds over the green (s).</summary>
    private const float FlyHeight = 30, FlyLookAhead = 90, FlySpeed = 55;
    private const double FlyHold = 1.2;

    private ShotPlayer _player = null!;
    private Vector3 _homePos, _homeLook, _look;
    private bool _putting;

    public bool Follow { get; set; } = true;

    // Landing view: the spot the shot set up would land on (null = off), and the aim direction to it.
    private Vector3? _landing;
    private Vector3 _landingDir;

    /// <summary>
    /// Pre-shot view of the landing spot <paramref name="at"/> (on the ground), from above and short of it along
    /// <paramref name="aim"/> (horizontal unit vector); null goes back behind the ball.
    /// </summary>
    public void SetLandingView(Vector3? at, Vector3 aim)
    {
        _landing = at;
        _landingDir = aim;
    }

    // Flyover: the hole's route on the ground, its cumulative lengths, and the clock.
    private Vector3[]? _fly;
    private float[] _flyAt = Array.Empty<float>();
    private double _flyT, _flyDuration;

    /// <summary>A hole flyover is playing.</summary>
    public bool Flying => _fly != null;
    /// <summary>The flyover has finished (or was skipped) and the camera is back behind the ball.</summary>
    public event Action? FlyoverFinished;

    /// <summary>
    /// Flies down a hole along <paramref name="route"/> (points on the ground, tee to pin): from behind the tee, high
    /// up and looking ahead, to just short of the green looking at it; holds there a moment, then cuts back home.
    /// </summary>
    public void Flyover(IReadOnlyList<Vector3> route)
    {
        if (route.Count < 2) return;
        _fly = route.ToArray();
        _flyAt = new float[_fly.Length];
        for (int i = 1; i < _fly.Length; i++) _flyAt[i] = _flyAt[i - 1] + Flat(_fly[i] - _fly[i - 1]).Length();
        _flyDuration = Mathf.Clamp(_flyAt[^1] / FlySpeed, 4.5f, 9f);
        _flyT = 0;
    }

    /// <summary>Ends the flyover now (Space, or a shot arriving) and goes back behind the ball.</summary>
    public void SkipFlyover()
    {
        if (_fly == null) return;
        _fly = null;
        Position = _homePos;
        _look = _homeLook;
        LookAt(_look, Vector3.Up);
        FlyoverFinished?.Invoke();
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>The point <paramref name="s"/> world units along the flyover route (before its start: back behind the tee).</summary>
    private Vector3 Along(float s)
    {
        var p = _fly!;
        if (s <= 0) return p[0] + Flat(p[0] - p[1]).Normalized() * -s;
        for (int i = 1; i < p.Length; i++)
            if (s <= _flyAt[i]) return p[i - 1].Lerp(p[i], (s - _flyAt[i - 1]) / Mathf.Max(_flyAt[i] - _flyAt[i - 1], 0.01f));
        return p[^1];
    }

    private void Fly(double delta)
    {
        _flyT += delta;
        if (_flyT >= _flyDuration + FlyHold) { SkipFlyover(); return; }
        float u = Mathf.SmoothStep(0, 1, (float)Mathf.Min(_flyT / _flyDuration, 1));
        float total = _flyAt[^1];
        float s = Mathf.Lerp(-60, Mathf.Max(total - 45, 0), u);
        var pos = Along(s);
        pos.Y = Mathf.Max(pos.Y, Ground(pos)) + FlyHeight;
        var look = Along(Mathf.Min(s + FlyLookAhead, total));
        look.Y = Ground(look) + 1;
        Position = pos;
        _look = look;
        LookAt(_look, Vector3.Up);
    }

    /// <summary>Ground height under a point (null = flat at 0). Keeps the camera above hills and aims it up/down slopes.</summary>
    public Func<Vector3, float>? GroundAt { get; set; }

    private float Ground(Vector3 at) => GroundAt?.Invoke(at) ?? 0f;

    /// <summary>Screen units covered by UI on each side; the view is centered on the remaining area.</summary>
    public (float Left, float Top, float Right, float Bottom) Insets { get; set; }

    public void Init(ShotPlayer player)
    {
        _player = player;
        Projection = ProjectionType.Frustum;
        Near = 0.1f;
        Size = 2 * Near * Mathf.Tan(Mathf.DegToRad(FovDegrees / 2));
        Far = 2500;
        Current = true;
        // See the world and the camera-facing trails, but not the minimap-only geometry.
        CullMask = 1 | ShotPlayer.TrailLayer;
        SetHome(Vector3.Zero, Vector3.Forward, putting: false, snap: true);
    }

    /// <summary>
    /// How near the camera sits for a shot of <paramref name="units"/>: 0 for a short one (a pitch or a chip, framed
    /// close), 1 for a long one (a full iron or more).
    /// </summary>
    private static float Reach(float units) => Mathf.Clamp((units - 20f) / 160f, 0f, 1f);

    /// <summary>
    /// Home pose behind <paramref name="ball"/> looking along <paramref name="aim"/> (horizontal unit vector), for a
    /// shot of about <paramref name="shotLength"/> world units: nearer the ball and looking less far ahead for a short one.
    /// </summary>
    public void SetHome(Vector3 ball, Vector3 aim, bool putting, bool snap = false, float shotLength = 150f)
    {
        _putting = putting;
        if (putting)
        {
            _homePos = ball - aim * 4.5f + new Vector3(0, 1.8f, 0);
            _homeLook = ball + aim * 8f;
        }
        else
        {
            float reach = Reach(shotLength);
            _homePos = ball - aim * Mathf.Lerp(5.5f, 8f, reach) + new Vector3(0, Mathf.Lerp(2.2f, 3.0f, reach), 0);
            _homeLook = ball + aim * Mathf.Clamp(shotLength * 1.1f + 10f, 30f, 110f);
            // Look at the ground out there (up or down the hill), a little above it.
            _homeLook.Y = (GroundAt == null ? ball.Y : Ground(_homeLook)) + 4f;
        }
        _homePos.Y = Mathf.Max(_homePos.Y, Ground(_homePos) + (putting ? 1.2f : 2.0f));
        if (snap)
        {
            Position = _homePos;
            _look = _homeLook;
            LookAt(_look, Vector3.Up);
        }
    }

    /// <summary>Puts the camera right beside <paramref name="ball"/>, looking at it (--ball-closeup, to check how the ball looks).</summary>
    public void CloseUp(Vector3 ball)
    {
        _homePos = ball + new Vector3(0.08f, 0.1f, 0.22f);
        _homeLook = ball;
        Position = _homePos;
        _look = _homeLook;
        LookAt(_look, Vector3.Up);
    }

    /// <summary>How long (s) the camera takes to pick up speed after it starts following a shot.</summary>
    private const double SettleSeconds = 0.6;

    /// <summary>
    /// How the camera follows one kind of shot. Always directly behind the ball, in line with where it was struck;
    /// only how far back, how long it waits, and how it sits vertically differ.
    /// </summary>
    /// <param name="Hold">Seconds after the strike before the camera starts following.</param>
    /// <param name="Back">Distance behind the ball (wu): kept large, so the shot stays framed from a distance.</param>
    /// <param name="Rise">While the ball climbs, the camera sits this fraction of the ball's height below it.</param>
    /// <param name="Above">Once the ball is coming down (and rolling), the camera sits this far above it (wu).</param>
    /// <param name="Clear">The camera never goes lower than this above the ground (wu).</param>
    private readonly record struct FollowStyle(double Hold, float Back, float Rise, float Above, float Clear);

    private static readonly FollowStyle FullStyle = new(1.25, 30f, 0f, 15f, 2.0f);
    private static readonly FollowStyle PitchStyle = new(1.0, 17f, 0f, 8f, 2.0f);
    private static readonly FollowStyle ChipStyle = new(1.0, 11f, 0f, 3.5f, 1.8f);

    /// <summary>Carry (wu) below which a shot is a chip, and below which it's a pitch; beyond that it's a full shot.</summary>
    private const float ChipCarry = 22f, PitchCarry = 70f;

    private static FollowStyle StyleFor(float carryUnits) =>
        carryUnits < ChipCarry ? ChipStyle : carryUnits < PitchCarry ? PitchStyle : FullStyle;

    /// <summary>
    /// Where the camera wants to be and look while following the ball in the air or rolling out: on the line from
    /// where the ball was struck through the ball (so a hook or a slice is followed round, not cut across), back by
    /// <see cref="FollowStyle.Back"/>; below the ball while it climbs, above it once it comes down.
    /// </summary>
    private (Vector3 Pos, Vector3 Look) FollowPose(FollowStyle style)
    {
        var ball = _player.BallPosition;
        var line = Flat(ball - _player.ShotStart);
        // Right after the strike the ball is too close to its start for that line to mean much: use the aim.
        float away = Mathf.SmoothStep(0, 1, Mathf.Clamp(line.Length() / 10f, 0f, 1f));
        var dir = _player.ShotDirection.Lerp(line.LengthSquared() > 1e-4f ? line.Normalized() : _player.ShotDirection, away).Normalized();

        // Vertical: 0 while the ball is still climbing, 1 once it's over the top and coming down, eased in just before the apex, so the camera is already over the ball as it starts down.
        float down = Mathf.SmoothStep(0, 1, Mathf.Clamp((float)(_player.ShotTime - _player.ApexTime + 0.9) / 1.2f, 0f, 1f));
        float height = Mathf.Max(ball.Y - Ground(ball), 0f);
        float offset = Mathf.Lerp(-style.Rise * height, style.Above, down);

        var pos = ball - dir * style.Back;
        pos.Y = Mathf.Max(ball.Y + offset, Ground(pos) + style.Clear);

        // Aim: just past the ball while it climbs; once it's coming down, between the ball and where it will land, so
        // the camera looks down at both.
        var look = ball + dir * 3f;
        var toLanding = Flat(_player.LandingPoint - ball);
        float ahead = toLanding.Dot(dir);
        if (ahead > 0f)
        {
            var mid = ball + toLanding * 0.5f;
            mid.Y = Ground(mid);
            look = look.Lerp(mid, down * Mathf.SmoothStep(0, 1, Mathf.Clamp(ahead / 10f, 0f, 1f)));
        }
        return (pos, look);
    }

    // Full-shot camera, modeled on GSPro's range camera (see FullPose). Distances in wu, times as fractions of the flight.
    private const double FullHoldFraction = 0.17, FullHoldMin = 1.0, FullHoldMax = 2.0;
    private const double SwoopStartFraction = 0.08;   // of the way from the apex to the landing
    private const double SwoopFraction = 0.13, SwoopMin = 0.7;
    private const float SwoopBack = 55f, SwoopHeight = 45f;     // where the swoop ends: high and back from the ball
    private const float OverheadBack = 14f, OverheadHeight = 26f; // where it settles: nearly straight over the ball

    /// <summary>
    /// A full shot, as GSPro's range camera does it: it stays put at the address pose for the first second or so; then,
    /// still in place, tilts up to keep the ball in view as it climbs and starts down; once the ball is on its way
    /// down it swoops up and forward to a high spot behind the landing area, looking down at it with the ball dropping
    /// in; and it then drifts down and in to nearly overhead, following the ball as it rolls out. Null: stay at home.
    /// </summary>
    private (Vector3 Pos, Vector3 Look, float Rate)? FullPose()
    {
        double t = _player.ShotTime, land = _player.LandTime, apex = _player.ApexTime;
        if (land <= 0 || t < Math.Clamp(FullHoldFraction * land, FullHoldMin, FullHoldMax)) return null;

        var ball = _player.BallPosition;
        double swoopStart = apex + SwoopStartFraction * (land - apex);
        double swoopEnd = Math.Min(swoopStart + Math.Max(SwoopFraction * land, SwoopMin), land - 0.2);
        if (t < swoopStart) return (_homePos, ball, 10f);

        var g = t < land ? _player.LandingPoint : ball;     // the ground spot it looks toward: where the ball comes down
        g.Y = Ground(g);
        var onGround = ball;                                 // ...and the camera sits behind the ball, so it always has the ball in view
        onGround.Y = Ground(ball);
        var line = Flat(g - _player.ShotStart);
        var dir = line.LengthSquared() > 1e-4f ? line.Normalized() : _player.ShotDirection;
        Vector3 Pose(float back, float height) { var p = onGround - dir * back; p.Y = onGround.Y + height; return p; }

        var far = Pose(SwoopBack, SwoopHeight);
        if (t < swoopEnd)
        {
            float s = Mathf.SmoothStep(0, 1, (float)((t - swoopStart) / (swoopEnd - swoopStart)));
            float up = 1f - (1f - s) * (1f - s);   // gets up in the air ahead of the move forward
            var pos = new Vector3(Mathf.Lerp(_homePos.X, far.X, s), Mathf.Lerp(_homePos.Y, far.Y, up), Mathf.Lerp(_homePos.Z, far.Z, s));
            return (pos, ball.Lerp(g, 0.5f * s), 20f);
        }

        float u = Mathf.SmoothStep(0, 1, Mathf.Clamp((float)((t - swoopEnd) / (land + 0.5 - swoopEnd)), 0f, 1f));
        var settled = Pose(Mathf.Lerp(SwoopBack, OverheadBack, u), Mathf.Lerp(SwoopHeight, OverheadHeight, u));
        return t < land ? (settled, ball.Lerp(g, 0.5f), 20f) : (settled, ball, 2.5f);
    }

    public override void _Process(double delta)
    {
        if (_player == null) return;

        // Off-axis projection: shift the optical center to the middle of the area not covered by panels.
        float h = GetViewport().GetVisibleRect().Size.Y;
        if (h > 0) FrustumOffset = new Vector2(-(Insets.Left - Insets.Right) / 2, -(Insets.Bottom - Insets.Top) / 2) * (Size / h);
        if (_fly != null) { Fly(delta); return; }

        Vector3 wantPos = _homePos, wantLook = _homeLook;
        float rate = 1.8f, lookGain = 1.6f, heightGain = 1f;

        bool tracking = Follow && (_player.IsPlaying || _player.IdleSeconds < HoldSeconds);
        if (_landing is { } land)
        {
            wantPos = land - _landingDir * LandingBack + new Vector3(0, LandingUp, 0);
            wantPos.Y = Mathf.Max(wantPos.Y, Ground(wantPos) + LandingUp / 2);
            wantLook = land + _landingDir * 6f;
            rate = 2.2f;
        }
        else if (tracking && _player.CarryUnits <= 0)
        {
            // A putt (or anything rolled along the ground): close behind and a little above the ball.
            var ball = _player.BallPosition;
            var dir = _player.ShotDirection;
            wantPos = ball - dir * 5.5f + new Vector3(0, 2.2f, 0);
            wantPos.Y = Mathf.Max(wantPos.Y, Ground(wantPos) + 1.4f);
            wantLook = ball + dir * 2.5f;
            rate = 3.2f;
        }
        else if (tracking && _player.CarryUnits >= PitchCarry)
        {
            if (FullPose() is { } full)
            {
                (wantPos, wantLook, rate) = full;
                lookGain = 1f;
            }
        }
        else if (tracking)
        {
            // Held at home for the first second or two (the ball is hit, and the camera lets it go), then eased onto
            // the follow pose.
            var style = StyleFor(_player.CarryUnits);
            float settle = (float)((_player.ShotTime - style.Hold) / SettleSeconds);
            if (settle >= 0)
            {
                (wantPos, wantLook) = FollowPose(style);
                heightGain = 2f;   // gets up to the ball's height ahead of the sideways sweep, so it never looks up at it from underneath
                lookGain = 3.5f;   // the aim keeps the ball centered even while the camera itself eases along
                rate = Mathf.Lerp(2.0f, 6.0f, Mathf.SmoothStep(0, 1, Mathf.Clamp(settle, 0f, 1f)));
            }
        }

        float k = 1f - Mathf.Exp(-rate * (float)delta);
        float ky = Mathf.Min(1f, k * heightGain);
        Position = new Vector3(Mathf.Lerp(Position.X, wantPos.X, k), Mathf.Lerp(Position.Y, wantPos.Y, ky), Mathf.Lerp(Position.Z, wantPos.Z, k));
        _look = _look.Lerp(wantLook, Mathf.Min(1f, k * lookGain));
        if (Position.DistanceSquaredTo(_look) > 0.01f) LookAt(_look, Vector3.Up);
    }
}
