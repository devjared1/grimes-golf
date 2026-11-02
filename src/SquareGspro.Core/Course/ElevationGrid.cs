using System.IO.Compression;

namespace SquareGspro.Core.Course;

/// <summary>
/// Ground heights on a latitude/longitude grid, read from the app's .dem format (made by
/// <c>tools/fetch-elevation.ps1</c> from USGS 3DEP data): gzipped, "SQDEM1", width, height, west/south/east/north
/// edges (degrees), base elevation (m), then per row (north to south) centimetre heights above the base,
/// delta-encoded along the row as int16.
/// </summary>
public sealed class ElevationGrid
{
    private readonly ushort[] _cm;

    private ElevationGrid(int width, int height, double west, double south, double east, double north, float baseMetres, ushort[] cm)
    {
        Width = width; Height = height; West = west; South = south; East = east; North = north; BaseMetres = baseMetres;
        _cm = cm;
    }

    public int Width { get; }
    public int Height { get; }
    public double West { get; }
    public double South { get; }
    public double East { get; }
    public double North { get; }
    /// <summary>Lowest height in the grid (m above sea level).</summary>
    public float BaseMetres { get; }

    public static ElevationGrid FromResource(string file)
    {
        using var s = typeof(ElevationGrid).Assembly.GetManifestResourceStream("SquareGspro.Core.Course.Data." + file)
            ?? throw new FileNotFoundException($"Elevation data '{file}' is not embedded in SquareGspro.Core.");
        return Load(s);
    }

    public static ElevationGrid Load(Stream stream)
    {
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var r = new BinaryReader(new BufferedStream(gz, 1 << 16));
        if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(6)) != "SQDEM1") throw new InvalidDataException("Not a .dem elevation file.");
        int width = r.ReadInt32(), height = r.ReadInt32();
        double west = r.ReadDouble(), south = r.ReadDouble(), east = r.ReadDouble(), north = r.ReadDouble();
        float baseMetres = r.ReadSingle();
        if (width <= 1 || height <= 1 || (long)width * height > 64_000_000) throw new InvalidDataException("Bad .dem size.");

        var cm = new ushort[width * height];
        var row = new byte[width * 2];
        for (int y = 0; y < height; y++)
        {
            r.BaseStream.ReadExactly(row);
            int v = 0;
            for (int x = 0; x < width; x++)
            {
                v += (short)(row[2 * x] | row[2 * x + 1] << 8);
                cm[y * width + x] = (ushort)v;
            }
        }
        return new ElevationGrid(width, height, west, south, east, north, baseMetres, cm);
    }

    public bool Covers(double lat, double lon) => lat >= South && lat <= North && lon >= West && lon <= East;

    /// <summary>Height (m above sea level) at a point, interpolated between cell centres; clamped at the edges.</summary>
    public double At(double lat, double lon)
    {
        double fx = (lon - West) / (East - West) * Width - 0.5;
        double fy = (North - lat) / (North - South) * Height - 0.5;
        fx = Math.Clamp(fx, 0, Width - 1.000001);
        fy = Math.Clamp(fy, 0, Height - 1.000001);
        int x = (int)fx, y = (int)fy;
        double tx = fx - x, ty = fy - y;
        double Cm(int cx, int cy) => _cm[cy * Width + cx];
        double top = Cm(x, y) * (1 - tx) + Cm(x + 1, y) * tx;
        double bottom = Cm(x, y + 1) * (1 - tx) + Cm(x + 1, y + 1) * tx;
        return BaseMetres + (top * (1 - ty) + bottom * ty) / 100;
    }
}

/// <summary>
/// The ground of a mapped course in its own frame (metres, X north, Z east around the course origin): a regular grid
/// of heights relative to <see cref="Datum"/> (the height at the course origin), resampled from an
/// <see cref="ElevationGrid"/> and smoothed (lightly everywhere, more on greens, where lidar noise would otherwise
/// read as bumps). Physics and the rendered ground both use this grid, so the ball sits on what you see.
/// </summary>
public sealed class Terrain
{
    private readonly float[] _h;

    /// <summary>A flat or synthetic terrain from a height function (tests, tools).</summary>
    public Terrain(Bounds area, double cell, Func<P2, double> height)
    {
        Cell = cell;
        Origin = area.Min;
        CountX = (int)Math.Ceiling((area.Max.X - area.Min.X) / cell) + 1;
        CountZ = (int)Math.Ceiling((area.Max.Z - area.Min.Z) / cell) + 1;
        _h = new float[CountX * CountZ];
        for (int i = 0; i < CountX; i++)
            for (int j = 0; j < CountZ; j++)
                _h[i * CountZ + j] = (float)height(Point(i, j));
    }

    /// <summary>
    /// Samples <paramref name="grid"/> over <paramref name="area"/> (course coordinates; the projection is the
    /// importer's: lat = lat0 + X / metresPerDegreeLat, lon = lon0 + Z / metresPerDegreeLon).
    /// </summary>
    public Terrain(ElevationGrid grid, double lat0, double lon0, double metresPerDegreeLat, double metresPerDegreeLon,
                   Bounds area, IReadOnlyList<Shape> greens, double cell = 2.0)
        : this(area, cell, p => grid.At(lat0 + p.X / metresPerDegreeLat, lon0 + p.Z / metresPerDegreeLon))
    {
        Datum = grid.At(lat0, lon0);
        for (int k = 0; k < _h.Length; k++) _h[k] -= (float)Datum;

        // Light smoothing everywhere (~2 m) takes out lidar noise.
        Blur(_h, 1, 2);
        foreach (var g in greens) ShapeGreen(g);
    }

    /// <summary>Steepest overall tilt a green is given (real greens are rarely over 3-4%).</summary>
    public const double MaxGreenTilt = 0.035;

