using Godot;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// Renders a hole from its Core definition (so what you see is exactly what the rules use): rough, fairways, greens
/// with fringe, bunkers (with rakes), water, tees, pins, yardage markers, out-of-bounds stakes, trees, and grass
/// blades around the ball. For a mapped course
/// (<see cref="Course.SharedLayout"/>) the whole course is drawn once, with a flag on every green, and
/// <see cref="ShowHole"/> switches holes. Also draws the aim line from the ball to the target.
/// </summary>
public partial class CourseWorld : Node3D
{
    private readonly Course _courseDef;
    private Hole _hole;
    private AimGuide _aim = null!;
    private MeshInstance3D _mapPin = null!;
    private readonly Dictionary<Hole, (MeshInstance3D Disc, Node3D Cup, Flagstick Flag)> _pins = new();
    private GrassPatch _grass = null!;
    private GrassField? _field;
    private P2? _grassAt;

    public CourseWorld(Course course, Hole hole)
    {
        _courseDef = course;
        _hole = hole;
    }

    // Godot needs a parameterless constructor for scripts it may instantiate itself.
    public CourseWorld() : this(CourseLibrary.All[0].Load()) { }

    private CourseWorld(Course course) : this(course, course.Holes[0]) { }

    public Course Course => _courseDef;
    private bool Shared => _courseDef.SharedLayout;
    private Terrain? Ground => _courseDef.Terrain;

    /// <summary>Ground height at a point (0 on a flat course).</summary>
    public float GroundAt(P2 p) => (float)(Ground?.HeightAt(p) ?? 0);

    /// <summary>Height of the putting surface at a point: the ground, or the raised green layer on a flat course.</summary>
    private float GreenAt(P2 p) => Ground != null ? GroundAt(p) : Scenery.GreenTop;

    private static P2 FromGodot(Vector3 v) => new(-v.Z, v.X);

    public override void _Ready()
    {
        var h = _hole;
        if (Ground != null)
        {
            _field = new GrassField(Ground, TerrainGround.Build(this, _courseDef, Ground));
            AddChild(_field);
        }
        else BuildFlatGround(h);

        foreach (var hole in Shared ? _courseDef.Holes : new[] { h })
        {
            if (hole.Tees.Count == 0 && Ground == null) AddTeeBox(hole);
            AddTeeMarkers(hole);
            _pins[hole] = AddPin(hole.Pin);
        }
        _mapPin = new MeshInstance3D
        {
            // A big dot on the minimap only, at the current hole's pin.
            Mesh = new SphereMesh { Radius = 3, Height = 6 },
            Layers = ShotPlayer.MapLayer,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.1f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        };
        AddChild(_mapPin);
        ShowHole(h);
        AddStakes();
        AddTrees();
        foreach (var hole in Shared ? _courseDef.Holes : new[] { h }) AddYardageMarkers(hole);
        AddRakes(h);
        _grass = new GrassPatch();
        AddChild(_grass);

        _aim = new AimGuide(lineColor: new Color(1, 1, 1, 0.6f), ringColor: new Color(1, 1, 1, 0.75f));
        AddChild(_aim);
    }

    /// <summary>Flat ground: deep rough everywhere, with the playing areas as flat shapes layered a few cm apart.</summary>
    private void BuildFlatGround(Hole h)
    {
        var area = Shared ? _courseDef.Extent : h.Bounds;
        float size = (float)Math.Max(area.Max.X - area.Min.X, area.Max.Z - area.Min.Z) + 3000;
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(size, size) },
            Position = Scenery.ToGodot(area.Center),
            MaterialOverride = Scenery.Grass(Scenery.DeepRough, Scenery.DeepRoughAlt, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // Layers are a few cm apart so they never z-fight: first cut < fairway < tee < cart path < fringe < green < bunker < water.
        var firstCut = Scenery.Grass(Scenery.Rough, Scenery.RoughAlt, 0);
        var tees = (Shared ? _courseDef.Holes : new[] { h }).Select(Scenery.TeeCut);
        foreach (var c in Scenery.FirstCut(h).Concat(tees)) Scenery.AddShape(this, c, 0.015f, firstCut);
        var fairway = Scenery.Grass(Scenery.Fairway, Scenery.FairwayAlt, 6, Scenery.Turf.Fairway);
        foreach (var f in h.Fairways) Scenery.AddShape(this, f, 0.03f, fairway);
        var tee = Scenery.Grass(new Color(0.40f, 0.66f, 0.29f), new Color(0.44f, 0.70f, 0.31f), 1.0f, Scenery.Turf.Fairway);
        foreach (var t in h.Tees) Scenery.AddShape(this, t, 0.045f, tee);
        var concrete = Scenery.CartPath();
        foreach (var c in h.CartPaths) Scenery.AddShape(this, c, 0.052f, concrete);
        foreach (var g in h.Greens) Scenery.AddShape(this, g, 0.06f, fairway, grow: 1.8f);
        var green = Scenery.Grass(Scenery.Green, Scenery.GreenAlt, 1.5f, Scenery.Turf.Green);
        foreach (var g in h.Greens) Scenery.AddShape(this, g, 0.09f, green);
        var sand = Scenery.Sand();
        foreach (var b in h.Bunkers) Scenery.AddShape(this, b, 0.12f, sand);
        var water = Scenery.Water();
        foreach (var w in h.Water) Scenery.AddShape(this, w, 0.15f, water);
        foreach (var b in h.Bridges) Scenery.AddShape(this, b, 0.165f, concrete);
    }

