using Godot;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Storage;

namespace GrimesGolf.Game;

/// <summary>
/// Grass tufts in the rough in front of the camera, so the cut between fairway and rough has an edge and the deep rough
/// looks long, not painted on. Everything is worked out on the GPU: each ring is a grid of tufts on world-aligned
/// cells (so tufts don't swim as the camera moves), drawn each frame only over the box round what the camera faces,
/// and the vertex shader drops each onto the ground (a
/// height texture of the terrain grid, interpolated like its mesh), reads its lie from the ground's coverage textures
/// and leaves out fairway, greens, sand, water and paths. Rings get sparser and their tufts wider with distance; how
/// far they reach follows the graphics quality. Around the resting ball it gives way to <see cref="GrassPatch"/>.
/// </summary>
public partial class GrassField : Node3D
{
    private const string ShaderCode = """
        shader_type spatial;
        render_mode cull_disabled;
        uniform sampler2D heights : filter_nearest;   // the terrain grid (course X down, course Z across)
        uniform vec2 h_origin;                        // course X, Z of grid point (0, 0)
        uniform float h_cell;
        uniform sampler2D mask : filter_linear, repeat_disable;   // as the ground's: fairway, green, sand, water
        uniform sampler2D cut : filter_linear, repeat_disable;    // first cut, pond, path, bridge
        uniform vec2 mask_origin;
        uniform vec2 mask_size;
        uniform vec2 grid_origin;   // world x, z of the ring's first cell (on the spacing grid)
        uniform float spacing;
        uniform int cols;           // cells across the area drawn this frame (instances fill it row by row)
        uniform vec3 eye;
        uniform float reach_from;   // the ring's tufts lie between these distances from the eye (m)
        uniform float reach_to;
        uniform vec4 ball_patch;    // world x, z of the ball's own grass, where it's full, where it ends; w = 0: none
        uniform vec3 rough_a : source_color; uniform vec3 rough_b : source_color;
        uniform vec3 deep_a : source_color;  uniform vec3 deep_b : source_color;
        uniform float rough_height = 0.065;
        uniform float deep_height = 0.11;
        varying vec3 tint;
        varying float tip;
        varying vec3 world_pos;
        """ + Scenery.CloudGlsl + """
        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        // Ground height as the terrain mesh draws it: each cell split from (i + 1, j) to (i, j + 1).
        float ground(vec2 course) {
            ivec2 size = textureSize(heights, 0);
            vec2 f = clamp((course - h_origin) / h_cell, vec2(0.0), vec2(size.yx) - 1.001);
            ivec2 c = ivec2(f);
            vec2 t = f - vec2(c);
            float h10 = texelFetch(heights, ivec2(c.y, c.x + 1), 0).r, h01 = texelFetch(heights, ivec2(c.y + 1, c.x), 0).r;
            if (t.x + t.y <= 1.0) {
                float h00 = texelFetch(heights, ivec2(c.y, c.x), 0).r;
                return h00 + t.x * (h10 - h00) + t.y * (h01 - h00);
            }
            float h11 = texelFetch(heights, ivec2(c.y + 1, c.x + 1), 0).r;
            return h11 + (1.0 - t.x) * (h01 - h11) + (1.0 - t.y) * (h10 - h11);
        }
        void vertex() {
            vec2 cell = grid_origin + vec2(float(INSTANCE_ID % cols), float(INSTANCE_ID / cols)) * spacing;
            vec2 key = floor(cell / spacing + 0.5);
            float r1 = hash(key), r2 = hash(key + 17.3), r3 = hash(key + 41.7), r4 = hash(key + 71.1);
            vec2 xz = cell + vec2(r1, r2) * spacing;
            float d = distance(xz, eye.xz);
            float keep = smoothstep(reach_from - 2.0, reach_from + 2.0, d) * (1.0 - smoothstep(reach_to * 0.55, reach_to, d));
            if (ball_patch.w > 0.0) keep *= smoothstep(ball_patch.z, ball_patch.w, distance(xz, ball_patch.xy));
            vec2 course = vec2(-xz.y, xz.x);
            vec2 uv = vec2((course.y - mask_origin.y) / mask_size.y, (course.x - mask_origin.x) / mask_size.x);
            vec4 m = textureLod(mask, uv, 0.0);
            vec4 c = textureLod(cut, uv, 0.0);
            float bare = max(max(max(m.r, m.g), max(m.b, m.a)), max(c.b, c.a));
            bool inside = all(greaterThan(uv, vec2(0.0))) && all(lessThan(uv, vec2(1.0)));
            tip = UV.y;
            world_pos = vec3(0.0);
            tint = vec3(0.0);
            if (!inside || bare > 0.35 || r4 >= keep) {
                VERTEX = vec3(0.0);   // no tuft here: a degenerate triangle draws nothing
            } else {
                bool first = c.r > 0.5;
                float h = (first ? rough_height : deep_height) * mix(0.7, 1.3, r3);
                // Farther tufts are wider so the thinner rings still read as a carpet.
                float w = max(h * 1.8, 0.11) * (1.0 + d / 25.0);
                float yaw = r1 * 6.2832;
                vec3 v = VERTEX;
                v = vec3(cos(yaw) * v.x - sin(yaw) * v.z, v.y, sin(yaw) * v.x + cos(yaw) * v.z) * vec3(w, h, w);
                float gust = sin(TIME * 1.3 + xz.x * 0.35 + xz.y * 0.21) * 0.6 + sin(TIME * 3.1 + r2 * 6.2832) * 0.4;
                v.x += gust * 0.1 * h * tip * tip;
                v.z += gust * 0.04 * h * tip * tip;
                VERTEX = v + vec3(xz.x, ground(course) - 0.01, xz.y);
                world_pos = VERTEX;
                tint = (first ? mix(rough_a, rough_b, r2) : mix(deep_a, deep_b, r2)) * (0.8 + 0.4 * r3);
            }
            NORMAL = normalize(mix(NORMAL, vec3(0.0, 1.0, 0.0), 0.7));
        }
        void fragment() {
            if (!FRONT_FACING) NORMAL = -NORMAL;
            // Lighter at the root than the ball's own tufts: seen from farther off, dark roots read as specks.
            ALBEDO = tint * mix(0.85, 1.25, tip * tip) * cloud_light(world_pos, TIME, PROJECTION_MATRIX);
            BACKLIGHT = tint * 0.3;
            ROUGHNESS = 0.95;
            SPECULAR = 0.15;
        }
        """;

