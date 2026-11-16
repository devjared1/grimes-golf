using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// A flagstick: a slightly tapered white pole with a cap, and a triangular pennant that ripples in the wind, standing at
/// its origin. The pole is drawn at its real thickness up close and widened with distance so it never thins to nothing;
/// the flag grows a little with distance so far pins stay readable. <see cref="LayDown"/> puts it on the ground beside the
/// hole (pulled for putting).
/// </summary>
public partial class Flagstick : Node3D
{
    private const float PoleRadius = 0.008f;   // ~5/8 in across at the base
    private const float Height = 2.13f;        // 7 ft
    private const float FlagLength = 0.62f;    // a pennant is about 20 in along and 14 in tall at the pole; a little bigger reads better
    private const float FlagDrop = 0.40f;
    private const int Segments = 12;           // along the flag, so the ripple has something to bend
    /// <summary>How far the flag hangs below the top of the pole (m): the tip of the pole shows above it.</summary>
    private const float FlagInset = 0.05f;

    private readonly Color _color;
    private readonly float _scale;

    /// <summary>Grow the flag with distance (off when the caller already sized it for distance, like range targets).</summary>
    public bool GrowFlag { get; init; } = true;
    private MeshInstance3D _pole = null!;
    private Node3D _flag = null!;
    private ShaderMaterial _cloth = null!;
    private Vector3 _standAt;

    public Flagstick(Color color, float scale = 1)
    {
        _color = color;
        _scale = scale;
    }

    public Flagstick() : this(Colors.Red) { }

