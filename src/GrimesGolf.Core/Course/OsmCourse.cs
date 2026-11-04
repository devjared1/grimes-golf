using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace GrimesGolf.Core.Course;

/// <summary>
/// Builds a <see cref="Course"/> from OpenStreetMap data (an .osm XML file, e.g. from <c>tools/fetch-osm-course.ps1</c>).
/// Uses golf=hole lines (ref, par, name), golf=tee/green/fairway/bunker, water hazards and natural=water, ditches and
/// streams, golf=cartpath (or paths tagged golf_cart=yes), woods, and the leisure=golf_course outline as the out-of-bounds line. Coordinates are projected to metres around the
/// course centre: X north, Z east.
/// Holes without a mapped fairway get one generated along the hole line. Duplicate or missing hole numbers are
/// repaired by routing (the next hole's tee is the one nearest the previous green). What was fixed or generated is
/// listed in <see cref="Course.Notes"/>.
/// </summary>
public static class OsmCourse
{
    public const string Credit = "Course map © OpenStreetMap contributors (ODbL)";

    /// <summary>Width of generated fairways (35 yd), and of the apron generated in front of par-3 greens.</summary>
    public const double FairwayWidth = 32, ApronWidth = 24;
    /// <summary>Generated fairways start this far from the tee on par 4s and 5s (about 100 yd).</summary>
    public const double FairwayStart = 90;
    /// <summary>Width of a cart path mapped as a line without a width tag (m).</summary>
    public const double CartPathWidth = 2.5;

    private const double EarthRadius = 6371008.8;
    /// <summary>How far the modelled ground extends past the course (beyond it the ground continues flat).</summary>
    private const double TerrainMargin = 200;
    /// <summary>How far past the course surveyed trees are kept (inside the modelled ground).</summary>
    private const double TreeMargin = 150;

    private sealed record Way(long Id, List<long> Nodes, Dictionary<string, string> Tags)
    {
        public bool Closed => Nodes.Count >= 4 && Nodes[0] == Nodes[^1];
        public string? Tag(string k) => Tags.GetValueOrDefault(k);
    }

    private sealed record Relation(long Id, List<(long Ref, string Role)> Members, Dictionary<string, string> Tags);

    private sealed record HoleLine(long WayId, int? Ref, int? Par, string Name, List<P2> Route);

    /// <param name="elevationFile">Optional embedded .dem with the course's ground heights.</param>
    /// <param name="treesFile">Optional embedded trees CSV (<see cref="TreeSurvey"/>) with the course's real trees.</param>
    public static Course FromResource(string file, string name, string? elevationFile = null, string? treesFile = null) =>
        FromResource(file, name, elevationFile, shapeWater: true, treesFile);

    /// <param name="shapeWater">False leaves ponds as the elevation data has them (tests check the map against them).</param>
    internal static Course FromResource(string file, string name, string? elevationFile, bool shapeWater, string? treesFile = null)
    {
        using var s = typeof(OsmCourse).Assembly.GetManifestResourceStream("GrimesGolf.Core.Course.Data." + file)
            ?? throw new FileNotFoundException($"Course data '{file}' is not embedded in GrimesGolf.Core.");
        return Load(s, name, elevationFile == null ? null : ElevationGrid.FromResource(elevationFile), shapeWater,
            treesFile == null ? null : TreeSurvey.FromResource(treesFile));
    }

    /// <param name="trees">Surveyed trees (any that are far outside the course are left out).</param>
    public static Course Load(Stream osm, string? name = null, ElevationGrid? elevation = null, IReadOnlyList<TreeSurvey.Tree>? trees = null) =>
        Load(osm, name, elevation, shapeWater: true, trees);

