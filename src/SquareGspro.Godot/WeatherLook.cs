using Godot;
using SquareGspro.Core.Course;

namespace SquareGspro.Range3D;

/// <summary>
/// How a round's weather looks (the physics side is <see cref="GameSetup.ApplyTo"/>): mild is the sky as it is; hot
/// warms the light and thickens the haze; cold cools and greys it a little; wet is overcast (a dimmer, greyer sky,
/// soft light, mist in the distance) with rain falling around the camera. Changes the scene's sky, sun and haze, and
/// puts them back for mild weather.
/// </summary>
public partial class WeatherLook : Node3D
{
    /// <summary>The rain's render layer: the main view's only (the overview map doesn't show it).</summary>
    private const uint RainLayer = ShotPlayer.TrailLayer;

    private readonly WorldEnvironment _world;
    private readonly DirectionalLight3D _sun;
    private readonly (Color SunColor, float SunEnergy, float Sky, float Ambient, float Saturation, Color Fog, float FogDensity, float FogSky, float Shadow) _clear;
    private GpuParticles3D? _rain;

    public WeatherLook(WorldEnvironment world, DirectionalLight3D sun)
    {
        _world = world;
        _sun = sun;
        var env = world.Environment;
        _clear = (sun.LightColor, sun.LightEnergy, ((PanoramaSkyMaterial)env.Sky.SkyMaterial).EnergyMultiplier, env.AmbientLightEnergy,
                  env.AdjustmentSaturation, env.FogLightColor, env.FogDensity, env.FogSkyAffect, sun.ShadowOpacity);
    }

    // Godot needs a parameterless constructor for scripts it may instantiate itself.
    public WeatherLook() : this(new WorldEnvironment { Environment = new Godot.Environment { Sky = new Sky { SkyMaterial = new PanoramaSkyMaterial() } } }, new DirectionalLight3D()) { }

    public Weather Current { get; private set; } = Weather.Mild;

    public void Set(Weather weather)
    {
        Current = weather;
        var env = _world.Environment;
        var sky = (PanoramaSkyMaterial)env.Sky.SkyMaterial;
        var c = _clear;
        // Start from the clear look, then change what this weather changes.
        _sun.LightColor = c.SunColor;
        _sun.LightEnergy = c.SunEnergy;
        _sun.ShadowOpacity = c.Shadow;
        sky.EnergyMultiplier = c.Sky;
        env.AmbientLightEnergy = c.Ambient;
        env.AdjustmentSaturation = c.Saturation;
        env.FogLightColor = c.Fog;
        env.FogDensity = c.FogDensity;
        env.FogSkyAffect = c.FogSky;
        switch (weather)
        {
            case Weather.Hot:
                _sun.LightColor = new Color(1.0f, 0.92f, 0.80f);
                _sun.LightEnergy = c.SunEnergy * 1.08f;
                env.FogLightColor = new Color(0.86f, 0.84f, 0.78f);
                env.FogDensity = c.FogDensity * 1.8f;
                break;
            case Weather.Cold:
                _sun.LightColor = new Color(0.90f, 0.95f, 1.0f);
                _sun.LightEnergy = c.SunEnergy * 0.9f;
                env.AdjustmentSaturation = c.Saturation * 0.88f;
                break;
            case Weather.Wet:
                // Overcast: the sun behind cloud (soft, faint shadows), a dull sky, mist hiding the far trees.
                _sun.LightColor = new Color(0.86f, 0.89f, 0.93f);
                _sun.LightEnergy = c.SunEnergy * 0.4f;
                _sun.ShadowOpacity = 0.35f;
                sky.EnergyMultiplier = c.Sky * 0.6f;
                env.AmbientLightEnergy = c.Ambient * 1.25f;
                env.AdjustmentSaturation = c.Saturation * 0.72f;
                env.FogLightColor = new Color(0.55f, 0.58f, 0.62f);
                env.FogDensity = 0.0032f;
                env.FogSkyAffect = 0.55f;
                break;
        }
        if (weather == Weather.Wet) (_rain ??= BuildRain()).Emitting = true;
        if (_rain != null) _rain.Visible = _rain.Emitting = weather == Weather.Wet;
    }

    /// <summary>Rain: thin streaks falling through a box around the camera (in world space, so they don't swing with it).</summary>
    private GpuParticles3D BuildRain()
    {
        var drop = new QuadMesh { Size = new Vector2(0.008f, 0.5f) };
        drop.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.88f, 0.91f, 0.97f, 0.3f),
            BillboardMode = BaseMaterial3D.BillboardModeEnum.FixedY,
            BillboardKeepScale = true,
            // Drops right in front of the lens would be great white bars: they fade out close to the camera.
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
            DistanceFadeMinDistance = 2.5f,
            DistanceFadeMaxDistance = 6f,
        };
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(14, 8, 14),
            Direction = new Vector3(0.08f, -1, 0.03f),
            Spread = 2,
            InitialVelocityMin = 16,
            InitialVelocityMax = 20,
            Gravity = new Vector3(0, -9.8f, 0),
        };
        var rain = new GpuParticles3D
        {
            Amount = 8000,
            Lifetime = 1.3,
            Preprocess = 1.3,
            LocalCoords = false,
            ProcessMaterial = process,
            DrawPass1 = drop,
            Layers = RainLayer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-40, -40, -40), new Vector3(80, 60, 80)),
        };
        AddChild(rain);
        return rain;
    }

    /// <summary>The rain keeps falling around wherever the camera is (a little above it, so it falls past the view).</summary>
    public override void _Process(double delta)
    {
        if (_rain is not { Emitting: true } rain || GetViewport().GetCamera3D() is not { } camera) return;
        rain.GlobalPosition = camera.GlobalPosition + new Vector3(0, 5, 0) - camera.GlobalBasis.Z * 8;
    }
}
