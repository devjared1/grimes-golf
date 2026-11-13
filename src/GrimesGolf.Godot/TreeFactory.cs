using Godot;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// Trees built in code: conifers (a trunk with drooping, jagged tiers of needles) and broadleaf trees (trunk and
/// branches under a crown of lumpy leaf clumps), mixed about half and half, with per-tree size, lean, turn and colour.
/// <para>
/// Each kind has a detailed mesh for nearby trees and a very cheap one for distant ones. Trees are grouped into
/// <see cref="ChunkSize"/> patches, each drawn with one MultiMesh per mesh, so whole patches switch detail with
/// distance (Godot visibility ranges, cross-faded). Meshes are one unit tall; the instance scale is the height in metres.
/// </para>
/// <para>
/// The foliage shader gives leaves without textures: 3D noise breaks the surfaces into light and dark leaf masses and
/// cuts holes where they're seen edge-on (so outlines are ragged, not smooth shells), light shows through from behind,
/// and the crowns sway a little in the wind. Mesh vertex colours carry baked shading (darker inside and low in the
/// crown); instance colours carry each tree's tint.
/// </para>
/// <para>
/// Nearby trees also wear <see cref="LeafCards"/>: sprays of leaves (or needles along each conifer branch) over the
/// crown, so up close it breaks up into leaves instead of reading as a smooth shell.
/// </para>
/// </summary>
public static class TreeFactory
{
    private const float ChunkSize = 120;
    /// <summary>Distance (m, from a patch's centre) where its trees switch from detailed to simple meshes, and the cross-fade either side.</summary>
    private const float DetailRange = 230, FadeMargin = 30;

    /// <summary>
    /// Shared by every tree shader: a tree standing between the camera and the ball (or with the camera inside it) is
    /// thinned to a ghost with a screen-door dither, so the ball can be seen through it. Worked out per tree (from its
    /// instance transform: base, width and height) in the vertex shader, so a whole tree fades, not a hole in it.
    /// Set by <see cref="SeeThrough"/>.
    /// </summary>
    internal const string SeeThroughGlsl = """
        uniform vec3 see_cam = vec3(0.0);
        uniform vec3 see_ball = vec3(0.0);
        uniform float see_on = 0.0;
        varying float see_fade;
        float see_through_amount(mat4 model) {
            if (see_on < 0.5) return 0.0;
            vec3 base = model[3].xyz;
            float h = length(model[1].xyz), w = length(model[0].xyz);
            vec3 d = see_ball - see_cam;
            float dd = dot(d.xz, d.xz);
            // The point on the camera-to-ball line nearest the tree's stem (seen from above).
            float raw = dd > 1e-4 ? dot(base.xz - see_cam.xz, d.xz) / dd : 1.0;
            vec3 p = see_cam + d * clamp(raw, 0.0, 1.0);
            float up = p.y - base.y;
            if (up < -1.0 || up > h + 1.0) return 0.0;
            // Between them, anything near the line (crown and a margin); behind the ball or the camera, only a
            // tree whose crown is over the ball or round the camera.
            bool between = raw >= 0.0 && raw <= 1.0;
            float r = between ? w * 0.45 + 1.5 : w * 0.35;
            return 1.0 - smoothstep(r, r + 1.5, length(p.xz - base.xz));
        }
        void see_through(float fade, vec2 frag) {
            if (fade <= 0.0) return;
            float n = fract(52.9829189 * fract(dot(frag, vec2(0.06711056, 0.00583715))));
            if (n < fade * 0.9) discard;   // a faint ghost of the tree is left
        }
        """;

    private const string BarkShader = """
        shader_type spatial;
        uniform vec3 albedo : source_color = vec3(0.27, 0.20, 0.14);
        """ + SeeThroughGlsl + """
        void vertex() { see_fade = see_through_amount(MODEL_MATRIX); }
        void fragment() {
            see_through(see_fade, FRAGCOORD.xy);
            ALBEDO = albedo;
            ROUGHNESS = 1.0;
        }
        """;

