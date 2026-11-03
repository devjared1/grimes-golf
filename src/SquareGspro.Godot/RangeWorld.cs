using Godot;

namespace SquareGspro.Range3D;

/// <summary>
/// The driving range: grass, fairway, tee, target greens with flags and yardage signs, and tree lines.
/// Godot axes: X right, Y up, -Z downrange (metres). The tee is at the origin. Sky and sun come from <see cref="Scenery"/>.
/// </summary>
public partial class RangeWorld : Node3D
{
    public const float Yard = 0.9144f;
    public const float RangeLength = 330f; // metres of mown range
    public static readonly int[] TargetYards = { 50, 100, 150, 200, 250, 300 };

    private MeshInstance3D _aimLine = null!, _aimRing = null!;

    public override void _Ready()
    {
        AddGround();
        AddTee();
        AddTargets();
        AddTrees();
        var aimMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1, 0.9f, 0.3f, 0.7f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = false,
        };
        _aimLine = new MeshInstance3D { MaterialOverride = aimMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _aimRing = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 2.4f, OuterRadius = 2.9f, Rings = 48, RingSegments = 6 },
            MaterialOverride = aimMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_aimLine);
        AddChild(_aimRing);
    }

    /// <summary>Where target flag <paramref name="index"/> (into <see cref="TargetYards"/>) stands, on the ground.</summary>
    public static Vector3 TargetPosition(int index) =>
        new(index % 2 == 0 ? 13 : -14, 0, -TargetYards[index] * Yard);   // alternate sides so the yardage signs don't stack up from the tee

    /// <summary>
    /// The aim line from the tee: along <paramref name="headingRad"/> (0 = straight downrange, positive = right) for
    /// <paramref name="length"/> metres, with a ring where it ends. Hidden while a shot is in the air.
    /// </summary>
    public void SetAim(double headingRad, double length, bool visible)
    {
        _aimLine.Visible = _aimRing.Visible = visible && length > 1;
        var dir = new Vector3((float)Math.Sin(headingRad), 0, (float)-Math.Cos(headingRad));
        float len = (float)Math.Max(length - 3.5, 0.5);   // stops at the ring
        _aimLine.Mesh = new BoxMesh { Size = new Vector3(0.22f, 0.01f, len) };
        _aimLine.Position = dir * (1.5f + len / 2) + new Vector3(0, 0.08f, 0);
        _aimLine.Rotation = new Vector3(0, (float)-headingRad, 0);
        _aimRing.Position = dir * (float)length + new Vector3(0, 0.08f, 0);
    }

    private void AddGround()
    {
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(2400, 2400) },
            MaterialOverride = Scenery.Grass(Scenery.Rough, Scenery.RoughAlt, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(80, RangeLength + 20) },
            Position = new Vector3(0, 0.02f, -(RangeLength + 20) / 2 + 10),
            MaterialOverride = Scenery.Grass(Scenery.Fairway, Scenery.FairwayAlt, 6, Scenery.Turf.Fairway),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // Yardage lines every 50 yd.
        var lineMat = new StandardMaterial3D { AlbedoColor = new Color(1, 1, 1, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        foreach (int yd in TargetYards)
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(80, 0.35f) },
                Position = new Vector3(0, 0.04f, -yd * Yard),
                MaterialOverride = lineMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
    }

    private void AddTee()
    {
        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(14, 0.12f, 9) },
            Position = new Vector3(0, 0.03f, 1.5f),
            MaterialOverride = Scenery.Grass(new Color(0.40f, 0.66f, 0.29f), new Color(0.44f, 0.70f, 0.31f), 1.0f, Scenery.Turf.Fairway),
        });
        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.5f, 0.03f, 1.5f) },
            Position = new Vector3(0, 0.1f, 0.4f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.30f, 0.14f), Roughness = 1 },
        });
        // Tee markers either side of the mat.
        var markerMat = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.15f, 0.12f) };
        foreach (float x in new[] { -3f, 3f })
            AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.07f, Height = 0.14f }, Position = new Vector3(x, 0.15f, 0), MaterialOverride = markerMat });
    }

    private void AddTargets()
    {
        var greenMat = Scenery.Grass(Scenery.Green, Scenery.GreenAlt, 0, Scenery.Turf.Green);
        var red = new Color(0.86f, 0.12f, 0.12f);
        var yellow = new Color(0.98f, 0.80f, 0.10f);

        for (int i = 0; i < TargetYards.Length; i++)
        {
            int yd = TargetYards[i];
            var at = TargetPosition(i);
            float x = at.X, z = at.Z;
            float radius = 7 + yd * 0.035f;
            AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.06f, RadialSegments = 48 },
                Position = new Vector3(x, 0.03f, z),
                MaterialOverride = greenMat,
            });

            // Flags are drawn oversized with distance so far targets stay readable.
            float s = 1.2f + yd / 90f;
            Scenery.AddFlag(this, new Vector3(x, 0, z), yd % 100 == 0 ? red : yellow, s, growFlag: false);
            AddChild(new Label3D
            {
                Text = yd.ToString(),
                FontSize = 96,
                OutlineSize = 18,
                PixelSize = 0.008f * s,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(x, 3.6f * s, z),
                Modulate = Colors.White,
                OutlineModulate = new Color(0, 0, 0, 0.7f),
            });
        }
    }

    private void AddTrees()
    {
        var rng = new RandomNumberGenerator { Seed = 7 };
        var spots = new List<Vector3>();
        for (float z = -20; z > -RangeLength - 30; z -= 9)
            foreach (int side in new[] { -1, 1 })
                for (int row = 0; row < 2; row++)
                    spots.Add(new Vector3(side * (48 + row * 14 + rng.RandfRange(-4, 4)), 0, z + rng.RandfRange(-4, 4)));
        for (float x = -110; x <= 110; x += 10)                         // backstop row at the far end
            spots.Add(new Vector3(x + rng.RandfRange(-3, 3), 0, -RangeLength - 45 + rng.RandfRange(-6, 6)));
        Scenery.AddTrees(this, spots, 7);
    }
}
