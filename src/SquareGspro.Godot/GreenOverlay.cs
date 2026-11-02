using Godot;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>
/// Green-reading aids laid on one putting surface, squared to the line of play:
/// <list type="bullet">
/// <item>a heat map of the green's height (blue the low side, red the high side, relative to this green's own relief);</item>
/// <item>a 1 m grid draped over the surface, with a dot on every grid segment rolling downhill along it, faster where
///   it's steeper (and still where the segment is flat). The dots are animated in their shader, so they cost nothing per frame.</item>
/// </list>
/// </summary>
public partial class GreenOverlay : Node3D
{
    /// <summary>Grid spacing (m).</summary>
    private const float Spacing = 1f;
    /// <summary>Heat map cell size (m).</summary>
    private const float HeatCell = 0.5f;
    /// <summary>Heights the layers are drawn above the surface (m), so they never z-fight with it.</summary>
    private const float HeatLift = 0.03f, LineLift = 0.035f, DotLift = 0.06f;
    /// <summary>Thin enough not to crowd the ball from the putting camera (dots a little smaller than the ball).</summary>
    private const float LineWidth = 0.018f, DotRadius = 0.026f;
    /// <summary>Dot speed (m/s) per unit slope along the segment, and its limits; below <see cref="FlatSlope"/> a dot stays put.</summary>
    private const float SpeedPerSlope = 25f, MinSpeed = 0.08f, MaxSpeed = 1.5f, FlatSlope = 0.002f;
    /// <summary>Height range (m) that spans the full colour ramp at least, so a nearly flat green doesn't look dramatic.</summary>
    private const float MinRelief = 0.2f;