    private const string FoliageShader = """
        shader_type spatial;
        render_mode cull_disabled;
        uniform float leaf_scale = 16.0;
        uniform float shell_shade = 1.0;   // under leaf cards (nearby trees) the crown is its shaded inside
        varying vec3 local_pos;
        varying vec3 world_pos;
        """ + Scenery.CloudGlsl + SeeThroughGlsl + """
        float hash3(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }
        float noise3(vec3 p) {
            vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(mix(hash3(i), hash3(i + vec3(1, 0, 0)), f.x), mix(hash3(i + vec3(0, 1, 0)), hash3(i + vec3(1, 1, 0)), f.x), f.y),
                       mix(mix(hash3(i + vec3(0, 0, 1)), hash3(i + vec3(1, 0, 1)), f.x), mix(hash3(i + vec3(0, 1, 1)), hash3(i + vec3(1, 1, 1)), f.x), f.y), f.z);
        }
        void vertex() {
            vec3 origin = MODEL_MATRIX[3].xyz;
            float phase = dot(origin.xz, vec2(0.11, 0.07));
            // Sway grows with height up the tree (the mesh is one unit tall).
            float sway = max(VERTEX.y, 0.0) * max(VERTEX.y, 0.0);
            VERTEX.x += sin(TIME * 1.3 + phase) * 0.006 * sway;
            VERTEX.z += sin(TIME * 0.9 + phase * 1.7) * 0.004 * sway;
            local_pos = VERTEX + vec3(fract(phase) * 13.0);   // each tree its own leaf pattern
            world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            see_fade = see_through_amount(MODEL_MATRIX);
        }
        void fragment() {
            see_through(see_fade, FRAGCOORD.xy);
            float leaves = noise3(local_pos * leaf_scale) * 0.6 + noise3(local_pos * leaf_scale * 2.9) * 0.4;
            float edge_on = 1.0 - abs(dot(NORMAL, VIEW));
            if (leaves < edge_on * 0.95 - 0.2) discard;
            vec3 c = COLOR.rgb * mix(0.55, 1.1, leaves) * shell_shade;
            ALBEDO = c * cloud_light(world_pos, TIME, PROJECTION_MATRIX);
            BACKLIGHT = c * 0.35;
            ROUGHNESS = 0.85;
            SPECULAR = 0.2;
        }
        """;

    private static ShaderMaterial? _foliage, _foliageUnderCards, _bark;
    private static Mesh? _coniferNear, _coniferFar, _broadleafNear, _broadleafFar, _trunkNear, _trunkFar, _coniferCards, _broadleafCards;

    private static ShaderMaterial Foliage => _foliage ??= new ShaderMaterial { Shader = new Shader { Code = FoliageShader } };
    private static ShaderMaterial FoliageUnderCards => _foliageUnderCards ??= UnderCards();

    private static ShaderMaterial UnderCards()
    {
        var m = new ShaderMaterial { Shader = Foliage.Shader };
        m.SetShaderParameter("shell_shade", 0.78f);
        return m;
    }
    private static ShaderMaterial Bark => _bark ??= new ShaderMaterial { Shader = new Shader { Code = BarkShader } };

    /// <summary>
    /// Thins out the trees standing between the camera and the ball, so the ball can be seen (null either = no tree
    /// is thinned). Call each frame.
    /// </summary>
    public static void SeeThrough(Vector3? camera, Vector3? ball)
    {
        bool on = camera != null && ball != null;
        foreach (var m in new[] { _foliage, _foliageUnderCards, _bark, LeafCards.MadeBroadleaf, LeafCards.MadeConifer })
        {
            if (m == null) continue;
            m.SetShaderParameter("see_on", on ? 1f : 0f);
            if (!on) continue;
            m.SetShaderParameter("see_cam", camera!.Value);
            m.SetShaderParameter("see_ball", ball!.Value);
        }
    }


    /// <summary>Plants a tree at each spot (ground level), mixed kinds and sizes, the same every time for <paramref name="seed"/>.</summary>
    public static void AddForest(Node3D parent, IReadOnlyList<Vector3> spots, ulong seed)
    {
        var rng = new RandomNumberGenerator { Seed = seed };
        var plants = new List<(Vector3 At, float Height, float Width, bool Conifer)>(spots.Count);
        foreach (var at in spots)
        {
            bool conifer = rng.Randf() < 0.55f;
            float height = conifer ? rng.RandfRange(12, 21) : rng.RandfRange(9, 15);
            float width = height * rng.RandfRange(0.85f, 1.2f) * (conifer ? 0.95f : 1.1f);
            plants.Add((at, height, width, conifer));
        }
        Plant(parent, plants, rng);
    }

