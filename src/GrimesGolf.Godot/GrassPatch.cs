using Godot;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// Grass blades around the resting ball, so its lie reads up close: tufts as tall as the cut they grow in (none on
/// greens, sand or water), swaying a little in the wind and thinning out toward the edge of the patch so they blend
/// into the ground's texture. Regrown wherever the ball comes to rest.
/// </summary>
public partial class GrassPatch : MultiMeshInstance3D
{
    /// <summary>Blades reach this far from the ball (m), at full density within <see cref="FullUntil"/>.</summary>
    public const float Radius = 10f, FullUntil = 5.5f;

    private const string ShaderCode = """
        shader_type spatial;
        render_mode cull_disabled;
        varying vec3 tint;
        varying float tip;
        varying vec3 world_pos;
        """ + Scenery.CloudGlsl + """
        void vertex() {
            tip = UV.y;
            tint = INSTANCE_CUSTOM.rgb;
            // Gusts roll across the patch; each tuft also flutters on its own.
            vec3 root = MODEL_MATRIX[3].xyz;
            float gust = sin(TIME * 1.3 + root.x * 0.35 + root.z * 0.21) * 0.6 + sin(TIME * 3.1 + INSTANCE_CUSTOM.a * 6.2832) * 0.4;
            VERTEX.x += gust * 0.1 * tip * tip;
            VERTEX.z += gust * 0.04 * tip * tip;
            // Lit mostly like the ground they grow from, so tufts don't flash as their faces turn.
            NORMAL = normalize(mix(NORMAL, vec3(0.0, 1.0, 0.0), 0.7));
            world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        }
        void fragment() {
            // Both sides of a blade face up-ish (the back face's normal would otherwise point into the ground).
            if (!FRONT_FACING) NORMAL = -NORMAL;
            ALBEDO = tint * mix(0.65, 1.3, tip * tip) * cloud_light(world_pos, TIME, PROJECTION_MATRIX);
            BACKLIGHT = tint * 0.3;
            ROUGHNESS = 0.95;
            SPECULAR = 0.15;
        }
        """;

    /// <summary>How each cut grows: tufts per m², blade height (m) and its two colours (as the ground shader's).</summary>
    private static (float Density, float Height, Color A, Color B) Cut(Lie lie) => lie switch
    {
        Lie.DeepRough or Lie.OutOfBounds => (110, 0.11f, Scenery.DeepRough, Scenery.DeepRoughAlt),
        Lie.Rough => (130, 0.065f, Scenery.Rough, Scenery.RoughAlt),
        Lie.Fringe => (150, 0.018f, Scenery.Fairway, Scenery.FairwayAlt),
        Lie.Fairway or Lie.Tee => (130, 0.014f, Scenery.Fairway, Scenery.FairwayAlt),
        _ => (0, 0, default, default),
    };

    private const float MaxDensity = 150;

