using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// Leaf cards for the nearby trees (see <see cref="TreeFactory"/>): flat quads carrying a picture of a spray of leaves
/// or needles, cut out by its alpha, set all over a crown so its outline and surface break up into leaves. The
/// pictures are painted in code at start-up (no image files), in greys with a little hue so each tree's tint colours them.
/// </summary>
public static class LeafCards
{
    private const string ShaderCode = """
        shader_type spatial;
        render_mode cull_disabled;
        uniform sampler2D leaves : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
        varying vec3 world_pos;
        """ + Scenery.CloudGlsl + """
        void vertex() {
            // The same sway as the crown it covers (TreeFactory's foliage shader), so cards and crown move together.
            vec3 origin = MODEL_MATRIX[3].xyz;
            float phase = dot(origin.xz, vec2(0.11, 0.07));
            float sway = max(VERTEX.y, 0.0) * max(VERTEX.y, 0.0);
            VERTEX.x += sin(TIME * 1.3 + phase) * 0.006 * sway;
            VERTEX.z += sin(TIME * 0.9 + phase * 1.7) * 0.004 * sway;
            world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        }
        void fragment() {
            vec4 t = texture(leaves, UV);
            // Lit as the crown's surface there (the card's normal points out of the crown), whichever side is seen.
            if (!FRONT_FACING) NORMAL = -NORMAL;
            // A little brighter than the crown beneath: the outer leaves are the ones in the light.
            vec3 c = COLOR.rgb * t.rgb * 1.2;
            ALBEDO = c * cloud_light(world_pos, TIME, PROJECTION_MATRIX);
            ALPHA = t.a;
            ALPHA_SCISSOR_THRESHOLD = 0.4;
            ALPHA_ANTIALIASING_EDGE = 0.25;
            ALPHA_TEXTURE_COORDINATE = UV * vec2(textureSize(leaves, 0));
            BACKLIGHT = c * 0.45;
            ROUGHNESS = 0.8;
            SPECULAR = 0.25;
        }
        """;

    private static ShaderMaterial? _broadleaf, _conifer;

    /// <summary>Cards of broad leaves: a round spray, its middle at the card's centre.</summary>
    public static ShaderMaterial Broadleaf => _broadleaf ??= Material(PaintLeaves());

    /// <summary>Cards of needles: a twig along the card from its base (u = 0) to its tip (u = 1), needles either side.</summary>
    public static ShaderMaterial Conifer => _conifer ??= Material(PaintNeedles());

    private static ShaderMaterial Material(Image picture)
    {
        picture.GenerateMipmaps();
        var m = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        m.SetShaderParameter("leaves", ImageTexture.CreateFromImage(picture));
        return m;
    }

    private const int Size = 256;

    /// <summary>About 50 pointed leaves round the middle, each turned its own way, with a darker midrib.</summary>
    private static Image PaintLeaves()
    {
        var img = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        var rng = new RandomNumberGenerator { Seed = 7 };
        for (int k = 0; k < 50; k++)
        {
            // Denser in the middle, out to near the edge.
            float r = Size * 0.4f * Mathf.Sqrt(rng.Randf()), a = rng.Randf() * Mathf.Tau;
            var at = new Vector2(Size / 2f, Size / 2f) + Vector2.FromAngle(a) * r;
            float length = rng.RandfRange(30, 46), width = length * rng.RandfRange(0.34f, 0.44f);
            // Pointing roughly away from the middle, as leaves on a spray do.
            float turn = a + rng.RandfRange(-0.7f, 0.7f);
            float shade = rng.RandfRange(0.8f, 1.0f);
            PaintLeaf(img, at, Vector2.FromAngle(turn), length, width, new Color(shade * 0.93f, shade, shade * 0.8f));
        }
        return img;
    }

