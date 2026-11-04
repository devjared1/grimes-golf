using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SquareGspro.Core.Course;

/// <summary>
/// A short hash of what a course plays like (each hole's number, par, tee, pin and aim point, how many fairways,
/// greens, bunkers and ponds it has, and the ground heights), so two PCs can check they built the same course from
/// their copies of the map data before playing online. Anything that changes where a ball goes changes it.
/// </summary>
public static class CourseFingerprint
{
    public static string Of(Course course) => Of(course.Holes, course.Terrain);

    public static string Of(IReadOnlyList<Hole> holes, Terrain? terrain)
    {
        var sb = new StringBuilder();
        string F(double v) => Math.Round(v, 2).ToString("0.00", CultureInfo.InvariantCulture);
        string P(P2 p) => $"{F(p.X)},{F(p.Z)}";
        foreach (var h in holes)
            sb.Append($"{h.Number}|{h.Par}|{P(h.Tee)}|{P(h.Pin)}|{P(h.TeeAim)}|{h.Fairways.Count}|{h.Greens.Count}|{h.Bunkers.Count}|{h.Water.Count}|{h.CartPaths.Count}\n");
        if (terrain != null)
        {
            sb.Append($"T|{terrain.CountX}|{terrain.CountZ}|{F(terrain.Cell)}|{F(terrain.Datum)}|{P(terrain.Origin)}|");
            // A sample of heights across the grid (every cell would be slow to hash on big courses and adds nothing).
            int step = Math.Max(1, terrain.CountX * terrain.CountZ / 4096);
            double sum = 0;
            for (int k = 0; k < terrain.CountX * terrain.CountZ; k += step) sum += terrain.HeightAt(k / terrain.CountZ, k % terrain.CountZ);
            sb.Append(F(sum));
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