    /// <summary>Grows grass blades around the ball where it rests (nothing happens if it hasn't moved).</summary>
    public void ShowGrassAround(P2 ball)
    {
        if (_grassAt is { } at && at.DistanceTo(ball) < 0.01) return;
        _grassAt = ball;
        // Only the features near the ball matter: a cut-down hole makes the thousands of lie look-ups cheap.
        var h = _hole;
        double reach = GrassPatch.Radius + Hole.FringeWidth + Hole.FirstCutWidth + 1;
        List<Shape> Near(IReadOnlyList<Shape> shapes) => shapes.Where(s => s.IsNear(ball, reach)).ToList();
        var local = h with { Fairways = Near(h.Fairways), Greens = Near(h.Greens), Bunkers = Near(h.Bunkers), Water = Near(h.Water), Tees = Near(h.Tees), CartPaths = Near(h.CartPaths), Bridges = Near(h.Bridges) };
        // Well inside the course outline, nothing near the ball is out of bounds: skip testing the (long) outline.
        if (h.Boundary != null && h.Boundary.Any(b => b.DistanceTo(ball) < -GrassPatch.Radius - 1))
            local = local with { Boundary = new[] { new Ellipse(ball, reach, reach) } };
        _grass.Grow(ball, local.LieAt, SurfaceAt);
        _field?.SetBallPatch(ball);
    }

    /// <summary>
    /// Top of the drawn surface at a point: the ground mesh with real heights, else the flat layer that lie is drawn
    /// at (see <see cref="BuildFlatGround"/>).
    /// </summary>
    private float SurfaceAt(P2 p, Lie lie) => Ground != null ? TerrainGround.MeshHeightAt(Ground, p) : lie switch
    {
        Lie.Rough => 0.015f,
        Lie.Fairway => 0.03f,
        Lie.Tee => _hole.Tees.Count > 0 ? 0.045f : 0.09f,
        Lie.CartPath => 0.052f,
        Lie.Fringe => 0.06f,
        _ => 0,
    };

    /// <summary>The aim line on the ground from the ball along the aim heading, with a ring at the target distance.</summary>
    public void SetAim(P2 ball, double heading, double length, bool visible, bool showRing = true, bool landingView = false)
    {
        _aim.Visible = visible;
        if (!visible) return;
        double ring = showRing ? 2.6 * Math.Clamp(length / 40, 0.15, 2.0) : 0;
        // Close up, a line held above the ground looks off to one side of the ball and the cup, so a putt's lies right on it.
        bool putt = !showRing && Ground != null;
        _aim.Set(ball, heading, length, ring, p => Scenery.ToGodot(p, putt ? TerrainGround.MeshHeightAt(Ground!, p) + 0.03f : GroundAt(p) + 0.2f), landingView,
                 putt: !showRing);
    }

    // ---- gimme circles -------------------------------------------------------------------------

    private MeshInstance3D? _gimme;
    private double _gimmeRadius, _secondRadius;

    /// <summary>
    /// Circles on the green round the hole being played: the gimme circle (<paramref name="radius"/> m) and, with
    /// auto-putting, its second circle (<paramref name="second"/> m; 0 = none). 0 for both hides them.
    /// </summary>
    public void SetGimmeCircles(double radius, double second)
    {
        _gimmeRadius = radius;
        _secondRadius = second;
        BuildGimmeCircles();
    }

    /// <summary>The gimme circle's colour while the ball is inside it.</summary>
    private static readonly Color GimmeIn = new(0.25f, 0.95f, 0.45f);

