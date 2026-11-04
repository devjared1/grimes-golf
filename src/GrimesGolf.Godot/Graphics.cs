using Godot;
using GrimesGolf.Core.Storage;

namespace GrimesGolf.Game;

/// <summary>
/// The 3D quality presets (Settings → Display): anti-aliasing, the sun's shadows and the screen-space lighting
/// effects. Medium is the look the app always had; High and Ultra add to it, Low takes away for slower machines.
/// </summary>
public static class Graphics
{
    /// <summary>What a preset turns on. Shadow distance is in metres; the atlas is the shadow map's size in pixels.</summary>
    private sealed record Preset(
        Viewport.Msaa Msaa, bool Taa, Viewport.ScreenSpaceAAEnum ScreenAa, float RenderScale,
        DirectionalLight3D.ShadowMode Splits, float ShadowDistance, int ShadowAtlas, RenderingServer.ShadowQuality Softness,
        Sky.RadianceSizeEnum Radiance, bool Ssao, bool Ssil, bool Ssr, bool Dof = false);

    private static Preset For(GraphicsQuality q) => q switch
    {
        // Upscaled from 80% (FSR 1), FXAA only, two shadow cascades and no ambient occlusion.
        GraphicsQuality.Low => new(Viewport.Msaa.Disabled, false, Viewport.ScreenSpaceAAEnum.Fxaa, 0.8f,
            DirectionalLight3D.ShadowMode.Parallel2Splits, 150, 2048, RenderingServer.ShadowQuality.SoftVeryLow,
            Sky.RadianceSizeEnum.Size128, false, false, false),
        GraphicsQuality.Medium => new(Viewport.Msaa.Msaa2X, false, Viewport.ScreenSpaceAAEnum.Disabled, 1,
            DirectionalLight3D.ShadowMode.Parallel4Splits, 250, 4096, RenderingServer.ShadowQuality.SoftLow,
            Sky.RadianceSizeEnum.Size256, true, false, false),
        // SMAA smooths what MSAA can't (leaf edges cut out in the foliage shader). Not TAA: the camera chases the ball,
        // and TAA blurs the whole view (and the yardage numbers) while it moves. SSIL bounces
        // light between nearby surfaces; SSR puts trees and banks in the water.
        GraphicsQuality.High => new(Viewport.Msaa.Msaa2X, false, Viewport.ScreenSpaceAAEnum.Smaa, 1,
            DirectionalLight3D.ShadowMode.Parallel4Splits, 320, 4096, RenderingServer.ShadowQuality.SoftMedium,
            Sky.RadianceSizeEnum.Size512, true, true, true, Dof: true),
        // Plus 4× MSAA and sharper, longer shadows. (Volumetric fog was tried for light shafts: it greys the whole view
        // once the camera rises after the ball.)
        _ => new(Viewport.Msaa.Msaa4X, false, Viewport.ScreenSpaceAAEnum.Smaa, 1,
            DirectionalLight3D.ShadowMode.Parallel4Splits, 450, 8192, RenderingServer.ShadowQuality.SoftHigh,
            Sky.RadianceSizeEnum.Size1024, true, true, true, Dof: true),
    };

    /// <summary>The preset last applied; things that scale with it themselves (<see cref="GrassField"/>) follow it.</summary>
    public static GraphicsQuality Current { get; private set; } = GraphicsQuality.High;

    /// <summary>Applies <paramref name="q"/> to the main view, the sky's lighting and the sun.</summary>
    public static void Apply(GraphicsQuality q, Viewport view, WorldEnvironment world, DirectionalLight3D sun)
    {
        Current = q;
        var p = For(q);
        view.Msaa3D = p.Msaa;
        view.UseTaa = p.Taa;
        view.ScreenSpaceAA = p.ScreenAa;
        view.Scaling3DMode = p.RenderScale < 1 ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
        view.Scaling3DScale = p.RenderScale;

        sun.DirectionalShadowMode = p.Splits;
        sun.DirectionalShadowBlendSplits = p.Splits == DirectionalLight3D.ShadowMode.Parallel4Splits;
        sun.DirectionalShadowMaxDistance = p.ShadowDistance;
        RenderingServer.DirectionalShadowAtlasSetSize(p.ShadowAtlas, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(p.Softness);

        var env = world.Environment;
        env.Sky.RadianceSize = p.Radiance;
        env.SsaoEnabled = p.Ssao;
        env.SsilEnabled = p.Ssil;
        env.SsilRadius = 3;
        env.SsilIntensity = 0.8f;
        env.SsrEnabled = p.Ssr;
        env.SsrMaxSteps = 48;
        env.SsrFadeOut = 1.5f;

        // A long lens's shallow focus up close: grass right in front of a low camera goes soft. (A far blur was tried
        // for the tree lines: it blurs the sky's clouds too.)
        if (view.GetCamera3D() is { } camera)
            camera.Attributes = p.Dof
                ? new CameraAttributesPractical { DofBlurNearEnabled = true, DofBlurNearDistance = 1.2f, DofBlurNearTransition = 0.8f, DofBlurAmount = 0.06f }
                : null;
    }
}
