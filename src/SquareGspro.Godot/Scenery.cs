using Godot;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>Shared look: sky and sun, grass/sand/water materials, flat ground meshes, trees and flags.</summary>
public static class Scenery
{
    /// <summary>
    /// Shared GLSL for grass and sand photo detail (see assets/CREDITS.md). The photo is divided by its own average
    /// colour, so it adds blade-level variation to each area's colour without changing it. It's sampled twice, the
    /// second time 3.7× larger and turned, so its tiling doesn't show. Needs <c>TURF_VERTEX</c> in vertex() for the
    /// normal map's tangents (UVs are world x/z).
    /// </summary>
    public const string TurfGlsl = """
        uniform sampler2D grass_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D grass_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D sand_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D sand_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
        vec2 turn(vec2 p) { return mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * p; }
        vec3 photo_detail(sampler2D tex, vec2 p, float tile, float strength) {
            vec3 mean = max(textureLod(tex, vec2(0.5), 12.0).rgb, vec3(0.01));
            vec3 d = mix(texture(tex, p / tile).rgb, texture(tex, turn(p) / (tile * 3.7)).rgb, 0.35) / mean;
            // Large soft light and dark patches (the photo blown up and blurred), as real turf has at a distance.
            float patch = dot(textureLod(tex, turn(p) / 41.0, 6.0).rgb / mean, vec3(1.0 / 3.0));
            return max(mix(vec3(1.0), d, strength) * (1.0 + (patch - 1.0) * 1.5), vec3(0.0));
        }
        vec3 photo_bump(sampler2D tex, vec2 p, float tile) {
            return mix(texture(tex, p / tile).rgb, texture(tex, turn(p) / (tile * 3.7)).rgb, 0.35);
        }
        """;

    /// <summary>Vertex-shader lines giving ground meshes tangents along world x and z (the photo detail's UVs).</summary>
    public const string TurfVertex = """
        TANGENT = normalize(cross(NORMAL, vec3(0.0, 0.0, 1.0)));
        BINORMAL = normalize(cross(TANGENT, NORMAL));
        """;

    /// <summary>How a kind of grass shows the photo detail: tile size (m), colour variation (1 = the photo's own; more exaggerates it) and bumpiness.</summary>
    public enum Turf { Rough, Fairway, Green }

    public static (float Tile, float Detail, float Bump) TurfLook(Turf t) => t switch
    {
        Turf.Green => (0.9f, 0.7f, 0.35f),
        Turf.Fairway => (1.5f, 1.3f, 0.8f),
        _ => (2.2f, 1.6f, 1.2f),
    };

    private static Texture2D? _grassAlbedo, _grassNormal, _sandAlbedo, _sandNormal;

    /// <summary>Gives a material using <see cref="TurfGlsl"/> its photo textures (loaded once).</summary>
    public static void SetTurfTextures(ShaderMaterial m)
    {
        _grassAlbedo ??= GD.Load<Texture2D>("res://assets/ground/grass_color.jpg");
        _grassNormal ??= GD.Load<Texture2D>("res://assets/ground/grass_normal.jpg");
        _sandAlbedo ??= GD.Load<Texture2D>("res://assets/ground/sand_color.jpg");
        _sandNormal ??= GD.Load<Texture2D>("res://assets/ground/sand_normal.jpg");
        m.SetShaderParameter("grass_albedo", _grassAlbedo);
        m.SetShaderParameter("grass_normal", _grassNormal);
        m.SetShaderParameter("sand_albedo", _sandAlbedo);
        m.SetShaderParameter("sand_normal", _sandNormal);
    }

    /// <summary>Tile size (m) of the raked-sand photo.</summary>
    public const float SandTile = 3.5f;