    /// <summary>A leaf: pointed at both ends, widest a third of the way along, from <paramref name="at"/> along <paramref name="dir"/>.</summary>
    private static void PaintLeaf(Image img, Vector2 at, Vector2 dir, float length, float width, Color colour)
    {
        var side = new Vector2(-dir.Y, dir.X);
        int x0 = (int)Mathf.Max(0, at.X - length - 2), x1 = (int)Mathf.Min(Size - 1, at.X + length + 2);
        int y0 = (int)Mathf.Max(0, at.Y - length - 2), y1 = (int)Mathf.Min(Size - 1, at.Y + length + 2);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var d = new Vector2(x + 0.5f, y + 0.5f) - at;
                float along = d.Dot(dir) / length, across = d.Dot(side);
                if (along < 0 || along > 1) continue;
                float half = width / 2 * Mathf.Sin(Mathf.Pi * Mathf.Pow(along, 0.75f));
                if (Mathf.Abs(across) > half) continue;
                float rib = Mathf.Abs(across) < 0.9f ? 0.8f : 1f;
                // Lighter toward the edges, as a leaf catches the light across its curve.
                float curve = 0.85f + 0.15f * Mathf.Abs(across) / Mathf.Max(half, 0.01f);
                img.SetPixel(x, y, new Color(colour.R * rib * curve, colour.G * rib * curve, colour.B * rib * curve));
            }
    }

    /// <summary>A twig along the middle (base on the left) with tufts of needles angled forward on both sides.</summary>
    private static Image PaintNeedles()
    {
        var img = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        var rng = new RandomNumberGenerator { Seed = 9 };
        float mid = Size / 2f;
        PaintLine(img, new Vector2(4, mid), new Vector2(Size - 6, mid), 2.5f, new Color(0.5f, 0.45f, 0.35f));
        for (int k = 0; k < 220; k++)
        {
            float u = rng.RandfRange(8, Size - 10);
            // Needles longer in the middle of the twig, shorter at its tip and base.
            float reach = Size * 0.42f * Mathf.Sin(Mathf.Pi * u / Size) * rng.RandfRange(0.6f, 1.0f);
            float sideOf = rng.Randf() < 0.5f ? -1 : 1;
            float angle = rng.RandfRange(0.5f, 1.1f);   // forward of square to the twig
            var from = new Vector2(u, mid + sideOf * rng.RandfRange(0, 3));
            var to = from + new Vector2(Mathf.Cos(angle), sideOf * Mathf.Sin(angle)) * reach;
            float shade = rng.RandfRange(0.6f, 1.0f);
            PaintLine(img, from, to, 1.6f, new Color(shade * 0.9f, shade, shade * 0.85f));
        }
        return img;
    }

    private static void PaintLine(Image img, Vector2 a, Vector2 b, float width, Color colour)
    {
        int steps = (int)Mathf.Ceil(a.DistanceTo(b) * 2);
        for (int s = 0; s <= steps; s++)
        {
            var p = a.Lerp(b, s / (float)Mathf.Max(steps, 1));
            for (int y = (int)(p.Y - width); y <= (int)(p.Y + width); y++)
                for (int x = (int)(p.X - width); x <= (int)(p.X + width); x++)
                    if (x >= 0 && y >= 0 && x < Size && y < Size && new Vector2(x + 0.5f, y + 0.5f).DistanceTo(p) <= width / 2 + 0.5f)
                        img.SetPixel(x, y, colour);
        }
    }

    /// <summary>Accumulates cards (two triangles each, with UVs, normals and vertex colours) into an ArrayMesh.</summary>
    public sealed class Builder
    {
        private readonly List<Vector3> _v = new(), _n = new();
        private readonly List<Color> _c = new();
        private readonly List<Vector2> _uv = new();

        public bool IsEmpty => _v.Count == 0;

        /// <summary>
        /// A card from <paramref name="baseMid"/> (u = 0) to <paramref name="tipMid"/> (u = 1), reaching
        /// <paramref name="across"/> either side (v from 0 to 1), lit as if facing <paramref name="normal"/>.
        /// </summary>
        public void Card(Vector3 baseMid, Vector3 tipMid, Vector3 across, Vector3 normal, Color shade)
        {
            Vector3 a = baseMid - across, b = tipMid - across, c = tipMid + across, d = baseMid + across;
            Vertex(a, new Vector2(0, 0), normal, shade); Vertex(c, new Vector2(1, 1), normal, shade); Vertex(b, new Vector2(1, 0), normal, shade);
            Vertex(a, new Vector2(0, 0), normal, shade); Vertex(d, new Vector2(0, 1), normal, shade); Vertex(c, new Vector2(1, 1), normal, shade);
        }

        private void Vertex(Vector3 p, Vector2 uv, Vector3 n, Color c) { _v.Add(p); _uv.Add(uv); _n.Add(n); _c.Add(c); }

        public ArrayMesh Build()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _v.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _n.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _c.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = _uv.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }
}