    public override void _Ready()
    {
        float h = Height * _scale;
        var white = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.95f, 0.95f), Roughness = 0.45f };
        _pole = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = PoleRadius * 0.55f, BottomRadius = PoleRadius, Height = h, RadialSegments = 8, Rings = 1 },
            Position = new Vector3(0, h / 2, 0),
            MaterialOverride = white,
        };
        // A rounded cap on the top (a child of the pole, so it widens with it).
        _pole.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = PoleRadius * 1.1f, Height = PoleRadius * 2.2f, RadialSegments = 8, Rings = 4 },
            Position = new Vector3(0, h / 2, 0),
            MaterialOverride = white,
        });
        AddChild(_pole);

        // The flag hangs from just under the top of the pole; its node is scaled with distance around that point.
        _cloth = new ShaderMaterial { Shader = ClothShader() };
        _cloth.SetShaderParameter("albedo", _color);
        _cloth.SetShaderParameter("flag_length", FlagLength * _scale);
        _cloth.SetShaderParameter("amplitude", _amplitude * _scale);
        _flag = new Node3D { Position = new Vector3(0, h - FlagInset * _scale, 0), Rotation = new Vector3(0, _pendingYaw, 0) };
        _flag.AddChild(new MeshInstance3D { Mesh = Pennant(_scale), MaterialOverride = _cloth, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(_flag);
        _standAt = Position;
    }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        float d = cam.GlobalPosition.DistanceTo(GlobalPosition);
        // Keep the pole ~2-3 px wide however far away it is.
        float widen = Mathf.Max(1f, d * 0.0012f / PoleRadius);
        _pole.Scale = new Vector3(widen, 1, widen);
        if (GrowFlag) _flag.Scale = Vector3.One * Mathf.Max(1f, d / 90f);
    }

    /// <summary>Turns the flag to stream along <paramref name="downwind"/> (a horizontal world direction; null = calm: it hangs slacker).</summary>
    public void SetWind(Vector3? downwind)
    {
        // The flag hangs along the pole's local +X; turning it about the pole by θ points it at (cos θ, 0, −sin θ).
        float yaw = downwind is { } d ? Mathf.Atan2(-d.Z, d.X) : 0;
        _amplitude = downwind == null ? CalmRipple : WindRipple;
        if (_flag != null)
        {
            _flag.Rotation = new Vector3(0, yaw, 0);
            _cloth.SetShaderParameter("amplitude", _amplitude * _scale);
        }
        else _pendingYaw = yaw;
    }

    private float _pendingYaw;
    /// <summary>How far the flag's tip swings sideways (m): a little in calm air, more in wind.</summary>
    private float _amplitude = WindRipple;
    private const float CalmRipple = 0.03f, WindRipple = 0.075f;

    /// <summary>Back in the hole.</summary>
    public void Stand()
    {
        Basis = Basis.Identity;
        Position = _standAt;
    }

    /// <summary>Lying on the ground, base at <paramref name="baseAt"/>, pointing along <paramref name="along"/> (may follow a slope).</summary>
    public void LayDown(Vector3 baseAt, Vector3 along)
    {
        along = along.Normalized();
        var side = along.Cross(Vector3.Up).Normalized();
        Basis = new Basis(side, along, side.Cross(along).Normalized());
        Position = baseAt + new Vector3(0, PoleRadius, 0);
    }

    // ---- the flag cloth ---------------------------------------------------------------------------

    private static readonly Dictionary<float, ArrayMesh> PennantsByScale = new();

    /// <summary>A triangular pennant hanging along +X from the pole at x = 0: top edge level, the lower edge rising to a point at the tip.</summary>
    private static ArrayMesh Pennant(float scale)
    {
        if (PennantsByScale.TryGetValue(scale, out var cached)) return cached;
        int n = Segments;
        var vertices = new Vector3[(n + 1) * 2];
        var normals = new Vector3[(n + 1) * 2];
        var uvs = new Vector2[(n + 1) * 2];
        var indices = new List<int>();
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float x = FlagLength * scale * t;
            // Row 2i is the top edge, 2i + 1 the lower edge; both meet at the tip.
            vertices[2 * i] = new Vector3(x, 0, 0);
            vertices[2 * i + 1] = new Vector3(x, -FlagDrop * scale * (1 - t), 0);
            normals[2 * i] = normals[2 * i + 1] = Vector3.Back;
            uvs[2 * i] = new Vector2(t, 0);
            uvs[2 * i + 1] = new Vector2(t, 1);
            if (i == n) break;
            int a = 2 * i, b = a + 1, c = a + 2, d = a + 3;
            indices.AddRange(new[] { a, b, c, b, d, c });
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        PennantsByScale[scale] = mesh;
        return mesh;
    }

    private static Shader? _clothShader;

    /// <summary>
    /// Cloth that ripples along its length (nothing at the pole, most at the free tip), each flag out of step with the
    /// next; the normals follow the ripple, so the folds catch the light. Both sides are drawn.
    /// </summary>
    private static Shader ClothShader() => _clothShader ??= new Shader
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled;

uniform vec4 albedo : source_color = vec4(0.86, 0.12, 0.12, 1.0);
uniform float flag_length = 0.62;
uniform float amplitude = 0.075;
uniform float wavelength = 0.34;
uniform float speed = 5.0;

void vertex() {
    float k = 6.2831853 / wavelength;
    float s = clamp(VERTEX.x / flag_length, 0.0, 1.0);
    // Out of step with other flags: from where this one stands.
    float phase = MODEL_MATRIX[3].x * 1.7 + MODEL_MATRIX[3].z * 2.3;
    float arg = k * VERTEX.x - speed * TIME + phase;
    VERTEX.z += amplitude * s * sin(arg);
    // The slack lower edge flutters a little more than the top.
    VERTEX.z += amplitude * 0.35 * s * UV.y * sin(arg * 1.7 + 1.3);
    float slope = amplitude * (sin(arg) / flag_length + s * k * cos(arg));
    NORMAL = normalize(vec3(-slope, 0.0, 1.0));
}

void fragment() {
    ALBEDO = albedo.rgb;
    ROUGHNESS = 0.85;
    EMISSION = albedo.rgb * 0.22;
}
",
    };
}
