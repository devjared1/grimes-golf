using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// Main camera. Rests at a "home" pose behind the ball looking along the aim line; in Follow mode it chases the
/// ball, holds on it after it stops, then glides back home. Before a shot, <see cref="SetLandingView"/> takes it
/// down the aim line to look at where the ball will land. <see cref="Flyover"/> flies down a hole from behind the
/// tee to the green.
/// </summary>
public partial class CameraRig : Camera3D
{
    private const double HoldSeconds = 2.5;
    private const float FovDegrees = 55;
    /// <summary>Landing view: how far short of the landing spot (m) and how high above it the camera sits.</summary>
    public const float LandingBack = 38, LandingUp = 20;
    /// <summary>Flyover: height above the ground (m), how far ahead it looks (m), speed (m/s) and how long it holds over the green (s).</summary>
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

    /// <summary>The point <paramref name="s"/> metres along the flyover route (before its start: back behind the tee).</summary>
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

    /// <summary>Screen units covered by UI on each side; the view is centred on the remaining area.</summary>
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

    /// <summary>Home pose behind <paramref name="ball"/> looking along <paramref name="aim"/> (horizontal unit vector).</summary>
    public void SetHome(Vector3 ball, Vector3 aim, bool putting, bool snap = false)
    {
        _putting = putting;
        if (putting)
        {
            _homePos = ball - aim * 4.5f + new Vector3(0, 1.8f, 0);
            _homeLook = ball + aim * 8f;
        }
        else
        {
            _homePos = ball - aim * 9f + new Vector3(0, 3.2f, 0);
            _homeLook = ball + aim * 110f;
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

    public override void _Process(double delta)
    {
        if (_player == null) return;

        // Off-axis projection: shift the optical centre to the middle of the area not covered by panels.
        float h = GetViewport().GetVisibleRect().Size.Y;
        if (h > 0) FrustumOffset = new Vector2(-(Insets.Left - Insets.Right) / 2, -(Insets.Bottom - Insets.Top) / 2) * (Size / h);
        if (_fly != null) { Fly(delta); return; }

        Vector3 wantPos = _homePos, wantLook = _homeLook;
        float rate = 1.8f;

        bool tracking = Follow && (_player.IsPlaying || _player.IdleSeconds < HoldSeconds);
        if (_landing is { } land)
        {
            wantPos = land - _landingDir * LandingBack + new Vector3(0, LandingUp, 0);
            wantPos.Y = Mathf.Max(wantPos.Y, Ground(wantPos) + LandingUp / 2);
            wantLook = land + _landingDir * 6f;
            rate = 2.2f;
        }
        else if (tracking)
        {
            var ball = _player.BallPosition;
            var dir = _player.ShotDirection;
            // Stay behind and above the ball along the shot line (much closer for a putt).
            float back = _putting ? 5.5f : 21f + ball.Y * 0.4f;
            float up = _putting ? 2.2f : 5.8f + ball.Y * 0.5f;
            wantPos = ball - dir * back + new Vector3(0, up, 0);
            wantPos.Y = Mathf.Max(wantPos.Y, Ground(wantPos) + (_putting ? 1.4f : 3f));
            wantLook = ball + dir * (_putting ? 2.5f : 8f);
            rate = 3.2f;
        }

        float k = 1f - Mathf.Exp(-rate * (float)delta);
        Position = Position.Lerp(wantPos, k);
        _look = _look.Lerp(wantLook, Mathf.Min(1f, k * 1.6f));
        if (Position.DistanceSquaredTo(_look) > 0.01f) LookAt(_look, Vector3.Up);
    }
}
