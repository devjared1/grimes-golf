using Godot;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// Plays shots: animates the ball along a <see cref="FlightResult"/>, draws a camera-facing trail behind it that fades
/// out from its far end (and entirely once the ball stops), labels the carry where the ball lands, and draws flat ground paths for the
/// current and recent shots that only the minimap camera sees.
/// </summary>
public partial class ShotPlayer : Node3D
{
    /// <summary>Render layer for camera-facing trails (main camera only).</summary>
    public const uint TrailLayer = 1 << 2;
    /// <summary>Render layer for flat ground paths and big dots (minimap camera only).</summary>
    public const uint MapLayer = 1 << 1;

    private const int MaxHistory = 10;
    /// <summary>Ball paths are drawn this far above y=0 so they sit on top of greens, sand and water (which are layered a few cm up).</summary>
    private const float Lift = 0.16f;
    /// <summary>In real proportion to the (enlarged) cup: a 42.67 mm ball to a 108 mm hole. Grows with distance, except near the cup.</summary>
    public const float BallRadius = Scenery.CupRadius * 42.67f / 108f;
    /// <summary>Within this horizontal distance of the cup (m) the ball is drawn at true size; growth returns over <see cref="CupBlend"/> beyond it.</summary>
    private const float CupTrueSize = 1.5f, CupBlend = 6f;
    /// <summary>Distance at which the ball starts being drawn bigger than <see cref="BallRadius"/>.</summary>
    private const float GrowFrom = 12f;
    private static readonly Color Current = new(1.0f, 0.82f, 0.25f);
    private static readonly Color Old = new(1, 1, 1, 0.55f);

    private sealed class Shot
    {
        public required FlightResult Flight;
        public required MeshInstance3D Trail;
        public required MeshInstance3D MapPath;
        public required Node3D Markers;
        public bool TreeReached;
    }

    /// <summary>The ball came down on this lie (not for putts).</summary>
    public event Action<Lie>? Landed;
    public event Action? TreeHit;
    /// <summary>The ball reached the end of its path.</summary>
    public event Action? Stopped;

    private readonly List<Shot> _shots = new();
    private MeshInstance3D _ball = null!, _mapBall = null!;
    private Decal _contactShadow = null!;
    private Label3D _carryLabel = null!;
    private StandardMaterial3D _ribbonMat = null!;
    private Vector3? _placed;
    private double _t;

    /// <summary>
    /// Height of the surface the ball rolls on above the path's ground level (the raised green layer on a flat
    /// course, the mat on the range, 0 where the ground has real heights).
    /// </summary>
    public float GroundTop { get; set; } = 0.115f;

    /// <summary>Where the cup is (null on the range): near it the ball isn't enlarged, so it stays in proportion with the hole.</summary>
    public Vector3? CupAt { get; set; }

    /// <summary>Draw the previous shot's flight faintly (physics tuning comparisons).</summary>
    public bool ShowGhost { get; set; }

    /// <summary>What the ground is at a point (null: grass everywhere, as on the range); picks what flies up where the ball lands.</summary>
    public Func<Vector3, Lie>? LieAt { get; set; }

    public bool IsPlaying { get; private set; }
    /// <summary>Seconds since the current shot finished rolling (0 while playing).</summary>
    public double IdleSeconds { get; private set; } = 999;
    public Vector3 BallPosition => _ball.Position;
    /// <summary>Horizontal direction of the current shot from where it was struck (defaults downrange).</summary>
    public Vector3 ShotDirection { get; private set; } = Vector3.Forward;
    /// <summary>How far (m, along the ground) the current shot goes from where it was struck to where it stops.</summary>
    public float ShotLength { get; private set; } = 150;

    /// <summary>
    /// The ball's time in the air plays this much faster than real time, so a long shot doesn't drag; once it's down,
    /// bounces and roll play at real speed (putts too).
    /// </summary>
    private const double FlightSpeedUp = 1.2;