    /// <summary>The gimme circle's own material (tints its white vertex colours), and whether it shows the ball inside.</summary>
    private StandardMaterial3D? _gimmeMaterial;
    private bool _ballInGimme;

    /// <summary>
    /// Turns the gimme circle green while the ball is on the ground inside it, and back to white when it rolls out
    /// (called every frame with the ball being played; null = no ball).
    /// </summary>
    public void ShowBallInGimme(Vector3? ball)
    {
        bool inside = false;
        if (ball is { } b && _gimmeRadius > 0)
        {
            var p = FromGodot(b);
            float ground = Ground != null ? TerrainGround.MeshHeightAt(Ground, p) : GreenAt(p);
            // On (or in) the green, not flying over it: a ball in the cup is below the surface, so only the height above counts.
            inside = p.DistanceTo(_hole.Pin) <= _gimmeRadius && b.Y - ground < 0.25f;
        }
        if (inside == _ballInGimme) return;
        _ballInGimme = inside;
        if (_gimmeMaterial != null) _gimmeMaterial.AlbedoColor = inside ? GimmeIn : Colors.White;
    }

    private void BuildGimmeCircles()
    {
        _gimme?.QueueFree();
        _gimme = null;
        _gimmeMaterial = null;
        if (_gimmeRadius <= 0 && _secondRadius <= 0) return;
        var mesh = new ImmediateMesh();
        var pin = _hole.Pin;
        // Draped on the drawn green, over the heat map and grid lines (see GreenOverlay) but under its rolling dots.
        Vector3 On(P2 p) => Scenery.ToGodot(p, (Ground != null ? TerrainGround.MeshHeightAt(Ground, p) : GreenAt(p)) + 0.042f);
        StandardMaterial3D Material() => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            RenderPriority = 1,   // drawn after the heat map, which is transparent too
        };
        // One surface per circle, each with its own material, so the gimme circle can change colour on its own.
        var materials = new List<StandardMaterial3D>();
        foreach (var (radius, alpha, gimme) in new[] { (_gimmeRadius, 0.75f, true), (_secondRadius, 0.45f, false) })
        {
            if (radius <= 0) continue;
            var material = Material();
            if (gimme)
            {
                _gimmeMaterial = material;
                material.AlbedoColor = _ballInGimme ? GimmeIn : Colors.White;
            }
            materials.Add(material);
            const double band = 0.035;   // m
            int segments = (int)Math.Clamp(radius * 40, 64, 240);
            mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
            for (int i = 0; i <= segments; i++)
            {
                double a = Math.Tau * i / segments;
                var round = new P2(Math.Cos(a), Math.Sin(a));
                mesh.SurfaceSetColor(new Color(1, 1, 1, alpha));
                mesh.SurfaceAddVertex(On(pin + round * (radius + band / 2)));
                mesh.SurfaceSetColor(new Color(1, 1, 1, alpha));
                mesh.SurfaceAddVertex(On(pin + round * (radius - band / 2)));
            }
            mesh.SurfaceEnd();
        }
        _gimme = new MeshInstance3D
        {
            Mesh = mesh, Layers = ShotPlayer.TrailLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        for (int i = 0; i < materials.Count; i++) _gimme.SetSurfaceOverrideMaterial(i, materials[i]);
        AddChild(_gimme);
    }

    /// <summary>Moves the minimap pin marker to this hole (a mapped course is drawn once for all its holes).</summary>
    public void ShowHole(Hole hole)
    {
        if (_pins.TryGetValue(_hole, out var old))
        {
            old.Disc.Visible = true;
            old.Cup.Visible = false;
            old.Flag.Stand();
        }
        _hole = hole;
        _mapPin.Position = Scenery.ToGodot(hole.Pin, GreenAt(hole.Pin));
        // Only the hole being played is cut into the green; the others show a dark disc.
        var pin = _pins[hole];
        pin.Disc.Visible = false;
        pin.Cup.Visible = true;
        Scenery.SetCupHole(Scenery.ToGodot(hole.Pin, GreenAt(hole.Pin)));
        _cupOwner = this;
        BuildGreenOverlay();
        BuildGimmeCircles();
    }

    private GreenOverlay? _greenOverlay;
    private bool _showHeatMap, _showGrid;

