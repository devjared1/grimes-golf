using Godot;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>
/// The aim on the ground: a thin solid line from the ball, draped over the ground, and a ring where the shot lands.
/// The ring is draped over the ground too, and draws over whatever is in front of it, so neither the ground nor the
/// grass can cover it. Both are for the main view only (the overview map draws its own).
/// </summary>
public partial class AimGuide : Node3D
{
    /// <summary>Where the line starts, this far (m) past the ball.</summary>
    private const double LineStart = 1.0;
    /// <summary>The line's half width: this much per metre from the camera keeps it about the same width on screen, within these limits (m).</summary>
    private const double WidthPerMetre = 0.0011, MinHalfWidth = 0.016, MaxHalfWidth = 0.3;
    /// <summary>Behind the ball, the camera sits about this far (m) back.</summary>
    private const double CameraBack = 9;
    /// <summary>The ring's band, as a share of its radius.</summary>
    private const double RingBand = 0.15;
    private const int RingSegments = 64;

    private readonly MeshInstance3D _line, _ring;

    public AimGuide(Color lineColor, Color ringColor)
    {
        _line = Part(new StandardMaterial3D { AlbedoColor = lineColor });
        _ring = Part(new StandardMaterial3D { AlbedoColor = ringColor, NoDepthTest = true, RenderPriority = 10 });
    }

    private MeshInstance3D Part(StandardMaterial3D material)
    {
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var part = new MeshInstance3D
        {
            Mesh = new ImmediateMesh(), MaterialOverride = material,
            Layers = ShotPlayer.TrailLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(part);
        return part;
    }

    /// <summary>
    /// Draws the line from <paramref name="ball"/> along <paramref name="heading"/> for <paramref name="length"/> metres,
    /// ending at a ring of <paramref name="ringRadius"/> metres (0: no ring). <paramref name="on"/> gives the point to
    /// draw at for a spot on the ground. <paramref name="landingView"/>: the camera is up near the landing spot
    /// (<see cref="CameraRig.SetLandingView"/>), not behind the ball, which sets how wide the line needs to be.
    /// </summary>
    public void Set(P2 ball, double heading, double length, double ringRadius, Func<P2, Vector3> on, bool landingView = false)
    {
        var dir = new P2(Math.Cos(heading), Math.Sin(heading));
        var (cameraAt, cameraUp) = landingView ? (length - CameraRig.LandingBack, (double)CameraRig.LandingUp) : (-CameraBack, 0.0);
        BuildLine(ball, dir, length - ringRadius * (1 - RingBand), cameraAt, cameraUp, on);
        BuildRing(ball + dir * length, ringRadius, on);
    }

    private void BuildLine(P2 ball, P2 dir, double end, double cameraAt, double cameraUp, Func<P2, Vector3> on)
    {
        var mesh = (ImmediateMesh)_line.Mesh;
        mesh.ClearSurfaces();
        if (end <= LineStart) return;   // a tap-in
        var across = new P2(-dir.Z, dir.X);
        int steps = (int)Math.Clamp((end - LineStart) / 0.75, 1, 160);   // short enough to follow the ground
        mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
        for (int i = 0; i <= steps; i++)
        {
            double s = LineStart + (end - LineStart) * i / steps;
            double half = Math.Clamp(Math.Sqrt((s - cameraAt) * (s - cameraAt) + cameraUp * cameraUp) * WidthPerMetre, MinHalfWidth, MaxHalfWidth);
            mesh.SurfaceAddVertex(on(ball + dir * s + across * half));
            mesh.SurfaceAddVertex(on(ball + dir * s - across * half));
        }
        mesh.SurfaceEnd();
    }

    private void BuildRing(P2 at, double radius, Func<P2, Vector3> on)
    {
        var mesh = (ImmediateMesh)_ring.Mesh;
        mesh.ClearSurfaces();
        if (radius <= 0) return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
        for (int i = 0; i <= RingSegments; i++)
        {
            double a = Math.Tau * i / RingSegments;
            var round = new P2(Math.Cos(a), Math.Sin(a));
            mesh.SurfaceAddVertex(on(at + round * radius));
            mesh.SurfaceAddVertex(on(at + round * (radius * (1 - RingBand))));
        }
        mesh.SurfaceEnd();
    }
}