    /// <summary>A ring: tufts from <c>From</c> to <c>To</c> metres from the camera, one per <c>Spacing</c>-metre cell.</summary>
    private sealed record Ring(float From, float To, float Spacing);

    private static Ring[] Rings(GraphicsQuality q) => q switch
    {
        GraphicsQuality.Low => [],
        GraphicsQuality.Medium => [new(0, 18, 0.22f)],
        GraphicsQuality.High => [new(0, 20, 0.17f), new(18, 40, 0.38f)],
        _ => [new(0, 24, 0.15f), new(22, 55, 0.35f)],
    };

    /// <summary>Above this height over the ground (m) the grass is too small to see and isn't drawn.</summary>
    private const float MaxEyeHeight = 25;

    private readonly Terrain _terrain;
    private readonly ShaderMaterial _ground;
    private readonly List<(MultiMeshInstance3D Node, ShaderMaterial Material, Ring Ring)> _rings = new();
    private ImageTexture? _heights;
    private GraphicsQuality? _builtFor;
    private Vector4 _ballPatch;

    /// <param name="ground">The terrain's material (<see cref="TerrainGround.Build"/>), for its coverage textures.</param>
    public GrassField(Terrain terrain, ShaderMaterial ground)
    {
        _terrain = terrain;
        _ground = ground;
    }

    // Godot needs a parameterless constructor for scripts it may instantiate itself.
    public GrassField() : this(null!, null!) { }

    /// <summary>The ball's own grass is at <paramref name="ball"/>: the field thins out there as the patch fills in.</summary>
    public void SetBallPatch(P2 ball)
    {
        var at = Scenery.ToGodot(ball);
        _ballPatch = new Vector4(at.X, at.Z, GrassPatch.FullUntil, GrassPatch.Radius);
        foreach (var (_, m, _) in _rings) m.SetShaderParameter("ball_patch", _ballPatch);
    }

    public override void _Process(double delta)
    {
        if (_terrain == null) return;
        if (_builtFor != Graphics.Current) Rebuild(Graphics.Current);
        var camera = GetViewport().GetCamera3D();
        if (camera == null || _rings.Count == 0) return;
        var eye = camera.GlobalPosition;
        bool show = eye.Y - _terrain.HeightAt(new P2(-eye.Z, eye.X)) < MaxEyeHeight;
        foreach (var (node, material, ring) in _rings)
        {
            node.Visible = show;
            if (!show) continue;
            var (min, max) = FacedBox(camera, eye, ring.To);
            // Snapped to whole cells, so a tuft stays put while the area moves with the camera.
            Vector2 first = (min / ring.Spacing).Floor(), last = (max / ring.Spacing).Ceil();
            int cols = (int)(last.X - first.X), rows = (int)(last.Y - first.Y);
            node.Multimesh.VisibleInstanceCount = Math.Min(cols * rows, node.Multimesh.InstanceCount);
            material.SetShaderParameter("grid_origin", first * ring.Spacing);
            material.SetShaderParameter("eye", eye);
            material.SetShaderParameter("cols", cols);
        }
    }