    /// <summary>Shows or hides the heat map and the rolling-dot grid on the green of the hole being played.</summary>
    public void SetGreenOverlay(bool heatMap, bool grid)
    {
        _showHeatMap = heatMap;
        _showGrid = grid;
        if ((heatMap || grid) && _greenOverlay == null) BuildGreenOverlay();
        if (_greenOverlay == null) return;
        _greenOverlay.HeatMapVisible = heatMap;
        _greenOverlay.GridVisible = grid;
    }

    /// <summary>(Re)builds the overlay for the current hole's green, only once it's wanted.</summary>
    private void BuildGreenOverlay()
    {
        _greenOverlay?.QueueFree();
        _greenOverlay = null;
        if (!_showHeatMap && !_showGrid) return;
        var pin = _hole.Pin;
        // A mapped course's holes share one list of greens: take the one with this hole's pin on it.
        var green = _hole.Greens.FirstOrDefault(g => g.Contains(pin)) ?? _hole.Greens.OrderBy(g => g.DistanceTo(pin)).FirstOrDefault();
        if (green == null) return;
        _greenOverlay = new GreenOverlay(green, GreenAt, pin - _hole.Tee);
        AddChild(_greenOverlay);
        _greenOverlay.HeatMapVisible = _showHeatMap;
        _greenOverlay.GridVisible = _showGrid;
    }

    // The cup cut-out is global; only the world that set it last may clear it (an old world is freed after the new one starts).
    private static CourseWorld? _cupOwner;

    /// <summary>
    /// Flag in the hole, or pulled and laid on the green beside it, out of the way of a putt coming along
    /// <paramref name="puttDirection"/>.
    /// </summary>
    public void SetFlagIn(bool standing, Vector3 puttDirection)
    {
        var flag = _pins[_hole].Flag;
        if (standing) { flag.Stand(); return; }
        var along = new Vector3(puttDirection.X, 0, puttDirection.Z).Normalized();
        if (along.LengthSquared() < 0.5f) along = Vector3.Forward;
        var across = along.Cross(Vector3.Up);
        // Beside and beyond the hole, pointing away from the player, lying on the green.
        var at = Scenery.ToGodot(_hole.Pin) + across * 0.8f + along * 0.6f;
        var tip = at + (across + along * 1.5f).Normalized() * 2.5f;
        at.Y = GreenAt(FromGodot(at));
        tip.Y = GreenAt(FromGodot(tip));
        flag.LayDown(at, tip - at);
    }

    /// <summary>
    /// The flag on the hole being played streams the way the wind blows (course heading it blows toward; null: calm,
    /// it hangs the usual way). Flags on the other greens are left alone.
    /// </summary>
    public void SetFlagWind(double? blowsToward)
    {
        foreach (var (hole, pin) in _pins)
            pin.Flag.SetWind(hole == _hole && blowsToward is { } h ? new Vector3((float)Math.Sin(h), 0, (float)-Math.Cos(h)) : null);
    }

    public override void _ExitTree()
    {
        if (_cupOwner != this) return;
        _cupOwner = null;
        Scenery.SetCupHole(null);
    }

    private static float TeeYaw(Hole hole)
    {
        var dir = hole.TeeAim - hole.Tee;
        return Mathf.Atan2((float)dir.Z, (float)dir.X);
    }

