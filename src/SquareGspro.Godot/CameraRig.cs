using Godot;

namespace SquareGspro.Range3D;

/// <summary>
/// Main camera. Rests at a "home" pose behind the ball looking along the aim line; in Follow mode it chases the
/// ball, holds on it after it stops, then glides back home.
/// </summary>
public partial class CameraRig : Camera3D
{
    private const double HoldSeconds = 2.5;
    private const float FovDegrees = 55;

    private ShotPlayer _player = null!;
    private Vector3 _homePos, _homeLook, _look;
    private bool _putting;

    public bool Follow { get; set; } = true;

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

    public override void _Process(double delta)
    {
        if (_player == null) return;

        // Off-axis projection: shift the optical centre to the middle of the area not covered by panels.
        float h = GetViewport().GetVisibleRect().Size.Y;
        if (h > 0) FrustumOffset = new Vector2(-(Insets.Left - Insets.Right) / 2, -(Insets.Bottom - Insets.Top) / 2) * (Size / h);

        Vector3 wantPos = _homePos, wantLook = _homeLook;
        float rate = 1.8f;

        bool tracking = Follow && (_player.IsPlaying || _player.IdleSeconds < HoldSeconds);
        if (tracking)
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
