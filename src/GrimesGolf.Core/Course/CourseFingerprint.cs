using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GrimesGolf.Core.Course;

/// <summary>
/// A short hash of what a course plays like (each hole's number, par, tee, pin and aim point, the outline of everything
/// a ball can land on, the ground heights and the trees), so two PCs can check they built the same course from their
/// copies of its files before playing online. Anything that changes where a ball goes changes it.
/// </summary>
public static class CourseFingerprint
{
    public static string Of(Course course) => Of(course.Holes, course.Terrain, course.Trees);

    public static string Of(IReadOnlyList<Hole> holes, Terrain? terrain, IReadOnlyList<CourseTree>? trees = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sb = new StringBuilder();
        string F(double v) => Math.Round(v, 2).ToString("0.00", CultureInfo.InvariantCulture);
        string P(P2 p) => $"{F(p.X)},{F(p.Z)}";
        void Shapes(string kind, IReadOnlyList<Shape> shapes)
        {
            sb.Append(kind).Append(shapes.Count).Append(':');
            foreach (var shape in shapes)
                switch (shape)
                {
                    case Polygon s: foreach (var p in s.Points) sb.Append(P(p)).Append(' '); sb.Append(';'); break;
                    case Strip s: foreach (var p in s.Points) sb.Append(P(p)).Append(' '); sb.Append(F(s.Width)).Append(';'); break;
                    case Ellipse s: sb.Append($"{P(s.Center)} {F(s.RadiusX)} {F(s.RadiusZ)} {F(s.RotationDeg)};"); break;
                }
            sb.Append('\n');
        }
        void Flush()
        {
            hash.AppendData(Encoding.UTF8.GetBytes(sb.ToString()));
            sb.Clear();
        }

        foreach (var h in holes)
        {
            sb.Append($"{h.Number}|{h.Par}|{P(h.Tee)}|{P(h.Pin)}|{P(h.TeeAim)}\n");
            Shapes("F", h.Fairways);
            Shapes("G", h.Greens);
            Shapes("B", h.Bunkers);
            Shapes("W", h.Water);
            Shapes("C", h.CartPaths);
            Shapes("R", h.Bridges);
            Flush();
        }
        if (terrain != null)
        {
            sb.Append($"T|{terrain.CountX}|{terrain.CountZ}|{F(terrain.Cell)}|{F(terrain.Datum)}|{P(terrain.Origin)}\n");
            Flush();
            // Every height, to the centimetre (as whole numbers: this is the bulk of it, and a big course has millions).
            var row = new int[terrain.CountZ];
            for (int i = 0; i < terrain.CountX; i++)
            {
                for (int j = 0; j < terrain.CountZ; j++) row[j] = (int)Math.Round(terrain.HeightAt(i, j) * 100);
                hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes<int>(row));
            }
        }
        if (trees != null)
        {
            foreach (var t in trees) sb.Append($"{P(t.At)} {F(t.Height)} {F(t.Crown)}\n");
            Flush();
        }
        return Convert.ToHexString(hash.GetHashAndReset(), 0, 8).ToLowerInvariant();
    }
}