    /// <summary>
    /// Plants surveyed trees, each at its spot (ground level). Its kind and size come from <see cref="TreeShape"/>,
    /// which the ball's flight past trees uses too, so a ball meets a tree only where one is drawn. The turn, lean and
    /// colour are the same every time for <paramref name="seed"/>.
    /// </summary>
    public static void AddMeasured(Node3D parent, IReadOnlyList<(Vector3 At, CourseTree Tree)> trees, ulong seed)
    {
        var rng = new RandomNumberGenerator { Seed = seed };
        var plants = new List<(Vector3 At, float Height, float Width, bool Conifer)>(trees.Count);
        foreach (var (at, tree) in trees)
        {
            bool conifer = TreeShape.IsConifer(tree);
            plants.Add((at, (float)tree.Height, (float)TreeShape.Width(tree, conifer), conifer));
        }
        Plant(parent, plants, rng);
    }

    private static void Plant(Node3D parent, List<(Vector3 At, float Height, float Width, bool Conifer)> plants, RandomNumberGenerator rng)
    {
        if (plants.Count == 0) return;
        if (_coniferNear == null)
        {
            var cards = new LeafCards.Builder();
            _coniferNear = Conifer(tiers: 8, spokes: 11, seed: 1, cards);
            _coniferCards = cards.Build();
        }
        _coniferFar ??= ConiferFar();
        if (_broadleafNear == null)
        {
            var cards = new LeafCards.Builder();
            _broadleafNear = Broadleaf(seed: 2, cards);
            _broadleafCards = cards.Build();
        }
        _broadleafFar ??= BroadleafFar();
        _trunkNear ??= Trunk(sides: 7, branches: true);
        _trunkFar ??= Trunk(sides: 6, branches: false);

        var chunks = new Dictionary<(int, int), List<(Transform3D Xform, Color Tint, bool Conifer)>>();
        foreach (var (at, height, width, conifer) in plants)
        {
            // Turned at random and leaning up to about 3° (about a horizontal axis at a random bearing).
            float bearing = rng.RandfRange(0, Mathf.Tau);
            var leanAxis = new Vector3(Mathf.Cos(bearing), 0, Mathf.Sin(bearing));
            var basis = new Basis(Vector3.Up, rng.RandfRange(0, Mathf.Tau)) * new Basis(leanAxis, rng.RandfRange(0, 0.05f))
                        * Basis.FromScale(new Vector3(width, height, width));
            float shade = rng.RandfRange(-1, 1);
            var tint = conifer
                ? new Color(0.11f + 0.02f * shade, 0.21f + 0.04f * shade, 0.10f + 0.015f * shade)
                : new Color(0.15f + 0.04f * shade, 0.26f + 0.05f * shade, 0.07f + 0.02f * shade);
            var key = ((int)Mathf.Floor(at.X / ChunkSize), (int)Mathf.Floor(at.Z / ChunkSize));
            if (!chunks.TryGetValue(key, out var list)) chunks[key] = list = new();
            list.Add((new Transform3D(basis, at - new Vector3(0, 0.3f, 0)), tint, conifer));
        }

        foreach (var trees in chunks.Values)
        {
            var conifers = trees.Where(t => t.Conifer).ToList();
            var broadleaves = trees.Where(t => !t.Conifer).ToList();
            // Near: detailed crowns and trunks with branches, casting shadows. Far: simple crowns and trunks, no shadows.
            Add(parent, _coniferNear, FoliageUnderCards, conifers, near: true);
            Add(parent, _broadleafNear, FoliageUnderCards, broadleaves, near: true);
            // Cards cast no shadows: the crown under them does, and cut-out cards in the shadow maps halved Ultra's frame rate.
            Add(parent, _coniferCards!, LeafCards.Conifer, conifers, near: true, shadows: false);
            Add(parent, _broadleafCards!, LeafCards.Broadleaf, broadleaves, near: true, shadows: false);
            Add(parent, _trunkFar, Bark, conifers, near: true, tinted: false);       // a conifer's trunk has no limbs
            Add(parent, _trunkNear, Bark, broadleaves, near: true, tinted: false);
            Add(parent, _coniferFar, Foliage, conifers, near: false);
            Add(parent, _broadleafFar, Foliage, broadleaves, near: false);
            Add(parent, _trunkFar, Bark, trees, near: false, tinted: false);
        }
    }

