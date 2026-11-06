using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// What a golf ball looks like: a glossy urethane cover, dimples and a printed alignment line, all worked out in the
/// shader from the point on the sphere, so there is no texture to run out of resolution. Dimples and the line fade
/// out once they are smaller than a pixel (they would only shimmer), leaving a clean lit ball at a distance.
/// </summary>
public static class BallLook
{
    private static readonly Shader BallShader = new()
    {
        Code = """
        shader_type spatial;
        uniform vec3 tint : source_color = vec3(0.93, 0.93, 0.9);
        uniform float glow_near = 0.0;   // how much it glows up close (fraction of its colour)
        uniform float glow_far = 0.25;   // and from far away, so it can still be found against the grass and sky
        const float DIMPLES = 336.0;
        const float DIMPLE_RADIUS = 0.092;   // on the unit sphere: just short of touching, leaving a thin land between
        const float DIMPLE_SLOPE = 0.55;
        const float LINE_WIDTH = 0.014;      // half width
        const float LINE_LENGTH = 0.6;       // half length (radians around the ball)
        const float GOLDEN = 1.61803398875;
        varying vec3 on_ball;
        void vertex() { on_ball = VERTEX; }
        // The closest point of a spherical Fibonacci lattice to p (a unit vector), without searching all of them
        // (Keinert et al. 2015, "Spherical Fibonacci Mapping"). The lattice packs dimples evenly, as on a real ball.
        vec3 nearest_dimple(vec3 p) {
            float m = 1.0 - 1.0 / DIMPLES;
            float k = max(2.0, floor(log(DIMPLES * PI * sqrt(5.0) * (1.0 - p.z * p.z)) / log(GOLDEN + 1.0)));
            float fk = pow(GOLDEN, k) / sqrt(5.0);
            vec2 f = vec2(round(fk), round(fk * GOLDEN));
            vec2 ka = 2.0 * f / DIMPLES;
            vec2 kb = TAU * (fract((f + 1.0) * GOLDEN) - (GOLDEN - 1.0));
            mat2 inv = mat2(vec2(ka.y, -ka.x), vec2(kb.y, -kb.x)) / (ka.y * kb.x - ka.x * kb.y);
            vec2 cell = floor(inv * vec2(min(atan(p.y, p.x), PI), p.z - m));
            float best = 8.0;
            vec3 nearest = p;
            for (int s = 0; s < 4; s++) {
                float i = clamp(dot(f, vec2(float(s % 2), float(s / 2)) + cell), 0.0, DIMPLES - 1.0);
                float angle = TAU * fract(i * GOLDEN);
                float z = m - 2.0 * i / DIMPLES;
                float ring = sqrt(max(1.0 - z * z, 0.0));
                vec3 q = vec3(cos(angle) * ring, sin(angle) * ring, z);
                float d = dot(q - p, q - p);
                if (d < best) { best = d; nearest = q; }
            }
            return nearest;
        }
        void fragment() {
            vec3 p = normalize(on_ball);
            float pixel = length(fwidth(p));   // the size of this pixel on the unit sphere
            float show = 1.0 - smoothstep(DIMPLE_RADIUS * 0.3, DIMPLE_RADIUS * 1.1, pixel);

            // A dimple is a shallow bowl: its wall tilts the surface toward its centre, more toward the rim.
            vec3 away = p - nearest_dimple(p);
            float w = length(away) / DIMPLE_RADIUS;
            float inside = 1.0 - smoothstep(0.82, 1.0, w);
            vec3 side = away - dot(away, p) * p;
            vec3 normal = normalize(p - side / DIMPLE_RADIUS * DIMPLE_SLOPE * inside * show);
            NORMAL = normalize(mat3(VIEW_MATRIX) * mat3(MODEL_MATRIX) * normal);

            // The alignment line, thinned by how much of the pixel it really covers.
            float line = min(2.0 * LINE_WIDTH / max(pixel, 1e-4), 1.0)
                * (1.0 - smoothstep(LINE_WIDTH, LINE_WIDTH + pixel, abs(p.y)))
                * (1.0 - smoothstep(LINE_LENGTH, LINE_LENGTH + pixel, abs(atan(p.x, p.z))));

            float pit = inside * show;
            ALBEDO = mix(tint * (1.0 - 0.1 * pit), vec3(0.03), line);
            AO = 1.0 - 0.35 * pit;
            ROUGHNESS = mix(0.3, 0.42, pit);
            SPECULAR = 0.5;
            CLEARCOAT = 1.0;
            CLEARCOAT_ROUGHNESS = 0.12;
            EMISSION = tint * mix(glow_near, glow_far, smoothstep(14.0, 60.0, length(VERTEX)));
        }
        """,
    };

    /// <summary>A sphere smooth enough for its outline to stay round from close up.</summary>
    public static SphereMesh Mesh(float radius) => new() { Radius = radius, Height = radius * 2, RadialSegments = 48, Rings = 24 };

    /// <summary>The ball's material: white by default, with a glow that mostly comes in from far away (<paramref name="glowNear"/> sets how much up close).</summary>
    public static ShaderMaterial Material(Color? tint = null, float glowNear = 0.06f)
    {
        var material = new ShaderMaterial { Shader = BallShader };
        if (tint is { } color) material.SetShaderParameter("tint", color);
        material.SetShaderParameter("glow_near", glowNear);
        material.SetShaderParameter("glow_far", Mathf.Max(glowNear, 0.25f));
        return material;
    }

    /// <summary>
    /// A soft dark patch projected onto the ground under the ball. The sun's shadow map is too coarse for something
    /// this small, so without it the ball looks like it floats. Being a decal it follows slopes, and it has nothing to
    /// land on once the ball leaves the ground. It only touches layer 1, so keep the ball itself on another layer.
    /// </summary>
    public static Decal ContactShadow() => new()
    {
        TextureAlbedo = new GradientTexture2D
        {
            Width = 64, Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1, 0.5f),
            Gradient = new Gradient
            {
                Offsets = new[] { 0f, 0.45f, 1f },
                Colors = new[] { new Color(0, 0, 0, 0.7f), new Color(0, 0, 0, 0.5f), new Color(0, 0, 0, 0) },
            },
        },
        CullMask = 1,
    };
}