    /// <summary>
    /// World x, z box round the ground the camera faces out to <paramref name="reach"/>: the wedge of its view (a little
    /// wider) plus a few metres round the camera itself, within the square of that reach. Looking straight down, the square.
    /// </summary>
    private static (Vector2 Min, Vector2 Max) FacedBox(Camera3D camera, Vector3 eye, float reach)
    {
        var at = new Vector2(eye.X, eye.Z);
        Vector2 min = at - Vector2.One * 3, max = at + Vector2.One * 3;
        var forward = -camera.GlobalBasis.Z;
        var flat = new Vector2(forward.X, forward.Z);
        if (flat.Length() < 0.3f) return (at - Vector2.One * reach, at + Vector2.One * reach);
        var size = camera.GetViewport().GetVisibleRect().Size;
        float half = Mathf.Atan(Mathf.Tan(Mathf.DegToRad(camera.Fov) / 2) * size.X / size.Y) + 0.2f;
        float heading = flat.Angle();
        for (int k = -4; k <= 4; k++)
        {
            var p = at + Vector2.FromAngle(heading + half * k / 4) * reach;
            min = min.Min(p);
            max = max.Max(p);
        }
        return (min.Max(at - Vector2.One * reach), max.Min(at + Vector2.One * reach));
    }

    private void Rebuild(GraphicsQuality q)
    {
        _builtFor = q;
        foreach (var (node, _, _) in _rings) node.QueueFree();
        _rings.Clear();
        var rings = Rings(q);
        if (rings.Length == 0) return;
        _heights ??= HeightTexture(_terrain);
        // The outer ring's tufts are a few pixels high: fewer blades do.
        var near = GrassPatch.BuildTuft(blades: 6, segments: 2);
        var far = GrassPatch.BuildTuft(blades: 4, segments: 2);
        foreach (var ring in rings)
        {
            var tuft = ring.From > 0 ? far : near;
            var m = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
            foreach (var name in new[] { "mask", "cut", "mask_origin", "mask_size" })
                m.SetShaderParameter(name, _ground.GetShaderParameter(name));
            m.SetShaderParameter("heights", _heights);
            m.SetShaderParameter("h_origin", new Vector2((float)_terrain.Origin.X, (float)_terrain.Origin.Z));
            m.SetShaderParameter("h_cell", (float)_terrain.Cell);
            m.SetShaderParameter("spacing", ring.Spacing);
            m.SetShaderParameter("reach_from", ring.From);
            m.SetShaderParameter("reach_to", ring.To);
            m.SetShaderParameter("ball_patch", _ballPatch);
            m.SetShaderParameter("rough_a", Scenery.Rough); m.SetShaderParameter("rough_b", Scenery.RoughAlt);
            m.SetShaderParameter("deep_a", Scenery.DeepRough); m.SetShaderParameter("deep_b", Scenery.DeepRoughAlt);

            // Enough instances for the whole square round the camera; each frame draws only the box it faces.
            int side = (int)Mathf.Ceil(2 * ring.To / ring.Spacing) + 1;
            // Every tuft's place comes from the shader; the instance transforms are left at identity.
            var identity = new float[side * side * 12];
            for (int i = 0; i < side * side; i++) { identity[i * 12] = 1; identity[i * 12 + 5] = 1; identity[i * 12 + 10] = 1; }
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = tuft, InstanceCount = side * side };
            mm.Buffer = identity;
            var node = new MultiMeshInstance3D
            {
                Multimesh = mm,
                MaterialOverride = m,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // The tufts are wherever the camera is: never cull the ring as a whole.
                CustomAabb = new Aabb(new Vector3(-1e5f, -1e3f, -1e5f), new Vector3(2e5f, 2e3f, 2e5f)),
            };
            AddChild(node);
            _rings.Add((node, m, ring));
        }
    }

    /// <summary>The terrain grid as a float texture: row i is course X, column j course Z (the grid's own layout).</summary>
    private static ImageTexture HeightTexture(Terrain t)
    {
        var data = new float[t.CountX * t.CountZ];
        for (int i = 0; i < t.CountX; i++)
            for (int j = 0; j < t.CountZ; j++)
                data[i * t.CountZ + j] = t.HeightAt(i, j);
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return ImageTexture.CreateFromImage(Image.CreateFromData(t.CountZ, t.CountX, false, Image.Format.Rf, bytes));
    }
}