    private const string DotShader = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, shadows_disabled;
        uniform vec4 tint : source_color;
        varying float fade;
        float hash(float n) { return fract(sin(n * 12.9898) * 43758.5453); }
        void vertex() {
            // INSTANCE_CUSTOM: xyz = the segment from its high end (the instance origin) to its low end, w = trips per second.
            float u = fract(TIME * INSTANCE_CUSTOM.w + hash(float(INSTANCE_ID)));
            VERTEX += INSTANCE_CUSTOM.xyz * u;
            fade = INSTANCE_CUSTOM.w > 0.0 ? sin(3.14159 * u) : 1.0;
        }
        void fragment() { ALBEDO = tint.rgb; ALPHA = tint.a * clamp(fade * 1.6, 0.0, 1.0); }
        """;

    private readonly Shape _green;
    private readonly Func<P2, float> _height;
    private readonly P2 _along, _across, _center;
    private readonly float _reach;
    private MeshInstance3D _heat = null!;
    private Node3D _grid = null!;

    /// <param name="green">The putting surface.</param>
    /// <param name="height">Height of the putting surface at a point.</param>
    /// <param name="lineOfPlay">Direction the grid is squared to (e.g. tee to pin).</param>
    public GreenOverlay(Shape green, Func<P2, float> height, P2 lineOfPlay)
    {
        _green = green;
        _height = height;
        _along = lineOfPlay.Length > 1e-6 ? lineOfPlay * (1 / lineOfPlay.Length) : new P2(1, 0);
        _across = new P2(-_along.Z, _along.X);
        var box = green.Box;
        _center = box.Center;
        _reach = (float)(box.Max.DistanceTo(box.Min) / 2) + Spacing;
    }

    // Godot needs a parameterless constructor for scripts it may instantiate itself.
    public GreenOverlay() : this(new Ellipse(new P2(0, 0), 10, 10), _ => 0, new P2(1, 0)) { }

    public bool HeatMapVisible { get => _heat.Visible; set => _heat.Visible = value; }
    public bool GridVisible { get => _grid.Visible; set => _grid.Visible = value; }

    public override void _Ready()
    {
        _heat = new MeshInstance3D { Mesh = BuildHeatMap(), MaterialOverride = Scenery.Overlay(0.55f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_heat);
        // The grid is too fine for the minimap: main camera only (the heat map shows on both).
        _grid = new Node3D();
        _grid.AddChild(new MeshInstance3D { Mesh = BuildLines(), MaterialOverride = Scenery.Overlay(0.6f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Layers = ShotPlayer.TrailLayer });
        var dots = BuildDots();
        dots.Layers = ShotPlayer.TrailLayer;
        _grid.AddChild(dots);
        AddChild(_grid);
    }

    /// <summary>Course point at (<paramref name="u"/> along the line of play, <paramref name="v"/> across it) from the green's centre.</summary>
    private P2 At(float u, float v) => _center + _along * u + _across * v;

    private Vector3 On(P2 p, float lift) => Scenery.ToGodot(p, _height(p) + lift);

    // ---- heat map --------------------------------------------------------------------------------

    private ArrayMesh BuildHeatMap()
    {
        int n = (int)Math.Ceiling(_reach / HeatCell);
        // Heights at the cell corners (shared by neighbouring cells), and which cells are on the green.
        var h = new float[2 * n + 1, 2 * n + 1];
        float lo = float.MaxValue, hi = float.MinValue;
        for (int a = 0; a <= 2 * n; a++)
            for (int b = 0; b <= 2 * n; b++)
            {
                var p = At((a - n) * HeatCell, (b - n) * HeatCell);
                h[a, b] = _height(p);
                if (_green.Contains(p)) { lo = Math.Min(lo, h[a, b]); hi = Math.Max(hi, h[a, b]); }
            }
        if (lo > hi) lo = hi = 0;
        float mid = (lo + hi) / 2, half = Math.Max(hi - lo, MinRelief) / 2;

        var verts = new List<Vector3>();
        var colors = new List<Color>();
        var indices = new List<int>();
        var corner = new Dictionary<(int, int), int>();
        int Vertex(int a, int b)
        {
            if (corner.TryGetValue((a, b), out int i)) return i;
            var p = At((a - n) * HeatCell, (b - n) * HeatCell);
            verts.Add(Scenery.ToGodot(p, h[a, b] + HeatLift));
            // Corners off the green are transparent, so the edge fades out over a cell instead of stair-stepping.
            colors.Add(new Color(Ramp((h[a, b] - mid) / half), _green.Contains(p) ? 1 : 0));
            corner[(a, b)] = verts.Count - 1;
            return verts.Count - 1;
        }
        bool OnGreen(int a, int b) => _green.Contains(At((a - n) * HeatCell, (b - n) * HeatCell));
        for (int a = 0; a < 2 * n; a++)
            for (int b = 0; b < 2 * n; b++)
            {
                if (!OnGreen(a, b) && !OnGreen(a + 1, b) && !OnGreen(a, b + 1) && !OnGreen(a + 1, b + 1)) continue;
                int k00 = Vertex(a, b), k10 = Vertex(a + 1, b), k01 = Vertex(a, b + 1), k11 = Vertex(a + 1, b + 1);
                indices.AddRange(new[] { k00, k10, k11, k00, k11, k01 });
            }
        return Surface(verts, colors, indices);
    }

    /// <summary>Low to high: blue, cyan, green, yellow, red (<paramref name="t"/> from −1 to 1).</summary>
    private static Color Ramp(float t)
    {
        Color[] stops = { new(0.15f, 0.35f, 0.95f), new(0.10f, 0.80f, 0.95f), new(0.25f, 0.85f, 0.30f), new(0.98f, 0.88f, 0.15f), new(0.95f, 0.22f, 0.15f) };
        float x = Math.Clamp((t + 1) / 2, 0, 1) * (stops.Length - 1);
        int i = Math.Min((int)x, stops.Length - 2);
        return stops[i].Lerp(stops[i + 1], x - i);
    }

    // ---- grid ------------------------------------------------------------------------------------

    /// <summary>The grid lines, draped over the surface in short pieces and clipped to the green.</summary>
    private ArrayMesh BuildLines()
    {
        const float piece = 0.25f;
        var lineColor = new Color(1, 1, 1, 0.8f);
        var verts = new List<Vector3>();
        var colors = new List<Color>();
        var indices = new List<int>();
        int n = (int)Math.Ceiling(_reach / Spacing);
        foreach (bool alongLines in new[] { true, false })
            for (int k = -n; k <= n; k++)
            {
                // A line at a fixed offset across (or along), walked in short pieces over the other axis.
                P2 Point(float s) => alongLines ? At(s, k * Spacing) : At(k * Spacing, s);
                var side = (alongLines ? _across : _along) * (LineWidth / 2);
                for (float s = -_reach; s < _reach; s += piece)
                {
                    P2 a = Point(s), b = Point(s + piece);
                    if (!_green.Contains(a) || !_green.Contains(b)) continue;
                    int i = verts.Count;
                    verts.Add(On(a + side, LineLift)); verts.Add(On(a - side, LineLift));
                    verts.Add(On(b + side, LineLift)); verts.Add(On(b - side, LineLift));
                    for (int c = 0; c < 4; c++) colors.Add(lineColor);
                    indices.AddRange(new[] { i, i + 2, i + 3, i, i + 3, i + 1 });
                }
            }
        return Surface(verts, colors, indices);
    }

    /// <summary>One dot per grid segment on the green, rolling from its high end to its low end at a speed set by the slope.</summary>
    private MultiMeshInstance3D BuildDots()
    {
        var segments = new List<(Vector3 Start, Vector3 Run, float Rate)>();
        int n = (int)Math.Ceiling(_reach / Spacing);
        for (int a = -n; a <= n; a++)
            for (int b = -n; b <= n; b++)
            {
                var p = At(a * Spacing, b * Spacing);
                if (!_green.Contains(p)) continue;
                foreach (var q in new[] { At((a + 1) * Spacing, b * Spacing), At(a * Spacing, (b + 1) * Spacing) })
                {
                    if (!_green.Contains(q)) continue;
                    Vector3 pv = On(p, DotLift), qv = On(q, DotLift);
                    float slope = (qv.Y - pv.Y) / Spacing;
                    if (Math.Abs(slope) < FlatSlope) { segments.Add(((pv + qv) / 2, Vector3.Zero, 0)); continue; }
                    var (high, low) = slope > 0 ? (qv, pv) : (pv, qv);
                    float speed = Math.Clamp(Math.Abs(slope) * SpeedPerSlope, MinSpeed, MaxSpeed);
                    segments.Add((high, low - high, speed / Spacing));
                }
            }

        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new SphereMesh { Radius = DotRadius, Height = DotRadius * 2, RadialSegments = 10, Rings = 5 },
            InstanceCount = segments.Count,
        };
        for (int i = 0; i < segments.Count; i++)
        {
            var (start, run, rate) = segments[i];
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, start));
            mm.SetInstanceCustomData(i, new Color(run.X, run.Y, run.Z, rate));
        }
        var material = new ShaderMaterial { Shader = new Shader { Code = DotShader } };
        material.SetShaderParameter("tint", new Color(1, 1, 1, 0.95f));
        // The dots move, so their bounds can't come from their rest positions alone.
        return new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 2 };
    }

    private static ArrayMesh Surface(List<Vector3> verts, List<Color> colors, List<int> indices)
    {
        var mesh = new ArrayMesh();
        if (indices.Count == 0) return mesh;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