    private const string GrassShader = """
        shader_type spatial;
        render_mode cull_disabled;
        uniform vec3 base_color : source_color;
        uniform vec3 alt_color : source_color;
        uniform float stripe_width = 0.0;
        uniform float noise_scale = 0.12;
        uniform float turf_tile = 2.2;
        uniform float turf_detail = 0.9;
        uniform float turf_bump = 1.0;
        // The open cup on the current green: x, z (world) and radius; radius 0 = none. Ground inside it isn't drawn.
        global uniform vec4 cup_hole;
        varying vec3 world_pos;
        """ + TurfGlsl + """
        void vertex() {
            world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        """ + TurfVertex + """
        }
        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
        }
        void fragment() {
            if (cup_hole.w > 0.0 && distance(world_pos.xz, cup_hole.xy) < cup_hole.w) discard;
            float n = noise(world_pos.xz * noise_scale) * 0.6 + noise(world_pos.xz * noise_scale * 5.0) * 0.4;
            vec3 c = mix(base_color, alt_color, n);
            if (stripe_width > 0.0) {
                float s = step(0.5, fract(world_pos.z / (2.0 * stripe_width)));
                c *= mix(0.9, 1.07, s);
            }
            ALBEDO = c * photo_detail(grass_albedo, world_pos.xz, turf_tile, turf_detail);
            NORMAL_MAP = photo_bump(grass_normal, world_pos.xz, turf_tile);
            NORMAL_MAP_DEPTH = turf_bump;
            ROUGHNESS = 0.9;
            SPECULAR = 0.25;
        }
        """;

    public static readonly Color Rough = new(0.22f, 0.42f, 0.17f), RoughAlt = new(0.30f, 0.50f, 0.20f);
    public static readonly Color DeepRough = new(0.12f, 0.25f, 0.09f), DeepRoughAlt = new(0.17f, 0.31f, 0.11f);

    /// <summary>A shape grown by <paramref name="d"/> metres, the same growth <see cref="Hole.Within"/> uses for lies.</summary>
    public static Shape Grown(Shape shape, double d) => shape switch
    {
        Ellipse e => e with { RadiusX = e.RadiusX + d, RadiusZ = e.RadiusZ + d },
        Polygon p => new Polygon(Grow(p.Points, d)),
        Strip s => new Strip(s.Points, s.Width + 2 * d),
        _ => shape,
    };

    /// <summary>
    /// The first cut of rough (the band beyond which is deep rough) around a hole's fairways, mapped tees and greens,
    /// as shapes to draw over the deep-rough ground. See also <see cref="TeeCut"/>.
    /// </summary>
    public static IEnumerable<Shape> FirstCut(Hole h) =>
        h.Fairways.Concat(h.Tees).Select(s => Grown(s, Hole.FirstCutWidth))
            .Concat(h.Greens.Select(g => Grown(g, Hole.FringeWidth + Hole.FirstCutWidth)));

    /// <summary>The first cut around the hole's tee point (which counts as tee within <see cref="Hole.TeeBoxRadius"/>).</summary>
    public static Shape TeeCut(Hole h) => new Ellipse(h.Tee, Hole.TeeBoxRadius + Hole.FirstCutWidth, Hole.TeeBoxRadius + Hole.FirstCutWidth);
    public static readonly Color Fairway = new(0.33f, 0.58f, 0.24f), FairwayAlt = new(0.38f, 0.63f, 0.27f);
    public static readonly Color Green = new(0.42f, 0.70f, 0.30f), GreenAlt = new(0.46f, 0.74f, 0.33f);

    /// <summary>
    /// Photographed sky (a CC0 "pure sky" panorama with real clouds, see assets/CREDITS.md). It also lights the scene:
    /// ambient light and reflections come from it.
    /// </summary>
    private const string SkyPanorama = "res://assets/sky/kloofendal_48d_partly_cloudy_puresky_4k.hdr";

    /// <summary>
    /// The sun in <see cref="SkyPanorama"/> is 48° up; turning the sky half way round puts it behind and to the left of
    /// the range tee (looking down -Z). The directional light shines from the same place, so shadows match the sky.
    /// </summary>
    private static readonly Vector3 SkyRotation = new(0, Mathf.Pi, 0), SunRotationDegrees = new(-48, -34.2f, 0);