    private static void Add(Node3D parent, Mesh mesh, Material material, List<(Transform3D Xform, Color Tint, bool Conifer)> trees, bool near, bool tinted = true, bool shadows = true)
    {
        if (trees.Count == 0) return;
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = tinted,
            Mesh = mesh,
            InstanceCount = trees.Count,
        };
        for (int i = 0; i < trees.Count; i++)
        {
            mm.SetInstanceTransform(i, trees[i].Xform);
            if (tinted) mm.SetInstanceColor(i, trees[i].Tint);
        }
        parent.AddChild(new MultiMeshInstance3D
        {
            Multimesh = mm,
            MaterialOverride = material,
            CastShadow = near && shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityRangeBegin = near ? 0 : DetailRange - FadeMargin,
            VisibilityRangeBeginMargin = near ? 0 : FadeMargin,
            VisibilityRangeEnd = near ? DetailRange + FadeMargin : 0,
            VisibilityRangeEndMargin = near ? FadeMargin : 0,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        });
    }

    // ---- meshes (one unit tall, base at the origin) ----------------------------------------------

    /// <summary>
    /// Tiers of needles: each a skirt from a point on the stem out to a jagged, drooping rim. Each spoke of a tier also
    /// gets a branch of needle cards in <paramref name="cards"/>: one lying along the skirt, one across it.
    /// </summary>
    private static ArrayMesh Conifer(int tiers, int spokes, int seed, LeafCards.Builder cards)
    {
        var b = new MeshBuilder();
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        for (int t = 0; t < tiers; t++)
        {
            float k = t / (float)(tiers - 1);              // 0 bottom … 1 top
            float bottom = 0.16f + k * 0.66f, top = bottom + 0.2f - 0.06f * k;
            float radius = 0.33f * Mathf.Pow(1 - k * 0.92f, 0.9f) + 0.02f;
            float light = 0.72f + 0.28f * k;                 // lower tiers sit in the upper ones' shade
            var apex = new Vector3(0, top, 0);
            float turn = rng.Randf() * Mathf.Tau;
            var mid = new Vector3[spokes];
            var rim = new Vector3[spokes];
            for (int s = 0; s < spokes; s++)
            {
                float a = turn + s * Mathf.Tau / spokes + rng.RandfRange(-0.12f, 0.12f);
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                float r = radius * rng.RandfRange(0.78f, 1.15f);
                rim[s] = dir * r + new Vector3(0, bottom - rng.RandfRange(0.01f, 0.05f), 0);
                mid[s] = dir * r * 0.55f + new Vector3(0, Mathf.Lerp(top, bottom, 0.5f) + 0.015f, 0);
            }
            var centre = new Vector3(0, top - 0.12f, 0);
            Vector3 N(Vector3 p) => ((p - centre).Normalized() + Vector3.Up * 0.35f).Normalized();
            for (int s = 0; s < spokes; s++)
            {
                int n = (s + 1) % spokes;
                b.Tri(apex, mid[s], mid[n], N(apex), N(mid[s]), N(mid[n]), Shade(0.55f * light), Shade(0.8f * light), Shade(0.8f * light));
                b.Tri(mid[s], rim[s], rim[n], N(mid[s]), N(rim[s]), N(rim[n]), Shade(0.8f * light), Shade(light), Shade(light));
                b.Tri(mid[s], rim[n], mid[n], N(mid[s]), N(rim[n]), N(mid[n]), Shade(0.8f * light), Shade(light), Shade(0.8f * light));

                // The branch: from near the stem out past the rim, so its needles fringe the tier.
                var from = new Vector3(rim[s].X * 0.2f, Mathf.Lerp(top, bottom, 0.3f), rim[s].Z * 0.2f);
                var tip = rim[s] + (rim[s] - mid[s]) * 0.3f;
                var outward = (tip - from).Normalized();
                var flat = outward.Cross(Vector3.Up).Normalized();
                var upright = flat.Cross(outward).Normalized();
                float spread = radius * 0.32f;
                var shade = Shade(0.9f * light);
                cards.Card(from, tip, flat * spread, N(tip), shade);
                cards.Card(from, tip, upright * spread * 0.7f, N(tip), shade);
            }
        }
        return b.Build();
    }

    /// <summary>Two stacked cones in the shape of a conifer's crown.</summary>
    private static ArrayMesh ConiferFar()
    {
        var b = new MeshBuilder();
        const int sides = 7;
        foreach (var (bottom, top, radius, light) in new[] { (0.16f, 0.72f, 0.34f, 0.8f), (0.55f, 1.0f, 0.2f, 1.0f) })
            for (int s = 0; s < sides; s++)
            {
                float a0 = s * Mathf.Tau / sides, a1 = (s + 1) * Mathf.Tau / sides;
                var p0 = new Vector3(Mathf.Cos(a0) * radius, bottom, Mathf.Sin(a0) * radius);
                var p1 = new Vector3(Mathf.Cos(a1) * radius, bottom, Mathf.Sin(a1) * radius);
                var apex = new Vector3(0, top, 0);
                Vector3 N(Vector3 p) => (new Vector3(p.X, 0, p.Z).Normalized() + Vector3.Up * 0.5f).Normalized();
                b.Tri(apex, p0, p1, Vector3.Up, N(p0), N(p1), Shade(0.9f * light), Shade(light), Shade(light));
            }
        return b.Build();
    }

    /// <summary>
    /// A crown of lumpy leaf clumps around an egg-shaped centre, and in <paramref name="cards"/> leaf sprays set over
    /// the clumps' surfaces, each tilted its own way so some stand out from the outline.
    /// </summary>
    private static ArrayMesh Broadleaf(int seed, LeafCards.Builder cards)
    {
        var b = new MeshBuilder();
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        var crown = new Vector3(0, 0.66f, 0);
        var clumps = new List<(Vector3 At, float R)> { (crown + new Vector3(0, 0.2f, 0), 0.2f) };
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.Tau / 6 + rng.RandfRange(-0.3f, 0.3f);
            clumps.Add((crown + new Vector3(Mathf.Cos(a) * 0.24f, rng.RandfRange(-0.02f, 0.08f), Mathf.Sin(a) * 0.24f), rng.RandfRange(0.17f, 0.21f)));
        }
        for (int i = 0; i < 4; i++)
        {
            float a = (i + 0.5f) * Mathf.Tau / 4 + rng.RandfRange(-0.3f, 0.3f);
            clumps.Add((crown + new Vector3(Mathf.Cos(a) * 0.17f, -0.15f, Mathf.Sin(a) * 0.17f), rng.RandfRange(0.14f, 0.18f)));
        }
        var sphere = Icosphere(1);
        foreach (var (at, r) in clumps)
        {
            var pts = sphere.Points.Select(d => at + d * r * rng.RandfRange(0.82f, 1.15f)).ToArray();
            foreach (var (i0, i1, i2) in sphere.Faces)
            {
                Vector3 N(int i) => ((pts[i] - at).Normalized() * 0.4f + (pts[i] - crown).Normalized() * 0.6f).Normalized();
                // Shaded by how far out and how high in the crown each point is.
                Color S(int i)
                {
                    var off = pts[i] - crown;
                    float outward = Mathf.Clamp(off.Length() / 0.42f, 0, 1), high = Mathf.Clamp((pts[i].Y - 0.4f) / 0.55f, 0, 1);
                    return Shade(0.45f + 0.35f * outward + 0.2f * high);
                }
                b.Tri(pts[i0], pts[i1], pts[i2], N(i0), N(i1), N(i2), S(i0), S(i1), S(i2));
            }
            for (int i = 0; i < pts.Length; i++)
            {
                if (rng.Randf() > 0.85f) continue;
                var n = ((pts[i] - at).Normalized() * 0.4f + (pts[i] - crown).Normalized() * 0.6f).Normalized();
                // Facing out of the crown, tilted up to about 45° about a random axis across it, then turned on itself.
                var tiltAxis = n.Cross(new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1))).Normalized();
                var facing = n.Rotated(tiltAxis, rng.RandfRange(-0.8f, 0.8f));
                var e1 = facing.Cross(Mathf.Abs(facing.Y) > 0.9f ? Vector3.Right : Vector3.Up).Normalized();
                var e2 = facing.Cross(e1);
                float roll = rng.RandfRange(0, Mathf.Tau), half = r * rng.RandfRange(0.4f, 0.52f);
                var along = (e1 * Mathf.Cos(roll) + e2 * Mathf.Sin(roll)) * half;
                var centre = pts[i] + (pts[i] - at).Normalized() * r * 0.06f;
                var off = (pts[i] - crown);
                float outward = Mathf.Clamp(off.Length() / 0.42f, 0, 1), high = Mathf.Clamp((pts[i].Y - 0.4f) / 0.55f, 0, 1);
                cards.Card(centre - along, centre + along, facing.Cross(along), n, Shade(0.55f + 0.35f * outward + 0.2f * high));
            }
        }
        return b.Build();
    }

    /// <summary>One coarse lumpy ball in the crown's shape.</summary>
    private static ArrayMesh BroadleafFar()
    {
        var b = new MeshBuilder();
        var crown = new Vector3(0, 0.68f, 0);
        var scale = new Vector3(0.42f, 0.34f, 0.42f);
        var sphere = Icosphere(0);
        var pts = sphere.Points.Select(d => crown + d * scale).ToArray();
        foreach (var (i0, i1, i2) in sphere.Faces)
        {
            Vector3 N(int i) => (pts[i] - crown).Normalized();
            Color S(int i) => Shade(0.6f + 0.4f * Mathf.Clamp((pts[i].Y - 0.4f) / 0.6f, 0, 1));
            b.Tri(pts[i0], pts[i1], pts[i2], N(i0), N(i1), N(i2), S(i0), S(i1), S(i2));
        }
        return b.Build();
    }

    /// <summary>A tapering trunk up into the crown, with (optionally) a few branches reaching out into it.</summary>
    private static ArrayMesh Trunk(int sides, bool branches)
    {
        var b = new MeshBuilder();
        b.Limb(Vector3.Zero, new Vector3(0, 0.8f, 0), 0.028f, 0.006f, sides);
        if (branches)
            for (int i = 0; i < 3; i++)
            {
                float a = i * Mathf.Tau / 3 + 0.4f;
                var from = new Vector3(0, 0.36f + 0.06f * i, 0);
                b.Limb(from, from + new Vector3(Mathf.Cos(a) * 0.2f, 0.22f, Mathf.Sin(a) * 0.2f), 0.012f, 0.004f, 5);
            }
        return b.Build();
    }

    private static Color Shade(float v) => new(v, v, v);

    private sealed record Sphere(Vector3[] Points, (int, int, int)[] Faces);

    /// <summary>Unit icosphere: 20 faces, ×4 per subdivision.</summary>
    private static Sphere Icosphere(int subdivisions)
    {
        float t = (1 + Mathf.Sqrt(5)) / 2;
        var pts = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (int i = 0; i < pts.Count; i++) pts[i] = pts[i].Normalized();
        var faces = new List<(int, int, int)>
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        };
        for (int s = 0; s < subdivisions; s++)
        {
            var mids = new Dictionary<(int, int), int>();
            int Mid(int a, int c)
            {
                var key = a < c ? (a, c) : (c, a);
                if (mids.TryGetValue(key, out int m)) return m;
                pts.Add(((pts[a] + pts[c]) / 2).Normalized());
                return mids[key] = pts.Count - 1;
            }
            faces = faces.SelectMany(f =>
            {
                var (a, c, d) = f;
                int ab = Mid(a, c), bc = Mid(c, d), ca = Mid(d, a);
                return new[] { (a, ab, ca), (c, bc, ab), (d, ca, bc), (ab, bc, ca) };
            }).ToList();
        }
        return new Sphere(pts.ToArray(), faces.ToArray());
    }

    /// <summary>Accumulates triangles (with normals and vertex colours) into an ArrayMesh.</summary>
    private sealed class MeshBuilder
    {
        private readonly List<Vector3> _v = new(), _n = new();
        private readonly List<Color> _c = new();

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, Color ca, Color cb, Color cc)
        {
            // Godot's front faces are clockwise seen from outside; foliage is two-sided anyway, so only lighting cares about normals.
            _v.Add(a); _v.Add(c); _v.Add(b);
            _n.Add(na); _n.Add(nc); _n.Add(nb);
            _c.Add(ca); _c.Add(cc); _c.Add(cb);
        }

        /// <summary>A tapered tube from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public void Limb(Vector3 from, Vector3 to, float r0, float r1, int sides)
        {
            var axis = (to - from).Normalized();
            var side = axis.Cross(Mathf.Abs(axis.Y) > 0.9f ? Vector3.Right : Vector3.Up).Normalized();
            var other = axis.Cross(side);
            for (int s = 0; s < sides; s++)
            {
                float a0 = s * Mathf.Tau / sides, a1 = (s + 1) * Mathf.Tau / sides;
                Vector3 d0 = side * Mathf.Cos(a0) + other * Mathf.Sin(a0), d1 = side * Mathf.Cos(a1) + other * Mathf.Sin(a1);
                Vector3 p0 = from + d0 * r0, p1 = from + d1 * r0, q0 = to + d0 * r1, q1 = to + d1 * r1;
                var w = Shade(1);
                Tri(p0, q0, q1, d0, d0, d1, w, w, w);
                Tri(p0, q1, p1, d0, d1, d1, w, w, w);
            }
        }

        public ArrayMesh Build()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _v.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _n.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _c.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }
}