    private void AddTeeBox(Hole hole)
    {
        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(10, 0.12f, 12) },
            Position = Scenery.ToGodot(hole.Tee, 0.03f) + new Vector3(0, 0, 2).Rotated(Vector3.Up, -TeeYaw(hole)),
            Rotation = new Vector3(0, -TeeYaw(hole), 0),
            MaterialOverride = Scenery.Grass(new Color(0.40f, 0.66f, 0.29f), new Color(0.44f, 0.70f, 0.31f), 1.0f, Scenery.Turf.Fairway),
        });
    }

    /// <summary>Two blue markers either side of the tee, square to the hole.</summary>
    private void AddTeeMarkers(Hole hole)
    {
        var markerMat = new StandardMaterial3D { AlbedoColor = new Color(0.20f, 0.35f, 0.85f) };
        var across = new Vector3(3, 0, 0).Rotated(Vector3.Up, -TeeYaw(hole));
        foreach (float s in new[] { -1f, 1f })
        {
            var at = Scenery.ToGodot(hole.Tee) + across * s;
            at.Y = GroundAt(FromGodot(at)) + (Ground != null ? 0.07f : 0.15f);
            AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.07f, Height = 0.14f }, Position = at, MaterialOverride = markerMat });
        }
    }

    /// <summary>
    /// A hole in the green: a flat dark disc (seen on other holes), an open cup with depth (white liner, dark inside;
    /// shown on the hole being played, where the green is cut away around it) and the flagstick, which stands on
    /// the bottom of the cup.
    /// </summary>
    private (MeshInstance3D Disc, Node3D Cup, Flagstick Flag) AddPin(P2 pin)
    {
        const float depth = (float)HoleRound.CupDepth, liner = 0.025f, r = Scenery.CupRadius;
        var top = Scenery.ToGodot(pin, GreenAt(pin));
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.07f, 0.06f), Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var disc = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = 0.004f, RadialSegments = 24 },
            Position = top + new Vector3(0, 0.003f, 0),
            MaterialOverride = dark,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(disc);

        var cup = new Node3D { Position = top, Visible = false };
        cup.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = depth, RadialSegments = 32, Rings = 1, CapTop = false },
            Position = new Vector3(0, -depth / 2, 0),
            MaterialOverride = dark,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        cup.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = r * 0.995f, BottomRadius = r * 0.995f, Height = liner, RadialSegments = 32, Rings = 1, CapTop = false, CapBottom = false },
            Position = new Vector3(0, -liner / 2 - 0.004f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.92f, 0.90f), CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        AddChild(cup);

        var flag = Scenery.AddFlag(this, top - new Vector3(0, depth, 0), new Color(0.86f, 0.12f, 0.12f), 1.2f);
        return (disc, cup, flag);
    }

    /// <summary>White stakes along the out-of-bounds line (the course outline, or the hole's bounds).</summary>
    private void AddStakes()
    {
        var b = _hole.Bounds;
        var rings = _hole.Boundary?.OfType<Polygon>().Select(p => p.Points).ToList()
                    ?? new() { new[] { b.Min, new P2(b.Max.X, b.Min.Z), b.Max, new P2(b.Min.X, b.Max.Z) } };
        var stakes = new List<Vector3>();
        foreach (var ring in rings)
        {
            // Walk the outline, one stake every 15 m.
            double carry = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                P2 a = ring[i], c = ring[(i + 1) % ring.Count];
                double len = a.DistanceTo(c);
                for (double s = carry; s < len; s += 15)
                {
                    var p = a + (c - a) * (s / len);
                    stakes.Add(Scenery.ToGodot(p, GroundAt(p) + 0.6f));
                }
                carry = len <= carry ? carry - len : 15 - (len - carry) % 15;
            }
        }
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.06f, Height = 1.2f, RadialSegments = 6 },
            InstanceCount = stakes.Count,
        };
        for (int i = 0; i < stakes.Count; i++) mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, stakes[i]));
        AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White } });
    }

    /// <summary>Yardages marked on the hole line, to the middle of the green, for the markers in the fairway.</summary>
    private static readonly (double Yards, Color Color)[] Yardages =
    {
        (100, new Color(0.85f, 0.12f, 0.10f)), (150, new Color(0.95f, 0.95f, 0.93f)), (200, new Color(0.15f, 0.30f, 0.80f)),
    };

    /// <summary>
    /// Sprinkler-plate yardage markers: a coloured disc with a white rim set into the fairway on the hole line at 100
    /// (red), 150 (white) and 200 (blue) yards from the middle of the green. Skipped where that spot isn't fairway.
    /// </summary>
    private void AddYardageMarkers(Hole hole)
    {
        var line = hole.Route ?? new[] { hole.Tee, hole.TeeAim, hole.Pin };
        var green = hole.Greens.FirstOrDefault(g => g.Contains(hole.Pin));
        P2 middle = green is Polygon poly ? poly.Centroid : green is Ellipse e ? e.Center : hole.Pin;
        double length = OsmCourse.Length(line);
        var rim = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.92f, 0.90f), Roughness = 0.5f };
        foreach (var (yards, color) in Yardages)
        {
            // Walk back from the green along the line to the spot that far from its middle.
            double want = yards * Units.YardsToMetres;
            P2? spot = null;
            for (double s = length; s > 0; s -= 0.5)
            {
                var p = OsmCourse.PointAt(line, s);
                if (p.DistanceTo(middle) >= want) { spot = p; break; }
            }
            if (spot is not { } at || hole.LieAt(at) != Lie.Fairway) continue;
            var top = Scenery.ToGodot(at, SurfaceAt(at, Lie.Fairway));
            AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 0.03f, RadialSegments = 24 },
                Position = top, MaterialOverride = rim, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.23f, BottomRadius = 0.23f, Height = 0.032f, RadialSegments = 24 },
                Position = top, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.5f },
            });
        }
    }

    /// <summary>
    /// A rake left on the grass beside each sizeable bunker, on the side away from the green, lying along the edge
    /// with its head toward the sand.
    /// </summary>
    private void AddRakes(Hole h)
    {
        var handle = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.74f, 0.42f), Roughness = 0.6f };
        var head = new StandardMaterial3D { AlbedoColor = new Color(0.93f, 0.48f, 0.12f), Roughness = 0.7f };
        foreach (var bunker in h.Bunkers)
        {
            if (bunker is not (Polygon or Ellipse)) continue;
            var outline = bunker is Polygon p ? p.Points : ((Ellipse)bunker).Outline(32);
            var box = bunker.Box;
            if ((box.Max.X - box.Min.X) * (box.Max.Z - box.Min.Z) < 40) continue;
            var centre = new P2(outline.Average(q => q.X), outline.Average(q => q.Z));
            var green = h.Greens.OrderBy(g => g.DistanceTo(centre)).FirstOrDefault();
            // The edge point farthest from the green (or the first, if there's no green about).
            var edge = green == null ? outline[0] : outline.MaxBy(q => green.DistanceTo(q));
            var outward = edge - centre;
            outward *= 1 / Math.Max(outward.Length, 1e-6);
            var along = new P2(-outward.Z, outward.X);
            // Handle end on the grass, head end angled in toward the sand.
            P2 butt = edge + outward * 0.9 - along * 0.8, tip = edge + outward * 0.35 + along * 0.8;
            Vector3 On(P2 q) => Scenery.ToGodot(q, SurfaceAt(q, h.LieAt(q)) + 0.015f);
            Vector3 a = On(butt), b = On(tip);
            float len = a.DistanceTo(b);
            var rake = new Node3D { Position = a, Basis = Basis.LookingAt(b - a, Vector3.Up) };   // -Z along the handle
            rake.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.013f, BottomRadius = 0.013f, Height = len, RadialSegments = 8 },
                Position = new Vector3(0, 0, -len / 2), RotationDegrees = new Vector3(90, 0, 0), MaterialOverride = handle,
            });
            rake.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.55f, 0.035f, 0.05f) },
                Position = new Vector3(0, 0.005f, -len), MaterialOverride = head,
            });
            AddChild(rake);
        }
    }

    /// <summary>
    /// The course's real trees where they were surveyed (each as tall and as wide as measured). Otherwise dense trees
    /// beyond out of bounds and in mapped woods, scattered ones in the rough well away from the playing areas. Either
    /// way the view from every tee is kept clear.
    /// </summary>
    private void AddTrees()
    {
        var h = _hole;
        var holes = Shared ? _courseDef.Holes : new[] { h };
        // The same trees, and shapes, that a ball can hit (see Course.StandingTrees and TreeShape).
        if (_courseDef.StandingTrees is { } standing)
        {
            var measured = standing.Select(t => (Scenery.ToGodot(t.At, GroundAt(t.At)), t)).ToList();
            TreeFactory.AddMeasured(this, measured, 7);
            return;
        }
        var area = Shared ? _courseDef.Extent : h.Bounds;
        var rng = new Random(Shared ? 7 : h.Number * 31);
        // Keep the line of play open on mapped holes (par 3s have no fairway between tee and green).
        var corridors = holes.Where(t => t.Route != null).Select(t => new Strip(t.Route!, 64)).ToList();
        var spots = new List<Vector3>();
        const double margin = 45, step = 8;
        for (double x = area.Min.X - margin; x <= area.Max.X + margin; x += step)
            for (double z = area.Min.Z - margin; z <= area.Max.Z + margin; z += step)
            {
                var p = new P2(x + (rng.NextDouble() - 0.5) * step, z + (rng.NextDouble() - 0.5) * step);
                double roll = rng.NextDouble();
                if (holes.Any(t => BehindTee(t, p)) || corridors.Any(c => c.IsNear(p, 0))) continue;
                double chance = !h.InBounds(p) ? 0.8
                    : h.IsNearPlayArea(p, 26) ? 0
                    : _courseDef.Woods.Any(w => w.Contains(p)) ? 0.8 : 0.45;
                if (roll < chance) spots.Add(Scenery.ToGodot(p, GroundAt(p)));
            }
        Scenery.AddTrees(this, spots, Shared ? 7UL : (ulong)h.Number);
    }

    /// <summary>Near the tee or just behind it, where the camera sits.</summary>
    private static bool BehindTee(Hole hole, P2 p) => Course.ClearedAroundTee(hole, p);
}
