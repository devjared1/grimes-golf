using System.Globalization;
using System.IO.Compression;

namespace GrimesGolf.Core.Course;

/// <summary>How a tree stands: on its own, in a group of 2-5 whose crowns touch, or in woods (6 or more).</summary>
public enum TreeKind { Single, Group, Dense }

/// <summary>A tree on the course: where its trunk is, how tall it is and how far its crown reaches (radius), in metres.</summary>
public readonly record struct CourseTree(P2 At, double Height, double Crown, TreeKind Kind);

/// <summary>
/// Trees measured from lidar, read from the gzipped CSV made by <c>tools/fetch-canopy.ps1</c>: <c>#</c> comment lines,
/// a header, then one row per tree, <c>lat,lon,height_m,crown_m,cluster,kind</c>.
/// </summary>
public static class TreeSurvey
{
    /// <summary>A tree as surveyed, before it's placed in a course's frame.</summary>
    public readonly record struct Tree(double Lat, double Lon, double Height, double Crown, TreeKind Kind);

    public static IReadOnlyList<Tree> Load(Stream stream)
    {
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var trees = new List<Tree>();
        bool header = false;
        int lineNo = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Length == 0 || line[0] == '#') continue;
            if (!header)
            {
                if (!line.StartsWith("lat,lon,height_m,crown_m", StringComparison.Ordinal)) throw new InvalidDataException("Not a trees CSV.");
                header = true;
                continue;
            }
            var f = line.Split(',');
            if (f.Length < 6) throw new InvalidDataException($"Trees CSV line {lineNo} has {f.Length} fields.");
            var kind = f[5] switch
            {
                "single" => TreeKind.Single,
                "group" => TreeKind.Group,
                "dense" => TreeKind.Dense,
                _ => throw new InvalidDataException($"Trees CSV line {lineNo}: unknown kind '{f[5]}'."),
            };
            trees.Add(new Tree(Num(f[0]), Num(f[1]), Num(f[2]), Num(f[3]), kind));
        }
        if (!header) throw new InvalidDataException("Not a trees CSV.");
        return trees;
    }

    private static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
