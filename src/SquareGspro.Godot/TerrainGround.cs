using Godot;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>
/// The ground of a course with real heights: one mesh following the <see cref="Terrain"/> grid (in chunks, so
/// off-screen parts are culled), with an apron running out flat to the horizon, coloured by a coverage texture of
/// the playing areas (fairway and tees, greens, bunkers, water). Coverage is anti-aliased when it's rasterised, so
/// edges stay smooth even though the texture is about a metre per texel.
/// </summary>
public static class TerrainGround
{
    private const string Shader = """
        shader_type spatial;
        render_mode cull_disabled;
        uniform sampler2D mask : filter_linear_mipmap, repeat_disable;
        uniform sampler2D cut : filter_linear_mipmap, repeat_disable;   // first cut of rough (R; deep rough elsewhere), ponds with a level surface (G), cart paths (B), bridges (A)
        uniform vec3 deep_a : source_color; uniform vec3 deep_b : source_color;
        uniform vec2 mask_origin;   // course coords (X north, Z east) of the texture's corner
        uniform vec2 mask_size;     // metres covered along X and Z
        uniform vec3 rough_a : source_color; uniform vec3 rough_b : source_color;
        uniform vec3 fair_a : source_color;  uniform vec3 fair_b : source_color;
        uniform vec3 green_a : source_color; uniform vec3 green_b : source_color;
        uniform vec3 sand : source_color;
        uniform vec3 concrete : source_color;
        uniform vec3 water : source_color;  // water that follows the ground (creeks, ditches)
        uniform vec3 pond_bed : source_color;   // under a pond's own water surface
        // Photo detail per area: tile size (m), colour variation, bumpiness (see Scenery.TurfLook).
        uniform vec3 rough_look; uniform vec3 fair_look; uniform vec3 green_look;
        uniform float sand_tile = 3.5;
        global uniform vec4 cup_hole;
        varying float steep;   // 0 on level ground, rising with the slope
        varying vec3 world_pos;
        """ + Scenery.TurfGlsl + Scenery.WaterGlsl + Scenery.CloudGlsl + """
        void vertex() {
            world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            steep = 1.0 - normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz).y;
        """ + Scenery.TurfVertex + """
        }
        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
        }
        // Mowing stripes: bands mown in turn one way and back along x. Grass leaning away from the eye shows its lighter
        // blades, so the bands swap light and dark as the view turns, and fade when looking across them.
        float stripes(float width, vec3 look) {
            float s = step(0.5, fract(world_pos.z / (2.0 * width))) * 2.0 - 1.0;
            return 0.985 + 0.085 * mix(s, s * look.x * 1.4, 0.65);
        }
        float edge(float coverage) { float w = max(fwidth(coverage), 0.02); return smoothstep(0.5 - w, 0.5 + w, coverage); }
        // Coverage for the edges. Near the camera: the full-resolution texture through a cubic B-spline (four bilinear
        // taps), so an edge follows a smooth curve instead of kinking at every 1 m texel. Further off: the mip level for
        // the screen direction that shows the most detail (the usual choice, the coarser one, smears a band of soft
        // edge across the ground seen at a low angle, ending in a straight seam where the level changes).
        vec4 coverage(sampler2D t, vec2 uv) {
            vec2 size = vec2(textureSize(t, 0));
            vec2 px = uv * size;
            float lod = log2(max(min(length(dFdx(px)), length(dFdy(px))), 1e-6));
            vec4 far = textureLod(t, uv, max(lod, 0.0));
            if (lod >= 1.0) return far;
            vec2 i = floor(px - 0.5), f = px - 0.5 - i;
            vec2 w0 = (1.0 - f) * (1.0 - f) * (1.0 - f) / 6.0;
            vec2 w1 = (4.0 - 6.0 * f * f + 3.0 * f * f * f) / 6.0;
            vec2 w2 = (1.0 + 3.0 * f + 3.0 * f * f - 3.0 * f * f * f) / 6.0;
            vec2 w3 = f * f * f / 6.0;
            vec2 g0 = w0 + w1, g1 = w2 + w3;
            vec2 h0 = (i - 0.5 + w1 / g0) / size, h1 = (i + 1.5 + w3 / g1) / size;
            vec4 cubic = g0.y * (g0.x * textureLod(t, vec2(h0.x, h0.y), 0.0) + g1.x * textureLod(t, vec2(h1.x, h0.y), 0.0))
                       + g1.y * (g0.x * textureLod(t, vec2(h0.x, h1.y), 0.0) + g1.x * textureLod(t, vec2(h1.x, h1.y), 0.0));
            return mix(cubic, far, clamp(lod, 0.0, 1.0));
        }
        void fragment() {
            if (cup_hole.w > 0.0 && distance(world_pos.xz, cup_hole.xy) < cup_hole.w) discard;
            vec2 course = vec2(-world_pos.z, world_pos.x);
            vec2 uv = vec2((course.y - mask_origin.y) / mask_size.y, (course.x - mask_origin.x) / mask_size.x);
            vec4 m = coverage(mask, uv);
            float n = noise(world_pos.xz * 0.12) * 0.6 + noise(world_pos.xz * 0.6) * 0.4;
            vec2 p = world_pos.xz;
            float f = edge(m.r), g = edge(m.g), s = edge(m.b), w = edge(m.a);
            vec4 cm = coverage(cut, uv);
            float pond = edge(cm.g), bridge = edge(cm.a), path = max(edge(cm.b), bridge);
            // Blurred coverage (a few metres) for what happens just outside sand and water.
            vec4 near = textureLod(mask, uv, 2.0);
            // Each area's colour times its photo detail; the rough's look also serves the deep rough.
            vec3 rough_c = mix(mix(deep_a, deep_b, n), mix(rough_a, rough_b, n), edge(cm.r))
                           * photo_detail(grass_albedo, p, rough_look.x, rough_look.y);
            vec3 look = normalize(world_pos - CAMERA_POSITION_WORLD);
            vec3 c = rough_c;
            c = mix(c, mix(fair_a, fair_b, n) * stripes(6.0, look) * photo_detail(grass_albedo, p, fair_look.x, fair_look.y), f);
            // The collar: the fringe's band round each green (painted with the fairway), mown as one ring between the two.
            float collar = f * (1.0 - g) * smoothstep(0.03, 0.2, textureLod(mask, uv, 1.5).g);
            c = mix(c, mix(mix(fair_a, green_a, 0.45), mix(fair_b, green_b, 0.45), n) * 0.92 * photo_detail(grass_albedo, p, green_look.x, fair_look.y), collar);
            c = mix(c, mix(green_a, green_b, n) * stripes(1.5, look) * photo_detail(grass_albedo, p, green_look.x, green_look.y), g);
            // Cart paths: concrete, weathered in patches with a fine speckle.
            c = mix(c, concrete * (0.9 + 0.14 * n) * (0.95 + 0.1 * noise(p * 4.0)), path);
            // Grass just outside a bunker is its lip: a little longer and shadowed. Banks by water are wet and dark.
            c *= 1.0 - 0.18 * smoothstep(0.05, 0.4, near.b) * (1.0 - s);
            c *= 1.0 - 0.15 * smoothstep(0.05, 0.45, near.a) * (1.0 - w);
            // Sand: darker and damper in the shade under the lip, lighter on the floor.
            // Sand: damp and shaded on the faces under the lip, drier and lighter on the floor.
            float under_lip = max(1.0 - smoothstep(0.55, 0.95, m.b), (1.0 - smoothstep(0.5, 0.95, near.b)) * 0.6);
            float face = smoothstep(0.01, 0.12, steep);
            c = mix(c, sand * photo_detail(sand_albedo, p, sand_tile, 1.0) * mix(1.0, 0.72, max(under_lip, face)), s);
            c = mix(c, mix(water, pond_bed, pond), w);
            // Bridges carry the path over the water.
            c = mix(c, concrete * (0.9 + 0.14 * n), bridge);
            w *= 1.0 - bridge;
            ALBEDO = c * cloud_light(world_pos, TIME, PROJECTION_MATRIX);
            // Only the finest grass that's showing is sampled for bumps: rough, then fairway, then green, then sand.
            vec3 bump = photo_bump(grass_normal, p, mix(mix(rough_look.x, fair_look.x, f), green_look.x, g));
            bump = mix(bump, photo_bump(sand_normal, p, sand_tile), s);
            float open_water = w * (1.0 - pond);
            NORMAL_MAP = mix(bump, water_normal(p, TIME), open_water);
            NORMAL_MAP_DEPTH = mix(mix(mix(mix(rough_look.z, fair_look.z, f), green_look.z, g), 0.8, s), 1.0, w) * (1.0 - w * pond) * (1.0 - 0.85 * path);
            // Close-mown greens have a soft sheen where they face the sun.
            float sheen = g * (1.0 - s) * (1.0 - w);
            ROUGHNESS = mix(mix(0.9, 0.55, sheen), 0.04, open_water);
            SPECULAR = mix(mix(0.25, 0.42, sheen), 0.5, open_water);
        }
        """;

