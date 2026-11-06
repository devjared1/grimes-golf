using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// The app's logo and course logos (<c>assets/ui</c>). A course's logo is found by its name:
/// "Deer Run" → <c>res://assets/ui/deer_run_logo.png</c>; a course without one just shows no logo.
/// </summary>
public static class Brand
{
    public const string Name = "Grimes Golf";

    private static Texture2D? _logo;
    private static bool _looked;

    /// <summary>
    /// The Grimes Golf Simulator logo (a neon green script, about 4:1, for dark backgrounds; made by
    /// <c>tools/make-brand-assets.py</c>); null if it's missing.
    /// </summary>
    public static Texture2D? Logo
    {
        get
        {
            if (!_looked) { _looked = true; _logo = Load("res://assets/ui/grimes_golf_logo.png"); }
            return _logo;
        }
    }

    /// <summary>The logo at a size on screen. The image is much bigger than any of them, so it's drawn from its mipmaps to keep the fine script clean.</summary>
    public static TextureRect LogoRect(Texture2D logo, Vector2 size, TextureRect.StretchModeEnum stretch) => new()
    {
        Texture = logo, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = stretch,
        TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps, CustomMinimumSize = size,
    };

    private static readonly Dictionary<string, Texture2D?> CourseLogos = new();

    public static Texture2D? CourseLogo(string? courseName)
    {
        if (string.IsNullOrWhiteSpace(courseName)) return null;
        string slug = new string(courseName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (!CourseLogos.TryGetValue(slug, out var tex))
            CourseLogos[slug] = tex = Load($"res://assets/ui/{slug}_logo.png");
        return tex;
    }

    private static Texture2D? Load(string path) => ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
}
