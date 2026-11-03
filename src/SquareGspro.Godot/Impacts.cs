using Godot;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>
/// What flies up where a shot first lands: a spray of sand from a bunker, a splash and a spreading ring on water,
/// bits of turf and a little dust from grass. One-shot particles that free themselves when they're done.
/// </summary>
public static class Impacts
{
    private const string RingShader = """
        shader_type spatial;
        render_mode unshaded, blend_mix, cull_disabled, depth_draw_never;
        uniform float life = 0.0;   // 0 → 1 over the ring's time
        void fragment() {
            float r = length(UV - 0.5) * 2.0;
            float front = 0.55 + 0.4 * life;
            float ring = smoothstep(0.1, 0.0, abs(r - front)) + 0.5 * smoothstep(0.08, 0.0, abs(r - front * 0.6));
            ALBEDO = vec3(0.85, 0.9, 0.9);
            ALPHA = ring * (1.0 - life) * 0.7;
        }
        """;

    private static StandardMaterial3D? _bits;
    private static ShaderMaterial? _puff;

    /// <summary>Small solid flecks, coloured per particle.</summary>
    private static StandardMaterial3D Bits => _bits ??= new()
    {
        VertexColorUseAsAlbedo = true,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
        BillboardKeepScale = true,
        Roughness = 1,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private const string PuffShader = """
        shader_type spatial;
        render_mode unshaded, blend_mix, cull_disabled, depth_draw_never;
        void vertex() {
            // Face the camera, keeping the particle's own scale (as a particle billboard does).
            mat4 world = mat4(normalize(INV_VIEW_MATRIX[0]), normalize(INV_VIEW_MATRIX[1]), normalize(INV_VIEW_MATRIX[2]), MODEL_MATRIX[3]);
            MODELVIEW_MATRIX = VIEW_MATRIX * world * mat4(vec4(length(MODEL_MATRIX[0].xyz), 0, 0, 0), vec4(0, length(MODEL_MATRIX[1].xyz), 0, 0),
                                                         vec4(0, 0, length(MODEL_MATRIX[2].xyz), 0), vec4(0, 0, 0, 1));
        }
        void fragment() {
            float r = length(UV - 0.5) * 2.0;
            ALBEDO = COLOR.rgb;
            ALPHA = COLOR.a * pow(clamp(1.0 - r, 0.0, 1.0), 1.5);
        }
        """;

    /// <summary>Soft round puffs (dust, spray) that fade with their colour's alpha.</summary>
    private static ShaderMaterial Puff => _puff ??= new() { Shader = new Shader { Code = PuffShader } };

    /// <summary>
    /// The burst for a ball landing at <paramref name="at"/> (on the ground) on <paramref name="lie"/>, travelling
    /// along <paramref name="dir"/> (horizontal unit vector) and coming down at <paramref name="fall"/> m/s.
    /// </summary>
    public static void Land(Node3D parent, Vector3 at, Lie lie, Vector3 dir, float fall)
    {
        // Harder landings throw more and higher; a chip barely disturbs anything.
        float force = Mathf.Clamp(fall / 18f, 0.25f, 1.3f);
        switch (lie)
        {
            case Lie.Bunker:
                Burst(parent, at, dir, Bits, (int)(120 * force), 0.045f, 0.09f, 3f + 4f * force, 30, 1.4f,
                      new Color(0.86f, 0.78f, 0.58f), new Color(0.72f, 0.63f, 0.44f));
                Burst(parent, at, dir, Puff, 14, 0.7f, 1.5f, 1f + 1.5f * force, 45, 2.4f,
                      new Color(0.88f, 0.82f, 0.68f, 0.8f), new Color(0.88f, 0.82f, 0.68f, 0.5f), gravity: -0.03f, fade: true);
                break;
            case Lie.Water:
                Burst(parent, at, dir, Bits, (int)(110 * force), 0.04f, 0.08f, 4f + 5f * force, 14, 1.3f,
                      new Color(0.92f, 0.96f, 0.98f), new Color(0.75f, 0.85f, 0.9f));
                Burst(parent, at, dir, Puff, 16, 0.5f, 1.0f, 2.5f + 3f * force, 18, 1.1f,
                      new Color(1, 1, 1, 0.85f), new Color(0.9f, 0.95f, 1f, 0.55f), gravity: 0.6f, fade: true);
                Ring(parent, at + new Vector3(0, 0.03f, 0), 2.2f + 2f * force, 1.6f);
                break;
            case Lie.CartPath:
                Burst(parent, at, dir, Puff, 5, 0.2f, 0.45f, 0.5f, 60, 1.0f,
                      new Color(0.8f, 0.78f, 0.72f, 0.4f), new Color(0.8f, 0.78f, 0.72f, 0.2f), gravity: -0.03f, fade: true);
                break;
            case Lie.Green:
                // Close-mown: a few beads of dew and a fleck or two.
                Burst(parent, at, dir, Bits, (int)(14 * force), 0.02f, 0.04f, 1.2f + 1.5f * force, 30, 0.7f,
                      new Color(0.85f, 0.92f, 0.95f), new Color(0.45f, 0.7f, 0.35f));
                break;
            default:
                // Grass: clippings and bits of soil, more from longer grass.
                bool longGrass = lie is Lie.Rough or Lie.DeepRough or Lie.OutOfBounds;
                Burst(parent, at, dir, Bits, (int)((longGrass ? 50 : 30) * force), 0.035f, 0.07f, 1.8f + 2.5f * force, 28, 1.0f,
                      new Color(0.36f, 0.6f, 0.22f), new Color(0.38f, 0.3f, 0.2f));
                Burst(parent, at, dir, Puff, 4, 0.15f, 0.3f, 0.6f, 60, 1.0f,
                      new Color(0.55f, 0.5f, 0.4f, 0.3f), new Color(0.55f, 0.5f, 0.4f, 0.15f), gravity: -0.03f, fade: true);
                break;
        }
    }

    /// <summary>
    /// <paramref name="count"/> particles thrown up from <paramref name="at"/>, leaning a little along <paramref name="dir"/>,
    /// in a cone <paramref name="spread"/> degrees wide, sized and coloured between the two values given.
    /// </summary>
    private static void Burst(Node3D parent, Vector3 at, Vector3 dir, Material material, int count, float minSize, float maxSize,
        float speed, float spread, float life, Color a, Color b, float gravity = 1f, bool fade = false)
    {
        if (count <= 0) return;
        var p = new CpuParticles3D
        {
            Emitting = false,
            OneShot = true,
            Explosiveness = 0.95f,
            Amount = count,
            Lifetime = life,
            Mesh = new QuadMesh { Size = Vector2.One, Material = material },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Direction = (Vector3.Up * 2f + dir * 0.6f).Normalized(),
            Spread = spread,
            InitialVelocityMin = speed * 0.5f,
            InitialVelocityMax = speed,
            Gravity = new Vector3(0, -9.8f * gravity, 0),
            DampingMin = fade ? 1.5f : 0.2f,
            DampingMax = fade ? 2.5f : 0.6f,
            ScaleAmountMin = minSize,
            ScaleAmountMax = maxSize,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.08f,
            ColorInitialRamp = new Gradient { Colors = new[] { a, b }, Offsets = new[] { 0f, 1f } },
        };
        if (fade)
        {
            // Puffs swell as they drift and fade out.
            p.ScaleAmountCurve = new Curve();
            p.ScaleAmountCurve.AddPoint(new Vector2(0, 0.4f));
            p.ScaleAmountCurve.AddPoint(new Vector2(1, 1.6f));
            p.ColorRamp = new Gradient { Colors = new[] { Colors.White, new Color(1, 1, 1, 0) }, Offsets = new[] { 0f, 1f } };
        }
        parent.AddChild(p);
        p.GlobalPosition = at + new Vector3(0, 0.1f, 0);
        p.Restart();
        p.Finished += p.QueueFree;
    }

    /// <summary>A ring spreading out on the water to <paramref name="radius"/> metres over <paramref name="seconds"/>.</summary>
    private static void Ring(Node3D parent, Vector3 at, float radius, float seconds)
    {
        var material = new ShaderMaterial { Shader = new Shader { Code = RingShader } };
        var ring = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = Vector2.One * 2 },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Scale = Vector3.One * 0.3f,
        };
        parent.AddChild(ring);
        ring.GlobalPosition = at;
        var tween = ring.CreateTween().SetParallel();
        tween.TweenProperty(ring, "scale", Vector3.One * radius, seconds).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenMethod(Callable.From<float>(t => material.SetShaderParameter("life", t)), 0f, 1f, seconds);
        tween.Chain().TweenCallback(Callable.From(ring.QueueFree));
    }
}