    public override void _Ready()
    {
        _ribbonMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _ball = new MeshInstance3D
        {
            Mesh = BallLook.Mesh(BallRadius),
            MaterialOverride = BallLook.Material(),
            Position = new Vector3(0, GroundTop + BallRadius, 0),
            Layers = TrailLayer,   // the main view only, and clear of its own contact shadow
        };
        AddChild(_ball);
        _contactShadow = BallLook.ContactShadow();
        AddChild(_contactShadow);
        _mapBall = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 3, Height = 6 },
            Layers = MapLayer,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        };
        AddChild(_mapBall);
        _carryLabel = new Label3D
        {
            FontSize = 40, OutlineSize = 12, FixedSize = true, PixelSize = 0.0012f, NoDepthTest = true,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Modulate = Colors.White, OutlineModulate = new Color(0, 0, 0, 0.85f),
        };
        AddChild(_carryLabel);
    }

    public void Play(FlightResult flight)
    {
        foreach (var s in _shots) Recolor(s, Old);
        if (_shots.Count >= MaxHistory)
        {
            var oldest = _shots[0];
            oldest.Trail.QueueFree(); oldest.MapPath.QueueFree(); oldest.Markers.QueueFree();
            _shots.RemoveAt(0);
        }

        var shot = new Shot
        {
            Flight = flight,
            Trail = new MeshInstance3D { Mesh = new ImmediateMesh(), MaterialOverride = _ribbonMat, Layers = TrailLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off },
            MapPath = new MeshInstance3D { Mesh = new ImmediateMesh(), MaterialOverride = _ribbonMat, Layers = MapLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off },
            Markers = new Node3D { Visible = false },
        };
        AddChild(shot.Trail);
        AddChild(shot.MapPath);
        AddChild(shot.Markers);
        BuildMarkers(shot);
        _shots.Add(shot);

        var flat = ToGodot(flight.Points[^1]) - ToGodot(flight.Points[0]);
        flat.Y = 0;
        if (flat.LengthSquared() > 0.25f) ShotDirection = flat.Normalized();
        ShotLength = flat.Length();
        _placed = null;
        _t = 0;
        IsPlaying = true;
        IdleSeconds = 0;
        _carryLabel.Visible = false;
    }

    /// <summary>The burst where a flight first comes down (not for putts, which never leave the ground).</summary>
    private void Splash(IReadOnlyList<FlightPoint> pts, int landing, Vector3 land)
    {
        var a = pts[landing - 1]; var b = pts[landing];
        float fall = (float)((a.Y - b.Y) / Math.Max(b.T - a.T, 1e-3));
        var ground = land - new Vector3(0, Lift - GroundTop, 0);
        Impacts.Land(this, ground, LieAt?.Invoke(ground) ?? Lie.Fairway, ShotDirection, fall);
    }

    /// <summary>Removes all shots and puts the ball at <paramref name="ballAt"/> (the range tee by default).</summary>
    public void Clear(Vector3? ballAt = null)
    {
        foreach (var s in _shots) { s.Trail.QueueFree(); s.MapPath.QueueFree(); s.Markers.QueueFree(); }
        _shots.Clear();
        IsPlaying = false;
        IdleSeconds = 999;
        _carryLabel.Visible = false;
        PlaceBall(ballAt ?? Vector3.Zero);
    }

    public void HideCarryLabel() => _carryLabel.Visible = false;

    /// <summary>Another player's ball at rest: where it sits on the ground, their colour and name.</summary>
    public readonly record struct OtherBall(Vector3 Ground, Color Color, string Name);

    private Node3D? _others;

    /// <summary>
    /// Shows the other players' balls (multiplayer): a ball in their colour, drawn a little larger so it can be found
    /// from a distance, with their name above it, and a dot in their colour on the minimap.
    /// </summary>
    public void SetOtherBalls(IReadOnlyList<OtherBall> balls)
    {
        _others?.QueueFree();
        _others = null;
        if (balls.Count == 0) return;
        _others = new Node3D();
        AddChild(_others);
        foreach (var b in balls)
        {
            const float r = BallRadius * 2.5f;
            _others.AddChild(new MeshInstance3D
            {
                Mesh = BallLook.Mesh(r),
                MaterialOverride = BallLook.Material(b.Color, glowNear: 0.35f),
                Position = b.Ground + new Vector3(0, GroundTop + r, 0),
            });
            _others.AddChild(new Label3D
            {
                Text = b.Name, FontSize = 36, OutlineSize = 10, FixedSize = true, PixelSize = 0.0012f, NoDepthTest = true,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Modulate = b.Color, OutlineModulate = new Color(0, 0, 0, 0.85f),
                Position = b.Ground + new Vector3(0, GroundTop + 1.2f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Layers = TrailLayer,   // the main view only, not the minimap
            });
            _others.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 3, Height = 6 },
                Layers = MapLayer,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = b.Color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
                Position = b.Ground + new Vector3(0, 5, 0),
            });
        }
    }

    /// <summary>Moves the resting ball, e.g. back to the previous spot after a penalty, or into the cup.</summary>
    public void PlaceBall(Vector3 groundPoint)
    {
        _placed = groundPoint;
        _ball.Position = groundPoint + new Vector3(0, GroundTop + BallRadius, 0);
    }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        var camPos = cam.GlobalPosition;

        if (_shots.Count > 0)
        {
            var cur = _shots[^1];
            var pts = cur.Flight.Points;
            if (IsPlaying)
            {
                bool flying = cur.Flight.LandingIndex > 0 && _t < pts[cur.Flight.LandingIndex].T;
                _t += delta * (flying ? FlightSpeedUp : 1);
                if (_t >= pts[^1].T) { _t = pts[^1].T; IsPlaying = false; Stopped?.Invoke(); }
            }
            else IdleSeconds += delta;

            int i = IndexAt(pts, _t);
            var head = Interpolate(pts, _t);
            if (!cur.TreeReached && cur.Flight.TreeHitIndex is { } tree && i >= tree)
            {
                cur.TreeReached = true;
                TreeHit?.Invoke();
            }
            // The path is drawn Lift up; the ball itself sits on the surface (and drops below it into the cup).
            var was = _ball.Position;
            _ball.Position = _placed is { } placed ? placed + new Vector3(0, GroundTop + BallRadius, 0) : head + new Vector3(0, GroundTop - Lift + BallRadius, 0);
            if (IsPlaying) Roll(_ball.Position - was);

            if (i >= cur.Flight.LandingIndex && !cur.Markers.Visible)
            {
                cur.Markers.Visible = true;
                var land = ToGodot(pts[cur.Flight.LandingIndex]);
                // Putts have no carry: label the roll in feet instead.
                _carryLabel.Text = cur.Flight.LandingIndex == 0 ? $"{cur.Flight.TotalYards * 3:0} ft" : $"{cur.Flight.CarryYards:0} yd";
                // Beside the landing spot, off the descending trail.
                _carryLabel.Position = land + new Vector3(0, 0.5f, 0) + ShotDirection.Cross(Vector3.Up) * 3.5f;
                _carryLabel.Visible = true;
                if (cur.Flight.LandingIndex > 0)
                {
                    Splash(pts, cur.Flight.LandingIndex, land);
                    Landed?.Invoke(LieAt?.Invoke(land) ?? Lie.Fairway);
                }
            }

            for (int s = 0; s < _shots.Count; s++)
            {
                var shot = _shots[s];
                bool isCurrent = s == _shots.Count - 1;
                int upTo = isCurrent ? i : shot.Flight.Points.Count - 1;
                Vector3? tip = isCurrent ? head : null;
                // Only the current shot leaves a trail in the air (earlier ones stay on the minimap), plus the
                // previous one as a faint ghost for comparison when it was struck from the same spot.
                // A putt's trail lies on the green and stops a few inches behind the ball; a flight's follows the ball's centre.
                bool putt = shot.Flight.LandingIndex == 0;
                if (isCurrent) BuildTrail((ImmediateMesh)shot.Trail.Mesh, shot.Flight.Points, upTo, head, _t, IdleSeconds, camPos,
                                          drop: putt ? Lift - GroundTop - PuttTrailHeight : 0, gap: putt ? PuttTrailGap : 0);
                else if (ShowGhost && s == _shots.Count - 2 && ToGodot(shot.Flight.Points[0]).DistanceTo(ToGodot(cur.Flight.Points[0])) < 1)
                {
                    shot.Trail.Visible = true;
                    BuildGhost((ImmediateMesh)shot.Trail.Mesh, shot.Flight.Points, camPos);
                }
                else shot.Trail.Visible = false;
                if (isCurrent || shot.MapPath.GetMeta("built", false).AsBool() == false)
                {
                    BuildGroundPath((ImmediateMesh)shot.MapPath.Mesh, shot.Flight.Points, upTo, tip, isCurrent ? Current : Old);
                    if (!isCurrent) shot.MapPath.SetMeta("built", true);
                }
            }
        }

        // Keep the ball visible from far away, but true to size beside the cup.
        float d = camPos.DistanceTo(_ball.GlobalPosition);
        float grow = Mathf.Max(1f, d / GrowFrom);
        if (CupAt is { } cup)
        {
            var off = _ball.GlobalPosition - cup;
            float toCup = new Vector2(off.X, off.Z).Length();
            grow = Mathf.Lerp(1f, grow, Mathf.Clamp((toCup - CupTrueSize) / CupBlend, 0f, 1f));
        }
        _ball.Scale = Vector3.One * grow;
        // Falls a little away from the sun; tall enough to reach the ground from the ball's centre, and no further.
        float r = BallRadius * grow;
        _contactShadow.Position = _ball.Position + Scenery.ShadowFalls * r * 0.6f;
        _contactShadow.Size = new Vector3(r * 3.6f, r * 4, r * 3.6f);
        _mapBall.Position = _ball.Position + new Vector3(0, 1, 0);
    }

    /// <summary>The most the ball turns in one frame (radians): true flight speeds would only strobe.</summary>
    private const float MaxTurnPerFrame = 0.5f;

    /// <summary>Turns the ball as if rolling over the ground it just crossed, so its markings move with it.</summary>
    private void Roll(Vector3 moved)
    {
        moved.Y = 0;
        float distance = moved.Length();
        if (distance < 1e-5f) return;
        var axis = Vector3.Up.Cross(moved / distance);
        float turn = Mathf.Min(distance / BallRadius, MaxTurnPerFrame);
        _ball.Quaternion = (new Quaternion(axis, turn) * _ball.Quaternion).Normalized();
    }

    private void BuildMarkers(Shot shot)
    {
        var rest = ToGodot(shot.Flight.Points[^1]);
        var ringMat = new StandardMaterial3D { AlbedoColor = Current, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        shot.Markers.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 2.2f, Height = 4.4f },
            Position = rest,
            Layers = MapLayer,
            MaterialOverride = ringMat,
        });
    }

    private static void Recolor(Shot s, Color c)
    {
        foreach (var child in s.Markers.GetChildren())
            if (child is MeshInstance3D m && m.MaterialOverride is StandardMaterial3D mat) mat.AlbedoColor = c;
        s.MapPath.SetMeta("built", false);
    }

    /// <summary>The trail stays solid for this long behind the ball, then fades out over <see cref="TrailFade"/> (seconds).</summary>
    private const double TrailSolid = 1.0, TrailFade = 1.5;
    /// <summary>Once the ball stops, whatever trail is left fades out over this long (seconds).</summary>
    private const double TrailRestFade = 1.0;

    /// <summary>A putt's trail: this high above the green (m), ending this far behind the ball (m).</summary>
    private const float PuttTrailHeight = 0.006f, PuttTrailGap = 0.08f;

    /// <summary>
    /// A camera-facing ribbon following the ball: solid just behind it, the oldest part fading out as the ball moves
    /// on, and the remainder fading away once the ball has stopped. <paramref name="drop"/> lowers it (m) and
    /// <paramref name="gap"/> ends it that far short of the ball (m), for a putt's trail along the green.
    /// </summary>
    private static void BuildTrail(ImmediateMesh mesh, IReadOnlyList<FlightPoint> pts, int upTo, Vector3 head, double now, double idle, Vector3 camPos,
                                   float drop = 0, float gap = 0)
    {
        mesh.ClearSurfaces();
        double rest = 1 - Math.Clamp(idle / TrailRestFade, 0, 1);
        if (rest <= 0) return;

        // Points still visible (age < solid + fade), oldest first, ending at the ball.
        var path = new List<(Vector3 P, double Age)>();
        for (int k = 0; k <= upTo && k < pts.Count; k++)
        {
            double age = now - pts[k].T;
            if (age < TrailSolid + TrailFade) path.Add((ToGodot(pts[k]), age));
        }
        if (path.Count == 0 || path[^1].P.DistanceSquaredTo(head) > 1e-4f) path.Add((head, 0));
        if (drop != 0) for (int k = 0; k < path.Count; k++) path[k] = (path[k].P - new Vector3(0, drop, 0), path[k].Age);
        if (gap > 0) TrimEnd(path, gap);
        if (path.Count < 2) return;

        mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
        for (int k = 0; k < path.Count; k++)
        {
            var (p, age) = path[k];
            var dir = (path[Math.Min(k + 1, path.Count - 1)].P - path[Math.Max(k - 1, 0)].P).Normalized();
            var side = dir.Cross((camPos - p).Normalized());
            if (side.LengthSquared() < 1e-6f) side = Vector3.Right;
            side = side.Normalized() * Mathf.Max(0.015f, camPos.DistanceTo(p) * 0.0022f); // ~constant on-screen width
            double fade = 1 - Math.Clamp((age - TrailSolid) / TrailFade, 0, 1);
            var c = new Color(Current, (float)(Current.A * fade * rest));
            mesh.SurfaceSetColor(c); mesh.SurfaceAddVertex(p + side);
            mesh.SurfaceSetColor(c); mesh.SurfaceAddVertex(p - side);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>Shortens a path by <paramref name="gap"/> metres at its end (the ball's end), cutting its last piece part way.</summary>
    private static void TrimEnd(List<(Vector3 P, double Age)> path, float gap)
    {
        float left = gap;
        while (path.Count >= 2)
        {
            var (end, endAge) = path[^1];
            var (before, beforeAge) = path[^2];
            float len = end.DistanceTo(before);
            if (len > left)
            {
                float k = left / len;
                path[^1] = (end.Lerp(before, k), endAge + (beforeAge - endAge) * k);
                return;
            }
            left -= len;
            path.RemoveAt(path.Count - 1);
        }
        path.Clear();
    }

    private static readonly Color Ghost = new(1, 1, 1, 0.35f);

    /// <summary>The whole path of an earlier shot as a thin, faint camera-facing ribbon.</summary>
    private static void BuildGhost(ImmediateMesh mesh, IReadOnlyList<FlightPoint> pts, Vector3 camPos)
    {
        mesh.ClearSurfaces();
        if (pts.Count < 2) return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
        for (int k = 0; k < pts.Count; k++)
        {
            var p = ToGodot(pts[k]);
            var dir = (ToGodot(pts[Math.Min(k + 1, pts.Count - 1)]) - ToGodot(pts[Math.Max(k - 1, 0)])).Normalized();
            var side = dir.Cross((camPos - p).Normalized());
            if (side.LengthSquared() < 1e-6f) side = Vector3.Right;
            side = side.Normalized() * Mathf.Max(0.01f, camPos.DistanceTo(p) * 0.0014f);
            mesh.SurfaceSetColor(Ghost); mesh.SurfaceAddVertex(p + side);
            mesh.SurfaceSetColor(Ghost); mesh.SurfaceAddVertex(p - side);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>A flat, wide path on the ground for the top-down minimap.</summary>
    private static void BuildGroundPath(ImmediateMesh mesh, IReadOnlyList<FlightPoint> pts, int upTo, Vector3? tip, Color color)
    {
        mesh.ClearSurfaces();
        var path = Path(pts, upTo, tip);
        if (path.Count < 2) return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
        for (int k = 0; k < path.Count; k++)
        {
            var p = path[k] + new Vector3(0, 0.5f, 0);   // just above the ball's path (and so the ground)
            var n = path[Math.Min(k + 1, path.Count - 1)] - path[Math.Max(k - 1, 0)];
            var side = new Vector3(-n.Z, 0, n.X).Normalized() * 0.9f;
            mesh.SurfaceSetColor(color); mesh.SurfaceAddVertex(p + side);
            mesh.SurfaceSetColor(color); mesh.SurfaceAddVertex(p - side);
        }
        mesh.SurfaceEnd();
    }

    private static List<Vector3> Path(IReadOnlyList<FlightPoint> pts, int upTo, Vector3? tip)
    {
        var list = new List<Vector3>(upTo + 2);
        for (int k = 0; k <= upTo && k < pts.Count; k++) list.Add(ToGodot(pts[k]));
        if (tip is { } t && (list.Count == 0 || list[^1].DistanceSquaredTo(t) > 1e-4f)) list.Add(t);
        return list;
    }

    /// <summary>Core: X downrange, Y up, Z right. Godot: -Z downrange, X right.</summary>
    public static Vector3 ToGodot(FlightPoint p) => new((float)p.Z, (float)p.Y + Lift, (float)-p.X);

    private static int IndexAt(IReadOnlyList<FlightPoint> p, double t)
    {
        int i = 0;
        while (i < p.Count - 1 && p[i + 1].T <= t) i++;
        return i;
    }

    private static Vector3 Interpolate(IReadOnlyList<FlightPoint> p, double t)
    {
        int i = IndexAt(p, t);
        if (i >= p.Count - 1) return ToGodot(p[^1]);
        var a = p[i]; var b = p[i + 1];
        float k = (float)((t - a.T) / Math.Max(b.T - a.T, 1e-6));
        return ToGodot(a).Lerp(ToGodot(b), k);
    }
}