    private const int Chunk = 128;          // cells per mesh chunk side
    private const double Texel = 1.0;       // metres per coverage texel
    private const float Apron = 4000;       // flat ground beyond the modelled area

    /// <summary>Adds the ground meshes and ponds; returns the ground material (its coverage textures serve <see cref="GrassField"/>).</summary>
    public static ShaderMaterial Build(Node3D parent, Course course, Terrain terrain)
    {
        var material = new ShaderMaterial { Shader = new Shader { Code = Shader } };
        var area = terrain.Area;
        var (mask, cut) = BuildMask(course, terrain);
        material.SetShaderParameter("mask", mask);
        material.SetShaderParameter("cut", cut);
        material.SetShaderParameter("deep_a", Scenery.DeepRough); material.SetShaderParameter("deep_b", Scenery.DeepRoughAlt);
        material.SetShaderParameter("mask_origin", new Vector2((float)area.Min.X, (float)area.Min.Z));
        material.SetShaderParameter("mask_size", new Vector2((float)(area.Max.X - area.Min.X), (float)(area.Max.Z - area.Min.Z)));
        material.SetShaderParameter("rough_a", Scenery.Rough); material.SetShaderParameter("rough_b", Scenery.RoughAlt);
        material.SetShaderParameter("fair_a", Scenery.Fairway); material.SetShaderParameter("fair_b", Scenery.FairwayAlt);
        material.SetShaderParameter("green_a", Scenery.Green); material.SetShaderParameter("green_b", Scenery.GreenAlt);
        material.SetShaderParameter("sand", Scenery.SandColor);
        material.SetShaderParameter("concrete", Scenery.Concrete);
        Vector3 Look(Scenery.Turf t) { var (tile, detail, bump) = Scenery.TurfLook(t); return new Vector3(tile, detail, bump); }
        material.SetShaderParameter("rough_look", Look(Scenery.Turf.Rough));
        material.SetShaderParameter("fair_look", Look(Scenery.Turf.Fairway));
        material.SetShaderParameter("green_look", Look(Scenery.Turf.Green));
        material.SetShaderParameter("sand_tile", Scenery.SandTile);
        Scenery.SetTurfTextures(material);
        material.SetShaderParameter("water", Scenery.WaterDeep);
        material.SetShaderParameter("pond_bed", new Color(0.16f, 0.17f, 0.11f));

        for (int i0 = 0; i0 < terrain.CountX - 1; i0 += Chunk)
            for (int j0 = 0; j0 < terrain.CountZ - 1; j0 += Chunk)
                parent.AddChild(new MeshInstance3D
                {
                    Mesh = BuildChunk(terrain, i0, j0, Math.Min(Chunk, terrain.CountX - 1 - i0), Math.Min(Chunk, terrain.CountZ - 1 - j0)),
                    MaterialOverride = material,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
        parent.AddChild(new MeshInstance3D { Mesh = BuildApron(terrain), MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        // Ponds: a level water surface over the carved bed.
        var water = Scenery.Water(seeThrough: true);
        foreach (var w in course.Holes[0].Water)
            if (terrain.WaterLevel(w) is { } level && Outline(w) is { } outline)
                parent.AddChild(Scenery.Flat(outline, (float)level, water));
        return material;
    }

    private static IReadOnlyList<P2>? Outline(Shape s) => s switch { Polygon p => p.Points, Ellipse e => e.Outline(48), _ => null };

    /// <summary>
    /// Height of the drawn ground at a point: the mesh's triangles, which differ slightly from
    /// <see cref="Terrain.HeightAt(P2)"/>'s bilinear blend inside each cell. For things that must sit exactly on it.
    /// </summary>
    public static float MeshHeightAt(Terrain t, P2 p)
    {
        double fx = Math.Clamp((p.X - t.Origin.X) / t.Cell, 0, t.CountX - 1.000001);
        double fz = Math.Clamp((p.Z - t.Origin.Z) / t.Cell, 0, t.CountZ - 1.000001);
        int i = (int)fx, j = (int)fz;
        float tx = (float)(fx - i), tz = (float)(fz - j);
        // Each cell is split along the diagonal from (i + 1, j) to (i, j + 1) (see BuildChunk).
        float h10 = t.HeightAt(i + 1, j), h01 = t.HeightAt(i, j + 1);
        if (tx + tz <= 1)
        {
            float h00 = t.HeightAt(i, j);
            return h00 + tx * (h10 - h00) + tz * (h01 - h00);
        }
        float h11 = t.HeightAt(i + 1, j + 1);
        return h11 + (1 - tx) * (h01 - h11) + (1 - tz) * (h10 - h11);
    }

    private static Vector3 Normal(Terrain t, int i, int j)
    {
        // Height slope along course X and Z, then into Godot axes (x = course Z, z = -course X).
        double gx = (t.HeightAt(i + 1, j) - t.HeightAt(i - 1, j)) / (2 * t.Cell);
        double gz = (t.HeightAt(i, j + 1) - t.HeightAt(i, j - 1)) / (2 * t.Cell);
        return new Vector3((float)-gz, 1, (float)gx).Normalized();
    }

    private static ArrayMesh BuildChunk(Terrain t, int i0, int j0, int ni, int nj)
    {
        var verts = new Vector3[(ni + 1) * (nj + 1)];
        var normals = new Vector3[verts.Length];
        for (int a = 0; a <= ni; a++)
            for (int b = 0; b <= nj; b++)
            {
                int i = i0 + a, j = j0 + b, k = a * (nj + 1) + b;
                verts[k] = Scenery.ToGodot(t.Point(i, j), t.HeightAt(i, j));
                normals[k] = Normal(t, i, j);
            }
        var indices = new int[ni * nj * 6];
        int n = 0;
        for (int a = 0; a < ni; a++)
            for (int b = 0; b < nj; b++)
            {
                int k = a * (nj + 1) + b;
                // Wound so the faces point up (seen from above, Godot's front faces are clockwise).
                indices[n++] = k; indices[n++] = k + nj + 1; indices[n++] = k + 1;
                indices[n++] = k + 1; indices[n++] = k + nj + 1; indices[n++] = k + nj + 2;
            }
        return Surface(verts, normals, indices);
    }

    /// <summary>A ring from the edge of the modelled ground out to the horizon, level with the edge.</summary>
    private static ArrayMesh BuildApron(Terrain t)
    {
        // Walk the grid's edge once around.
        var edge = new List<(int I, int J)>();
        for (int j = 0; j < t.CountZ - 1; j++) edge.Add((0, j));
        for (int i = 0; i < t.CountX - 1; i++) edge.Add((i, t.CountZ - 1));
        for (int j = t.CountZ - 1; j > 0; j--) edge.Add((t.CountX - 1, j));
        for (int i = t.CountX - 1; i > 0; i--) edge.Add((i, 0));

        var centre = t.Area.Center;
        var verts = new Vector3[edge.Count * 2];
        var normals = new Vector3[verts.Length];
        for (int e = 0; e < edge.Count; e++)
        {
            var p = t.Point(edge[e].I, edge[e].J);
            float h = t.HeightAt(edge[e].I, edge[e].J);
            var outward = p - centre;
            outward *= Apron / outward.Length;
            verts[2 * e] = Scenery.ToGodot(p, h);
            verts[2 * e + 1] = Scenery.ToGodot(p + outward, h);
            normals[2 * e] = normals[2 * e + 1] = Vector3.Up;
        }
        var indices = new List<int>();
        for (int e = 0; e < edge.Count; e++)
        {
            int a = 2 * e, b = 2 * ((e + 1) % edge.Count);
            indices.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
        }
        return Surface(verts, normals, indices.ToArray());
    }

    private static ArrayMesh Surface(Vector3[] verts, Vector3[] normals, int[] indices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // ---- coverage texture ------------------------------------------------------------------------

    /// <summary>
    /// RGBA coverage of the playing areas over the terrain: R fairways, tees and the fringe round greens,
    /// G greens, B bunkers, A water; and a second texture with the first cut (R), ponds that have their own level
    /// surface (G), cart paths (B) and their bridges over water (A). Rows run along course X, columns along course Z.
    /// </summary>
    private static (ImageTexture Mask, ImageTexture Cut) BuildMask(Course course, Terrain terrain)
    {
        var area = terrain.Area;
        int w = (int)Math.Ceiling((area.Max.Z - area.Min.Z) / Texel), h = (int)Math.Ceiling((area.Max.X - area.Min.X) / Texel);
        var data = new byte[w * h * 4];
        var cut = new byte[w * h * 4];
        var hole = course.Holes[0];   // a mapped course's holes share their feature lists
        void Paint(byte[] into, IEnumerable<Shape> shapes, int channel)
        {
            foreach (var s in shapes) Rasterise(s, area, w, h, into, channel);
        }
        Paint(data, hole.Fairways.Concat(hole.Tees), 0);
        Paint(data, hole.Greens.Select(g => Scenery.Grown(g, Hole.FringeWidth)), 0);   // fringe
        Paint(data, hole.Greens, 1);
        Paint(data, hole.Bunkers, 2);
        Paint(data, hole.Water, 3);
        // First cut of rough (the rest of the rough is deep rough): the shared areas, plus around every hole's tee.
        Paint(cut, Scenery.FirstCut(hole).Concat(course.Holes.Select(Scenery.TeeCut)), 0);
        Paint(cut, hole.Water.Where(w => terrain.WaterLevel(w) != null), 1);
        Paint(cut, hole.CartPaths, 2);
        Paint(cut, hole.Bridges, 3);

        ImageTexture Texture(byte[] bytes)
        {
            var image = Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
            image.GenerateMipmaps();
            return ImageTexture.CreateFromImage(image);
        }
        return (Texture(data), Texture(cut));
    }

    /// <summary>Adds a shape's coverage (0-255, anti-aliased) to one channel, keeping the larger value.</summary>
    private static void Rasterise(Shape shape, Bounds area, int w, int h, byte[] data, int channel)
    {
        var box = shape.Box;
        int r0 = Math.Max(0, (int)((box.Min.X - area.Min.X) / Texel) - 1), r1 = Math.Min(h - 1, (int)((box.Max.X - area.Min.X) / Texel) + 1);
        int c0 = Math.Max(0, (int)((box.Min.Z - area.Min.Z) / Texel) - 1), c1 = Math.Min(w - 1, (int)((box.Max.Z - area.Min.Z) / Texel) + 1);
        if (r0 > r1 || c0 > c1) return;
        int cols = c1 - c0 + 1;
        var cover = new float[(r1 - r0 + 1) * cols];

        if (shape is Polygon poly)
        {
            // Scanlines: 4 per texel row; spans add their exact overlap with each texel column.
            const int sub = 4;
            var pts = poly.Points;
            var xs = new List<double>();
            for (int r = r0; r <= r1; r++)
                for (int s = 0; s < sub; s++)
                {
                    double x = area.Min.X + (r + (s + 0.5) / sub) * Texel;
                    xs.Clear();
                    for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                    {
                        P2 a = pts[i], b = pts[j];
                        if ((a.X > x) != (b.X > x)) xs.Add(a.Z + (x - a.X) / (b.X - a.X) * (b.Z - a.Z));
                    }
                    xs.Sort();
                    for (int k = 0; k + 1 < xs.Count; k += 2)
                    {
                        double z0 = (xs[k] - area.Min.Z) / Texel, z1 = (xs[k + 1] - area.Min.Z) / Texel;
                        for (int c = Math.Max(c0, (int)z0); c <= Math.Min(c1, (int)z1); c++)
                        {
                            double overlap = Math.Min(z1, c + 1) - Math.Max(z0, c);
                            if (overlap > 0) cover[(r - r0) * cols + (c - c0)] += (float)(overlap / sub);
                        }
                    }
                }
        }
        else
        {
            // Other shapes: from distance to the edge (one texel of soft edge).
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    var p = new P2(area.Min.X + (r + 0.5) * Texel, area.Min.Z + (c + 0.5) * Texel);
                    cover[(r - r0) * cols + (c - c0)] = (float)Math.Clamp(0.5 - shape.DistanceTo(p) / Texel, 0, 1);
                }
        }

        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                int idx = (r * w + c) * 4 + channel;
                byte v = (byte)Math.Clamp((int)(cover[(r - r0) * cols + (c - c0)] * 255 + 0.5), 0, 255);
                if (v > data[idx]) data[idx] = v;
            }
    }
}