    public static void AddSkyAndSun(Node3D parent)
    {
        var sky = new PanoramaSkyMaterial { Panorama = GD.Load<Texture2D>(SkyPanorama), EnergyMultiplier = 1.0f };
        parent.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky, RadianceSize = Sky.RadianceSizeEnum.Size256 },
                SkyRotation = SkyRotation,
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.55f,
                ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
                TonemapMode = Godot.Environment.ToneMapper.Agx,
                TonemapExposure = 0.9f,
                SsaoEnabled = true,
                SsaoRadius = 1.5f,
                SsaoIntensity = 1.6f,
                GlowEnabled = true,
                GlowIntensity = 0.3f,
                GlowBloom = 0.02f,
                // Distance haze tinted by the sky behind it (aerial perspective), not painted over the sky itself.
                FogEnabled = true,
                FogLightColor = new Color(0.74f, 0.82f, 0.90f),
                FogDensity = 0.0006f,
                FogAerialPerspective = 0.6f,
                FogSkyAffect = 0.0f,
                AdjustmentEnabled = true,
                AdjustmentSaturation = 1.15f,
            },
        });
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = SunRotationDegrees,
            LightColor = new Color(1.0f, 0.96f, 0.90f),
            LightEnergy = 1.4f,
            LightAngularDistance = 0.5f,
            ShadowEnabled = true,
            ShadowBlur = 1.2f,
            DirectionalShadowMaxDistance = 250,
        });
    }

    /// <summary>Visual cup: larger than regulation so it reads on screen; the ball is drawn in real proportion to it.</summary>
    public const float CupRadius = 0.085f;
    /// <summary>Height of the green surface (greens are layered just above the other ground).</summary>
    public const float GreenTop = 0.09f;

    private static bool _cupParamAdded;

    /// <summary>Opens the cup at <paramref name="at"/> (Godot coordinates) in every grass surface, or closes it (null).</summary>
    public static void SetCupHole(Vector3? at)
    {
        EnsureCupParam();
        RenderingServer.GlobalShaderParameterSet("cup_hole", at is { } p ? new Vector4(p.X, p.Z, 0, CupRadius) : Vector4.Zero);
    }

    private static void EnsureCupParam()
    {
        if (_cupParamAdded) return;
        _cupParamAdded = true;
        RenderingServer.GlobalShaderParameterAdd("cup_hole", RenderingServer.GlobalShaderParameterType.Vec4, Vector4.Zero);
    }

    public static ShaderMaterial Grass(Color baseColor, Color alt, float stripeWidth, Turf turf = Turf.Rough)
    {
        EnsureCupParam();
        var m = new ShaderMaterial { Shader = new Shader { Code = GrassShader } };
        m.SetShaderParameter("base_color", baseColor);
        m.SetShaderParameter("alt_color", alt);
        m.SetShaderParameter("stripe_width", stripeWidth);
        var (tile, detail, bump) = TurfLook(turf);
        m.SetShaderParameter("turf_tile", tile);
        m.SetShaderParameter("turf_detail", detail);
        m.SetShaderParameter("turf_bump", bump);
        SetTurfTextures(m);
        return m;
    }

    private const string OverlayShader = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;
        uniform float opacity = 1.0;
        global uniform vec4 cup_hole;
        varying vec3 world_pos;
        void vertex() { world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
        void fragment() {
            if (cup_hole.w > 0.0 && distance(world_pos.xz, cup_hole.xy) < cup_hole.w) discard;
            ALBEDO = COLOR.rgb;
            ALPHA = COLOR.a * opacity;
        }
        """;

    /// <summary>Unshaded, see-through, vertex-coloured: for read-outs laid on the ground (kept out of the open cup).</summary>
    public static ShaderMaterial Overlay(float opacity)
    {
        EnsureCupParam();
        var m = new ShaderMaterial { Shader = new Shader { Code = OverlayShader } };
        m.SetShaderParameter("opacity", opacity);
        return m;
    }

    /// <summary>Bunker sand colour; the raked-sand photo adds its texture.</summary>
    public static readonly Color SandColor = new(0.87f, 0.80f, 0.60f);

    public static StandardMaterial3D Sand() => new()
    {
        AlbedoColor = SandColor,
        AlbedoTexture = GD.Load<Texture2D>("res://assets/ground/sand_color.jpg"),
        NormalEnabled = true,
        NormalTexture = GD.Load<Texture2D>("res://assets/ground/sand_normal.jpg"),
        NormalScale = 0.8f,
        Uv1Triplanar = true,
        Uv1WorldTriplanar = true,
        Uv1Scale = Vector3.One / SandTile,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>Cart path concrete, weathered light grey.</summary>
    public static readonly Color Concrete = new(0.66f, 0.65f, 0.61f);

    /// <summary>Cart path concrete for the flat test holes (the terrain shader draws its own).</summary>
    public static StandardMaterial3D CartPath() => new() { AlbedoColor = Concrete, Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

    /// <summary>Open water colours: deep and dark (the sky's reflection does most of the work), and the silty shallows.</summary>
    public static readonly Color WaterDeep = new(0.035f, 0.11f, 0.13f), WaterShallow = new(0.20f, 0.30f, 0.24f);

    /// <summary>
    /// Shared GLSL for moving water: ripples as a normal map (tangent x = world x, y = world z; needs
    /// <c>TURF_VERTEX</c>-style tangents) from a few travelling waves plus wind-blown noise, calmed with distance so
    /// far water doesn't shimmer.
    /// </summary>
    public const string WaterGlsl = """
        float water_hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float water_noise(vec2 p) {
            vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(water_hash(i), water_hash(i + vec2(1, 0)), f.x), mix(water_hash(i + vec2(0, 1)), water_hash(i + vec2(1, 1)), f.x), f.y);
        }
        float water_height(vec2 p, float t) {
            return sin(dot(p, vec2(0.82, 0.57)) * 1.7 + t * 1.3) * 0.5
                 + sin(dot(p, vec2(-0.45, 0.89)) * 2.9 + t * 1.9) * 0.3
                 + sin(dot(p, vec2(0.97, -0.24)) * 5.3 + t * 2.7) * 0.15
                 + (water_noise(p * 3.0 + vec2(t * 0.4, t * 0.25)) - 0.5) * 0.4
                 + (water_noise(p * 9.0 - vec2(t * 0.7, t * 0.3)) - 0.5) * 0.12;
        }
        vec3 water_normal(vec2 p, float t) {
            const float e = 0.04;
            float h = water_height(p, t);
            vec2 slope = vec2(water_height(p + vec2(e, 0.0), t) - h, water_height(p + vec2(0.0, e), t) - h) / e;
            float calm = 1.0 / (1.0 + length(fwidth(p)) * 6.0);
            return normalize(vec3(-slope * 0.06 * calm, 1.0)) * 0.5 + 0.5;
        }
        """;

    private const string WaterShader = """
        shader_type spatial;
        render_mode blend_mix, cull_disabled, depth_draw_always;
        uniform vec3 deep_color : source_color;
        uniform vec3 shallow_color : source_color;
        // 1 = see the bed through shallow water (needs real ground under it); 0 = opaque (the flat test holes).
        uniform float see_through = 1.0;
        uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
        varying vec3 world_pos;
        """ + WaterGlsl + """
        void vertex() {
            world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            TANGENT = vec3(1.0, 0.0, 0.0);
            BINORMAL = vec3(0.0, 0.0, 1.0);
        }
        void fragment() {
            float depth = 4.0;
            if (see_through > 0.5) {
                // Water depth under this point: the ground behind it along the view ray, turned to vertical.
                vec4 bed = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, texture(depth_tex, SCREEN_UV).r, 1.0);
                bed.xyz /= bed.w;
                vec3 ray = normalize(VERTEX);
                float along = (VERTEX.z - bed.z) / max(-ray.z, 0.05);
                depth = max(along, 0.0) * abs(dot(ray, normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz)));
            }
            float murk = 1.0 - exp(-depth * 1.8);
            NORMAL_MAP = water_normal(world_pos.xz, TIME);
            float facing = clamp(dot(NORMAL, VIEW), 0.0, 1.0);
            float fresnel = pow(1.0 - facing, 4.0);
            // A thin, broken line of foam where the water meets the bank.
            float foam = (1.0 - smoothstep(0.0, 0.1, depth)) * smoothstep(0.35, 0.75, water_noise(world_pos.xz * 5.0 + vec2(TIME * 0.3, -TIME * 0.2)));
            ALBEDO = mix(mix(shallow_color, deep_color, murk), vec3(0.75, 0.78, 0.74), foam * 0.6);
            ALPHA = clamp(mix(0.3, 0.94, murk) + fresnel * 0.6 + foam * 0.4, 0.0, 1.0) * smoothstep(0.0, 0.03, depth);
            ROUGHNESS = mix(0.03, 0.4, foam);
            SPECULAR = 0.5;
        }
        """;

    /// <summary>
    /// Open water: rippling, reflecting the sky, darker with depth. <paramref name="seeThrough"/> shows the bed through
    /// the shallows and a soft shoreline (for water surfaces over real ground).
    /// </summary>
    public static ShaderMaterial Water(bool seeThrough = false)
    {
        var m = new ShaderMaterial { Shader = new Shader { Code = WaterShader } };
        m.SetShaderParameter("deep_color", WaterDeep);
        m.SetShaderParameter("shallow_color", WaterShallow);
        m.SetShaderParameter("see_through", seeThrough ? 1f : 0f);
        return m;
    }

    /// <summary>Hole coordinates (X forward, Z right) to Godot (-Z forward, X right).</summary>
    public static Vector3 ToGodot(P2 p, float y = 0) => new((float)p.Z, y, (float)-p.X);

    /// <summary>A flat filled polygon (any simple outline, concave included).</summary>
    public static MeshInstance3D Flat(IReadOnlyList<P2> outline, float y, Material material)
    {
        // Wind every triangle the same way so the faces point up (otherwise they're lit from below and look dark).
        static double Cross(P2 a, P2 b, P2 c) => (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
        var tris = Geometry2D.TriangulatePolygon(outline.Select(p => new Vector2((float)p.X, (float)p.Z)).ToArray());
        if (tris.Length == 0)
        {
            // Not a simple polygon (e.g. self-touching); fall back to a fan around the centroid.
            var fan = outline.Append(new P2(outline.Average(p => p.X), outline.Average(p => p.Z))).ToList();
            outline = fan;
            tris = Enumerable.Range(0, fan.Count - 1).SelectMany(i => new[] { fan.Count - 1, i, (i + 1) % (fan.Count - 1) }).ToArray();
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i + 2 < tris.Length; i += 3)
        {
            P2 a = outline[tris[i]], b = outline[tris[i + 1]], c = outline[tris[i + 2]];
            if (Cross(a, b, c) < 0) (b, c) = (c, b);
            foreach (var p in new[] { a, b, c }) { st.SetNormal(Vector3.Up); st.AddVertex(ToGodot(p, y)); }
        }
        return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    /// <summary>The outline pushed out by <paramref name="d"/> metres (for fringes around mapped greens).</summary>
    public static List<P2> Grow(IReadOnlyList<P2> pts, double d)
    {
        double area = 0;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++) area += pts[j].X * pts[i].Z - pts[i].X * pts[j].Z;
        double sign = area >= 0 ? 1 : -1;
        P2 Normal(P2 a, P2 b)
        {
            var e = b - a; double len = Math.Max(e.Length, 1e-9);
            return new P2(e.Z / len, -e.X / len) * sign;
        }
        var result = new List<P2>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            P2 prev = pts[(i + pts.Count - 1) % pts.Count], cur = pts[i], next = pts[(i + 1) % pts.Count];
            var n = Normal(prev, cur) + Normal(cur, next);
            double len = n.Length;
            // Mitre (|n1 + n2| = 2 cos(half angle)), limited to 3× the offset so sharp corners don't spike.
            double scale = len < 1e-9 ? 0 : d * Math.Min(2 / (len * len), 3 / len);
            result.Add(cur + n * scale);
        }
        return result;
    }

    /// <summary>A flat shape: ellipses and polygons as one mesh, strips as quads per segment plus round joints.</summary>
    public static void AddShape(Node3D parent, Shape shape, float y, Material material, float grow = 0)
    {
        switch (shape)
        {
            case Ellipse e:
                parent.AddChild(Flat((e with { RadiusX = e.RadiusX + grow, RadiusZ = e.RadiusZ + grow }).Outline(48), y, material));
                break;
            case Polygon p:
                parent.AddChild(Flat(grow > 0 ? Grow(p.Points, grow) : p.Points, y, material));
                break;
            case Strip s:
                double half = s.Width / 2 + grow;
                for (int i = 0; i < s.Points.Count - 1; i++)
                {
                    var a = s.Points[i]; var b = s.Points[i + 1];
                    var d = b - a; double len = d.Length;
                    var n = new P2(-d.Z / len, d.X / len) * half;
                    parent.AddChild(Flat(new[] { a + n, b + n, b - n, a - n }, y, material));
                }
                foreach (var p in s.Points)
                    parent.AddChild(Flat(new Ellipse(p, half, half).Outline(24), y, material));
                break;
        }
    }

    /// <summary>Cone trees (one MultiMesh for foliage, one for trunks) at the given ground points.</summary>
    /// <summary>Trees at ground points (see <see cref="TreeFactory"/>).</summary>
    public static void AddTrees(Node3D parent, IReadOnlyList<Vector3> spots, ulong seed) => TreeFactory.AddForest(parent, spots, seed);

    /// <summary>A flagstick standing at <paramref name="at"/>, scaled up with <paramref name="scale"/> so far targets stay readable.</summary>
    public static Flagstick AddFlag(Node3D parent, Vector3 at, Color color, float scale = 1, bool growFlag = true)
    {
        var flag = new Flagstick(color, scale) { Position = at, GrowFlag = growFlag };
        parent.AddChild(flag);
        return flag;
    }
}