    public GrassPatch()
    {
        CastShadow = ShadowCastingSetting.Off;
        Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = BuildTuft(),
        };
        MaterialOverride = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
    }

    /// <summary>
    /// Grows the patch around <paramref name="ball"/>. <paramref name="lieAt"/> says which cut each spot is, and
    /// <paramref name="groundAt"/> where its surface is (given the spot and its lie).
    /// </summary>
    public void Grow(P2 ball, Func<P2, Lie> lieAt, Func<P2, Lie, float> groundAt)
    {
        // Lies looked up once per 25 cm cell rather than per tuft (finer than the ground's 1 m coverage texture).
        const double cell = 0.25;
        int n = (int)Math.Ceiling(2 * Radius / cell) + 1;
        var lies = new Lie[n * n];
        var corner = ball - new P2(Radius, Radius);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                lies[i * n + j] = lieAt(corner + new P2((i + 0.5) * cell, (j + 0.5) * cell));

        // Same spot, same grass: the seed comes from the position.
        var rng = new Random(HashCode.Combine(Math.Round(ball.X, 2), Math.Round(ball.Z, 2)));
        int candidates = (int)(Mathf.Pi * Radius * Radius * MaxDensity);
        // Per tuft: a 3×4 transform (rows of basis | origin), then custom data (linear colour, sway phase).
        const int stride = 16;
        var buffer = new float[candidates * stride];
        int count = 0;
        for (int k = 0; k < candidates; k++)
        {
            // Uniform over the disc.
            double r = Radius * Math.Sqrt(rng.NextDouble()), a = rng.NextDouble() * Math.Tau;
            var p = ball + new P2(r * Math.Cos(a), r * Math.Sin(a));
            double roll = rng.NextDouble(), yaw = rng.NextDouble() * Math.Tau, size = 0.7 + rng.NextDouble() * 0.6;
            float shade = (float)rng.NextDouble(), phase = (float)rng.NextDouble();
            int ci = Math.Clamp((int)((p.X - corner.X) / cell), 0, n - 1), cj = Math.Clamp((int)((p.Z - corner.Z) / cell), 0, n - 1);
            var lie = lies[ci * n + cj];
            var (density, height, ca, cb) = Cut(lie);
            float edge = 1 - Mathf.SmoothStep(FullUntil, Radius, (float)r);
            if (roll >= density / MaxDensity * edge) continue;

            float h = height * (float)size * (0.55f + 0.45f * edge);
            // Wide enough to read on screen even for short grass; tufts sit a few mm into the ground.
            float w = Mathf.Max(h * 1.3f, 0.08f);
            float cos = (float)Math.Cos(yaw), sin = (float)Math.Sin(yaw);
            var at = Scenery.ToGodot(p, groundAt(p, lie) - 0.004f);
            var c = ca.Lerp(cb, shade).SrgbToLinear() * (0.8f + 0.4f * phase);
            int o = count++ * stride;
            // Turned about Y by yaw, scaled (w, h, w).
            buffer[o + 0] = cos * w; buffer[o + 1] = 0; buffer[o + 2] = sin * w; buffer[o + 3] = at.X;
            buffer[o + 4] = 0; buffer[o + 5] = h; buffer[o + 6] = 0; buffer[o + 7] = at.Y;
            buffer[o + 8] = -sin * w; buffer[o + 9] = 0; buffer[o + 10] = cos * w; buffer[o + 11] = at.Z;
            buffer[o + 12] = c.R; buffer[o + 13] = c.G; buffer[o + 14] = c.B; buffer[o + 15] = phase;
        }

        Multimesh.InstanceCount = count;
        if (count > 0) Multimesh.Buffer = buffer.AsSpan(0, count * stride).ToArray();
    }

    /// <summary>
    /// One tuft, 1 unit tall: a few tapering, curving blades around its root (five of three segments each here;
    /// <see cref="GrassField"/> uses a cheaper one). UV.y runs from 0 at the root to 1 at the tip.
    /// </summary>
    public static ArrayMesh BuildTuft(int blades = 5, int segments = 3)
    {
        var rng = new Random(11);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int b = 0; b < blades; b++)
        {
            float face = (float)(rng.NextDouble() * Math.Tau), at = (float)(rng.NextDouble() * Math.Tau);
            var root = new Vector3(Mathf.Cos(at), 0, Mathf.Sin(at)) * (float)(rng.NextDouble() * 0.3);
            var across = new Vector3(Mathf.Cos(face), 0, Mathf.Sin(face));
            var lean = new Vector3(-across.Z, 0, across.X) * (float)(0.15 + rng.NextDouble() * 0.35);
            float tall = 0.65f + (float)rng.NextDouble() * 0.35f, width = 0.06f;
            var normal = across.Cross(Vector3.Up).Normalized();

            Vector3 Spine(float t) => root + lean * (t * t) + new Vector3(0, t * tall, 0);
            for (int s = 0; s < segments; s++)
            {
                float t0 = (float)s / segments, t1 = (float)(s + 1) / segments;
                Vector3 c0 = Spine(t0), c1 = Spine(t1);
                Vector3 l0 = c0 - across * width * (1 - t0), r0 = c0 + across * width * (1 - t0);
                Vector3 l1 = c1 - across * width * (1 - t1), r1 = c1 + across * width * (1 - t1);
                void V(Vector3 v, float u, float t) { st.SetNormal(normal); st.SetUV(new Vector2(u, t)); st.AddVertex(v); }
                if (s == segments - 1)
                {
                    V(l0, 0, t0); V(c1, 0.5f, t1); V(r0, 1, t0);   // pointed tip
                }
                else
                {
                    V(l0, 0, t0); V(l1, 0, t1); V(r0, 1, t0);
                    V(r0, 1, t0); V(l1, 0, t1); V(r1, 1, t1);
                }
            }
        }
        return st.Commit();
    }
}