    /// <summary>
    /// Greens are built surfaces: fit the green's overall tilt (capped at <see cref="MaxGreenTilt"/>), keep a little of
    /// its contour, and blend that into the surrounding ground over a few metres. Without this, lidar noise and the
    /// banks around raised greens read as bumps and steep slopes.
    /// </summary>
    private void ShapeGreen(Shape green)
    {
        const double blend = 4, contour = 0.35, maxContour = 0.25;
        var b = green.Box.Expand(blend);
        int i0 = Math.Max(0, (int)((b.Min.X - Origin.X) / Cell)), i1 = Math.Min(CountX - 1, (int)Math.Ceiling((b.Max.X - Origin.X) / Cell));
        int j0 = Math.Max(0, (int)((b.Min.Z - Origin.Z) / Cell)), j1 = Math.Min(CountZ - 1, (int)Math.Ceiling((b.Max.Z - Origin.Z) / Cell));

        // Least-squares plane through the grid points on the green.
        var on = new List<(int I, int J)>();
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
                if (green.Contains(Point(i, j))) on.Add((i, j));
        if (on.Count < 4) return;
        double mx = on.Average(q => Point(q.I, q.J).X), mz = on.Average(q => Point(q.I, q.J).Z), mh = on.Average(q => _h[q.I * CountZ + q.J]);
        double sxx = 0, szz = 0, sxz = 0, sxh = 0, szh = 0;
        foreach (var (i, j) in on)
        {
            var p = Point(i, j);
            double dx = p.X - mx, dz = p.Z - mz, dh = _h[i * CountZ + j] - mh;
            sxx += dx * dx; szz += dz * dz; sxz += dx * dz; sxh += dx * dh; szh += dz * dh;
        }
        double det = sxx * szz - sxz * sxz;
        if (Math.Abs(det) < 1e-9) return;
        double a = (sxh * szz - szh * sxz) / det, c = (szh * sxx - sxh * sxz) / det;
        double fit(P2 p) => mh + a * (p.X - mx) + c * (p.Z - mz);
        double tilt = Math.Sqrt(a * a + c * c), k = tilt > MaxGreenTilt ? MaxGreenTilt / tilt : 1;
        double surface(P2 p) => mh + k * (a * (p.X - mx) + c * (p.Z - mz));

        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
            {
                var p = Point(i, j);
                double d = green.DistanceTo(p);
                if (d >= blend) continue;
                int idx = i * CountZ + j;
                double shaped = surface(p) + Math.Clamp(_h[idx] - fit(p), -maxContour, maxContour) * contour;
                double w = d <= 0 ? 1 : 1 - d / blend;
                w = w * w * (3 - 2 * w);   // smooth shoulder
                _h[idx] = (float)(_h[idx] * (1 - w) + shaped * w);
            }
    }

    /// <summary>Height above sea level that counts as 0 (the course origin's height); 0 for synthetic terrain.</summary>
    public double Datum { get; }

    /// <summary>Grid layout: point (i, j) is at <see cref="Origin"/> + (i, j) × <see cref="Cell"/>.</summary>
    public P2 Origin { get; }
    public double Cell { get; }
    public int CountX { get; }
    public int CountZ { get; }
    public Bounds Area => new(Origin, Point(CountX - 1, CountZ - 1));

    public P2 Point(int i, int j) => new(Origin.X + i * Cell, Origin.Z + j * Cell);

    /// <summary>Height at grid point (i, j), clamped to the grid.</summary>
    public float HeightAt(int i, int j) => _h[Math.Clamp(i, 0, CountX - 1) * CountZ + Math.Clamp(j, 0, CountZ - 1)];

    /// <summary>Ground height (m) at a point, interpolated between grid points; flat beyond the edges.</summary>
    public double HeightAt(P2 p)
    {
        double fx = Math.Clamp((p.X - Origin.X) / Cell, 0, CountX - 1.000001);
        double fz = Math.Clamp((p.Z - Origin.Z) / Cell, 0, CountZ - 1.000001);
        int i = (int)fx, j = (int)fz;
        double tx = fx - i, tz = fz - j;
        int k = i * CountZ + j;
        double a = _h[k] * (1 - tz) + _h[k + 1] * tz;
        double b = _h[k + CountZ] * (1 - tz) + _h[k + CountZ + 1] * tz;
        return a * (1 - tx) + b * tx;
    }

    /// <summary>Slope at a point: rise per metre toward +X and +Z.</summary>
    public P2 Gradient(P2 p, double step = 0.5) => new(
        (HeightAt(p + new P2(step, 0)) - HeightAt(p - new P2(step, 0))) / (2 * step),
        (HeightAt(p + new P2(0, step)) - HeightAt(p - new P2(0, step))) / (2 * step));

    /// <summary>Repeated box blur (separable, running sums) with the given radius in cells.</summary>
    private void Blur(float[] h, int radius, int passes)
    {
        var tmp = new float[h.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            BlurLines(h, tmp, CountX, CountZ, CountZ, 1, radius);   // along Z
            BlurLines(tmp, h, CountZ, CountX, 1, CountZ, radius);   // along X
        }
    }

    private static void BlurLines(float[] src, float[] dst, int lines, int length, int lineStride, int step, int r)
    {
        for (int l = 0; l < lines; l++)
        {
            int start = l * lineStride;
            double sum = 0;
            int Idx(int x) => start + Math.Clamp(x, 0, length - 1) * step;
            for (int x = -r; x <= r; x++) sum += src[Idx(x)];
            for (int x = 0; x < length; x++)
            {
                dst[start + x * step] = (float)(sum / (2 * r + 1));
                sum += src[Idx(x + r + 1)] - src[Idx(x - r)];
            }
        }
    }
}