    private static Course Load(Stream osm, string? name, ElevationGrid? elevation, bool shapeWater, IReadOnlyList<TreeSurvey.Tree>? survey)
    {
        XDocument doc;
        using (var reader = XmlReader.Create(osm, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidDataException("Empty OSM file.");

        var nodes = new Dictionary<long, (double Lat, double Lon)>();
        foreach (var n in root.Elements("node"))
            nodes[Long(n, "id")] = (Dbl(n, "lat"), Dbl(n, "lon"));
        var ways = root.Elements("way").Select(w => new Way(
                Long(w, "id"),
                w.Elements("nd").Select(nd => Long(nd, "ref")).Where(nodes.ContainsKey).ToList(),
                Tags(w)))
            .ToDictionary(w => w.Id);
        var relations = root.Elements("relation").Select(r => new Relation(
                Long(r, "id"),
                r.Elements("member").Where(m => (string?)m.Attribute("type") == "way")
                    .Select(m => (Long(m, "ref"), (string?)m.Attribute("role") ?? "")).ToList(),
                Tags(r)))
            .ToList();

        // Areas: closed ways, plus the outer rings of multipolygon relations.
        var areas = new List<(Dictionary<string, string> Tags, List<long> Ring)>();
        foreach (var w in ways.Values.Where(w => w.Closed)) areas.Add((w.Tags, w.Nodes));
        foreach (var r in relations.Where(r => r.Tags.GetValueOrDefault("type") == "multipolygon"))
        {
            var outer = r.Members.Where(m => m.Role is "outer" or "").Select(m => ways.GetValueOrDefault(m.Ref)).OfType<Way>().Select(w => w.Nodes);
            foreach (var ring in AssembleRings(outer)) areas.Add((r.Tags, ring));
        }

        var holeWays = ways.Values.Where(w => w.Tag("golf") == "hole" && w.Nodes.Count >= 2).ToList();
        if (holeWays.Count == 0) throw new InvalidDataException("No golf=hole lines in the OSM data.");

        // The course outline (the named one, else the largest) and the projection origin at its centre.
        var outlines = areas.Where(a => a.Tags.GetValueOrDefault("leisure") == "golf_course").ToList();
        if (name != null && outlines.Any(a => Named(a.Tags, name)))
            outlines = outlines.Where(a => Named(a.Tags, name)).ToList();
        var originNodes = (outlines.Count > 0 ? outlines.SelectMany(a => a.Ring) : holeWays.SelectMany(w => w.Nodes)).Select(id => nodes[id]).ToList();
        double lat0 = (originNodes.Min(n => n.Lat) + originNodes.Max(n => n.Lat)) / 2;
        double lon0 = (originNodes.Min(n => n.Lon) + originNodes.Max(n => n.Lon)) / 2;
        double k = Math.PI / 180 * EarthRadius, kLon = k * Math.Cos(lat0 * Math.PI / 180);
        P2 Project(long id) => new((nodes[id].Lat - lat0) * k, (nodes[id].Lon - lon0) * kLon);
        Polygon Poly(List<long> ring) => new(ring.Take(ring.Count - 1).Select(Project).ToList());

        var notes = new List<string>();
        var lines = holeWays.Select(w => new HoleLine(
            w.Id, Int(w.Tag("ref")), Int(w.Tag("par")), w.Tag("name") ?? "", w.Nodes.Select(Project).ToList())).ToList();

        var extent = Bounds.Of(lines.SelectMany(l => l.Route));
        var boundary = outlines.Select(a => Poly(a.Ring)).Where(p => p.Points.Count >= 3).ToList();
        if (boundary.Count > 0) extent = boundary.Aggregate(extent, (e, p) => e.Union(p.Box));
        var near = extent.Expand(150);
        bool Near(Polygon p) => p.Box.Max.X >= near.Min.X && p.Box.Min.X <= near.Max.X && p.Box.Max.Z >= near.Min.Z && p.Box.Min.Z <= near.Max.Z;

        List<Polygon> AreasWhere(Func<Dictionary<string, string>, bool> pick) =>
            areas.Where(a => pick(a.Tags)).Select(a => Poly(a.Ring)).Where(p => p.Points.Count >= 3 && Near(p)).ToList();
        // Mown areas and bunkers have soft, curved edges: the mapped outlines' corners are rounded off.
        List<Polygon> Mown(string golf) => AreasWhere(t => t.GetValueOrDefault("golf") == golf).Select(p => p.Rounded()).ToList();
        var greens = Mown("green");
        var tees = Mown("tee");
        var bunkers = Mown("bunker");
        var mappedFairways = Mown("fairway");
        var water = AreasWhere(t => t.GetValueOrDefault("golf") is "water_hazard" or "lateral_water_hazard"
                                    || t.GetValueOrDefault("natural") == "water");
        var woods = AreasWhere(t => t.GetValueOrDefault("natural") == "wood" || t.GetValueOrDefault("landuse") == "forest");

        // Ditches and streams mapped as lines are water too, as wide as tagged.
        var waterLines = ways.Values
            .Where(w => !w.Closed && w.Nodes.Count >= 2 && w.Tag("waterway") is "ditch" or "drain" or "stream" or "river" or "canal")
            .Select(w => new Strip(w.Nodes.Select(Project).ToList(), WaterwayWidth(w)))
            .Where(s => s.Box.Max.X >= near.Min.X && s.Box.Min.X <= near.Max.X && s.Box.Max.Z >= near.Min.Z && s.Box.Min.Z <= near.Max.Z)
            .ToList();
        var allWater = water.Cast<Shape>().Concat(waterLines).ToList();

        // Cart paths: lines as wide as tagged (2.5 m if not), or mapped as areas (area=yes). Bridges carry them over water.
        bool NearBox(Bounds b) => b.Max.X >= near.Min.X && b.Min.X <= near.Max.X && b.Max.Z >= near.Min.Z && b.Min.Z <= near.Max.Z;
        var pathLines = ways.Values
            .Where(w => w.Nodes.Count >= 2 && IsCartPath(w.Tags) && !(w.Closed && w.Tag("area") == "yes"))
            .Select(w => (Way: w, Shape: (Shape)new Strip(w.Nodes.Select(Project).ToList(), TaggedWidth(w) ?? CartPathWidth)))
            .Where(x => NearBox(x.Shape.Box))
            .ToList();
        var cartPaths = pathLines.Select(x => x.Shape)
            .Concat(AreasWhere(t => IsCartPath(t) && t.GetValueOrDefault("area") == "yes"))
            .ToList();
        var bridges = pathLines.Where(x => x.Way.Tag("bridge") is { } b && b != "no").Select(x => x.Shape).ToList();

        // Hole lines run tee to green; flip any drawn the other way.
        lines = lines.Select(l =>
        {
            double Nearest(List<Polygon> set, P2 p) => set.Count == 0 ? 0 : set.Min(s => s.DistanceTo(p));
            bool backwards = Nearest(greens, l.Route[0]) + Nearest(tees, l.Route[^1]) < Nearest(tees, l.Route[0]) + Nearest(greens, l.Route[^1]);
            if (backwards) notes.Add($"Hole line {l.WayId} was drawn green-to-tee; reversed.");
            return backwards ? l with { Route = Enumerable.Reverse(l.Route).ToList() } : l;
        }).ToList();

        var numbered = Renumber(lines, notes);

        if (boundary.Count == 0)
        {
            notes.Add("No golf course outline mapped; out of bounds is 60 m outside the holes.");
            var b = extent.Expand(60);
            boundary.Add(new Polygon(new[] { b.Min, new P2(b.Max.X, b.Min.Z), b.Max, new P2(b.Min.X, b.Max.Z) }));
            extent = b;
        }
        else
        {
            foreach (var (n, l) in numbered)
                if (!boundary.Any(p => p.Contains(l.Route[0])) || !boundary.Any(p => p.Contains(l.Route[^1])))
                    notes.Add($"Hole {n}'s tee or green is outside the mapped course outline (it will play as out of bounds).");
        }

        // Fairways: mapped ones, plus generated strips for holes that have none along their line.
        var fairways = new List<Shape>(mappedFairways);
        var generated = new List<int>();
        foreach (var (n, l) in numbered)
        {
            double len = Length(l.Route);
            bool mapped = new[] { 0.4, 0.6, 0.8 }.Any(f => mappedFairways.Any(fw => fw.Contains(PointAt(l.Route, len * f))));
            if (mapped) continue;
            var green = greens.Where(g => g.DistanceTo(l.Route[^1]) < 25).OrderBy(g => g.DistanceTo(l.Route[^1])).FirstOrDefault();
            double reach = len - 10;
            if (green != null)
                for (double s = 0; s < len; s += 1) if (green.Contains(PointAt(l.Route, s))) { reach = s; break; }
            int par = l.Par ?? GuessPar(len);
            var strip = par <= 3
                ? (reach > 60 ? new Strip(SubPath(l.Route, reach - 30, reach - 4), ApronWidth) : null)
                : new Strip(SubPath(l.Route, Math.Min(FairwayStart, reach * 0.3), reach - 6), FairwayWidth);
            if (strip == null) continue;
            fairways.Add(strip);
            generated.Add(n);
        }
        if (generated.Count > 0)
            notes.Add($"Fairways aren't mapped for {(generated.Count == numbered.Count ? "any hole" : "holes " + string.Join(", ", generated))}; " +
                      $"generated {FairwayWidth * Units.MetresToYards:0}-yd fairways along the hole lines.");

        var holes = numbered.Select(x =>
        {
            var (n, l) = x;
            P2 tee = l.Route[0], pin = l.Route[^1];
            double len = Length(l.Route);
            int par = l.Par ?? GuessPar(len);
            if (l.Par == null) notes.Add($"Hole {n} has no par tagged; using {par} from its length.");

            if (!greens.Any(g => g.Contains(pin)))
            {
                var g = greens.Where(g => g.DistanceTo(pin) < 25).OrderBy(g => g.DistanceTo(pin)).FirstOrDefault();
                if (g != null) { pin = g.Centroid; notes.Add($"Hole {n}'s line ends off the green; pin moved to the green's centre."); }
            }

            // Aim off the tee: the dog-leg point if it's in driving range, otherwise ~250 yd down the line.
            P2 aim;
            if (par <= 3 || len < 250) aim = pin;
            else if (l.Route.Count > 2 && tee.DistanceTo(l.Route[1]) is >= 150 and <= 280) aim = l.Route[1];
            else aim = PointAt(l.Route, Math.Min(230, len - 100));

            var route = l.Route.ToList();
            route[0] = tee; route[^1] = pin;
            var frame = Bounds.Of(route.Concat(greens.Where(g => g.Contains(pin)).SelectMany(g => g.Points))).Expand(35);
            return new Hole(n, l.Name, par, tee, pin, aim, fairways, greens, bunkers, allWater, frame)
            {
                Tees = tees, CartPaths = cartPaths, Bridges = bridges, Boundary = boundary, Route = route,
            };
        }).ToList();

        // Ground heights, in the same frame (and relative to the height at the course origin).
        Terrain? terrain = null;
        if (elevation != null)
        {
            bool covered = new[] { extent.Min, extent.Max }.All(p => elevation.Covers(lat0 + p.X / k, lon0 + p.Z / kLon));
            if (covered) terrain = new Terrain(elevation, lat0, lon0, k, kLon, extent.Expand(TerrainMargin), greens, bunkers, shapeWater ? allWater : null);
            else notes.Add("The elevation data doesn't cover the whole course; playing it flat.");
        }

        // Surveyed trees, in the same frame; those far outside the course can't be seen from it.
        List<CourseTree>? trees = null;
        if (survey != null)
        {
            var keep = extent.Expand(TreeMargin);
            trees = survey.Select(t => new CourseTree(new P2((t.Lat - lat0) * k, (t.Lon - lon0) * kLon), t.Height, t.Crown, t.Kind))
                .Where(t => keep.Contains(t.At))
                .ToList();
            if (trees.Count == 0) { notes.Add("The tree survey doesn't cover the course; planting trees instead."); trees = null; }
        }

        string courseName = name ?? outlines.Select(a => a.Tags.GetValueOrDefault("name")).FirstOrDefault(n => n != null) ?? "OpenStreetMap course";
        return new Course(courseName, holes)
        {
            SharedLayout = true,
            Extent = extent,
            Woods = woods,
            Credit = Credit,
            Notes = notes,
            Terrain = terrain,
            Trees = trees,
        };
    }

    /// <summary>
    /// Keeps unique hole numbers; holes with a duplicate or missing number fill the gaps in routing order (the hole
    /// whose tee is nearest the previous hole's green comes next).
    /// </summary>
    private static List<(int Number, HoleLine Line)> Renumber(List<HoleLine> lines, List<string> notes)
    {
        var result = new Dictionary<int, HoleLine>();
        var loose = new List<HoleLine>();
        foreach (var g in lines.GroupBy(l => l.Ref))
        {
            if (g.Key is int n && n >= 1 && n <= lines.Count && g.Count() == 1) result[n] = g.First();
            else loose.AddRange(g);
        }
        for (int n = 1; n <= lines.Count && loose.Count > 0; n++)
        {
            if (result.ContainsKey(n)) continue;
            var prev = result.GetValueOrDefault(n - 1);
            var pick = prev == null ? loose[0] : loose.OrderBy(l => l.Route[0].DistanceTo(prev.Route[^1])).First();
            loose.Remove(pick);
            result[n] = pick;
            if (pick.Ref == null) notes.Add($"Hole line {pick.WayId} has no number; playing it as hole {n}.");
            else if (pick.Ref != n) notes.Add($"Hole line {pick.WayId} is tagged as hole {pick.Ref} (a duplicate); playing it as hole {n}, which its tee's position fits.");
        }
        return result.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Joins way node lists end to end into closed rings; pieces that don't close are dropped.</summary>
    private static List<List<long>> AssembleRings(IEnumerable<List<long>> parts)
    {
        var open = parts.Where(p => p.Count >= 2).Select(p => p.ToList()).ToList();
        var rings = new List<List<long>>();
        while (open.Count > 0)
        {
            var ring = open[0];
            open.RemoveAt(0);
            while (ring[0] != ring[^1])
            {
                int i = open.FindIndex(p => p[0] == ring[^1] || p[^1] == ring[^1]);
                if (i < 0) break;
                var next = open[i];
                open.RemoveAt(i);
                if (next[0] != ring[^1]) next.Reverse();
                ring.AddRange(next.Skip(1));
            }
            if (ring.Count >= 4 && ring[0] == ring[^1]) rings.Add(ring);
        }
        return rings;
    }

    public static double Length(IReadOnlyList<P2> route)
    {
        double len = 0;
        for (int i = 1; i < route.Count; i++) len += route[i - 1].DistanceTo(route[i]);
        return len;
    }

    /// <summary>The point <paramref name="s"/> metres along the route (clamped to its ends).</summary>
    public static P2 PointAt(IReadOnlyList<P2> route, double s)
    {
        for (int i = 1; i < route.Count; i++)
        {
            double seg = route[i - 1].DistanceTo(route[i]);
            if (s <= seg) return route[i - 1] + (route[i] - route[i - 1]) * (seg == 0 ? 0 : Math.Max(s, 0) / seg);
            s -= seg;
        }
        return route[^1];
    }

    /// <summary>The part of the route between <paramref name="from"/> and <paramref name="to"/> metres along it.</summary>
    public static List<P2> SubPath(IReadOnlyList<P2> route, double from, double to)
    {
        var pts = new List<P2> { PointAt(route, from) };
        double s = 0;
        for (int i = 1; i < route.Count - 1; i++)
        {
            s += route[i - 1].DistanceTo(route[i]);
            if (s > from && s < to) pts.Add(route[i]);
        }
        pts.Add(PointAt(route, to));
        return pts;
    }

    /// <summary>The tagged width (metres), or a typical one for the kind of waterway.</summary>
    private static double WaterwayWidth(Way w) =>
        TaggedWidth(w) ?? w.Tag("waterway") switch { "stream" => 3, "river" or "canal" => 10, _ => 1.5 };

    private static double? TaggedWidth(Way w) =>
        double.TryParse(w.Tag("width")?.Replace(" m", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    /// <summary>golf=cartpath, or a path or track tagged for golf carts.</summary>
    private static bool IsCartPath(Dictionary<string, string> t) =>
        t.GetValueOrDefault("golf") == "cartpath"
        || (t.GetValueOrDefault("golf_cart") is "designated" or "yes" && t.GetValueOrDefault("highway") is "path" or "track" or "service" or "footway");

    private static int GuessPar(double metres) => metres < 230 ? 3 : metres < 430 ? 4 : 5;

    private static bool Named(Dictionary<string, string> tags, string name) =>
        tags.GetValueOrDefault("name")?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;

    private static Dictionary<string, string> Tags(XElement e) =>
        e.Elements("tag").GroupBy(t => (string?)t.Attribute("k") ?? "").ToDictionary(g => g.Key, g => (string?)g.First().Attribute("v") ?? "");

    private static long Long(XElement e, string attr) => long.Parse((string?)e.Attribute(attr) ?? "0", CultureInfo.InvariantCulture);
    private static double Dbl(XElement e, string attr) => double.Parse((string?)e.Attribute(attr) ?? "0", CultureInfo.InvariantCulture);
    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
