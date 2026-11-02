using Godot;

namespace SquareGspro.Range3D;

/// <summary>
/// A flagstick: a thin pole with a flag, standing at its origin. The pole is drawn at its real thickness up close and
/// widened with distance so it never thins to nothing; the flag grows a little with distance so far pins stay
/// readable. <see cref="LayDown"/> puts it on the ground beside the hole (pulled for putting).
/// </summary>
public partial class Flagstick : Node3D
{
    private const float PoleRadius = 0.008f;   // ~5/8 in across
    private const float Height = 2.13f;        // 7 ft

    private readonly Color _color;
    private readonly float _scale;

    /// <summary>Grow the flag with distance (off when the caller already sized it for distance, like range targets).</summary>
    public bool GrowFlag { get; init; } = true;
    private MeshInstance3D _pole = null!;
    private Node3D _flag = null!;
    private Vector3 _standAt;

    public Flagstick(Color color, float scale = 1)
    {
        _color = color;
        _scale = scale;
    }

    public Flagstick() : this(Colors.Red) { }

    public override void _Ready()
    {
        float h = Height * _scale;
        _pole = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = PoleRadius, BottomRadius = PoleRadius, Height = h, RadialSegments = 8, Rings = 1 },
            Position = new Vector3(0, h / 2, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.95f, 0.95f) },
        };
        AddChild(_pole);
        // The flag hangs from the top of the pole; its node is scaled with distance around that point.
        _flag = new Node3D { Position = new Vector3(0, h, 0) };
        _flag.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.75f * _scale, 0.5f * _scale, 0.01f) },
            Position = new Vector3(0.375f * _scale, -0.25f * _scale, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = _color, EmissionEnabled = true, Emission = _color * 0.3f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        });
        AddChild(_flag);
        _standAt = Position;
    }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        float d = cam.GlobalPosition.DistanceTo(GlobalPosition);
        // Keep the pole ~2-3 px wide however far away it is.
        float widen = Mathf.Max(1f, d * 0.0012f / PoleRadius);
        _pole.Scale = new Vector3(widen, 1, widen);
        if (GrowFlag) _flag.Scale = Vector3.One * Mathf.Max(1f, d / 90f);
    }

    /// <summary>Back in the hole.</summary>
    public void Stand()
    {
        Basis = Basis.Identity;
        Position = _standAt;
    }

    /// <summary>Lying on the ground, base at <paramref name="baseAt"/>, pointing along <paramref name="along"/> (may follow a slope).</summary>
    public void LayDown(Vector3 baseAt, Vector3 along)
    {
        along = along.Normalized();
        var side = along.Cross(Vector3.Up).Normalized();
        Basis = new Basis(side, along, side.Cross(along).Normalized());
        Position = baseAt + new Vector3(0, PoleRadius, 0);
    }
}
