using System.Text.Json;

namespace GrimesGolf.Core;

/// <summary>Where the app and its courses are published: the project's GitHub releases.</summary>
public static class Releases
{
    private const string Repo = "devjared1/grimes-golf";

    /// <summary>The downloadable courses are the assets of one release (a pre-release, so it's never "the latest version").</summary>
    public const string CoursesUrl = $"https://github.com/{Repo}/releases/download/courses/";

    /// <summary>The newest version of the app, as GitHub describes it.</summary>
    public const string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>A version of the app that's newer than this one, and the page to get it from.</summary>
    public sealed record Update(string Version, string Page);

    /// <summary>The update on offer, given GitHub's answer and this app's version ("0.9.7"); null if there's none, or the answer doesn't make sense.</summary>
    public static Update? Newer(string json, string current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string? tag = doc.RootElement.GetProperty("tag_name").GetString(), page = doc.RootElement.GetProperty("html_url").GetString();
            if (tag == null || page == null || !page.StartsWith($"https://github.com/{Repo}/releases/", StringComparison.Ordinal)) return null;
            string version = tag.TrimStart('v');
            return Parse(version) is { } latest && Parse(current) is { } ours && Compare(latest, ours) > 0 ? new Update(version, page) : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"v1.0.0-beta" → (1.0.0, "beta"); "0.9.8" → (0.9.8, null); null if it isn't a version.</summary>
    private static (Version Number, string? Label)? Parse(string text)
    {
        text = text.TrimStart('v');
        int dash = text.IndexOf('-');
        string number = dash < 0 ? text : text[..dash];
        string? label = dash < 0 ? null : text[(dash + 1)..];
        if (label is { Length: 0 } || !Version.TryParse(number, out var v)) return null;
        // 1.0 and 1.0.0 are the same version.
        return (new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)), label);
    }

    /// <summary>By number; at the same number a pre-release ("1.0.0-beta") comes before the release ("1.0.0").</summary>
    private static int Compare((Version Number, string? Label) a, (Version Number, string? Label) b)
    {
        int byNumber = a.Number.CompareTo(b.Number);
        if (byNumber != 0) return byNumber;
        if (a.Label == null || b.Label == null) return a.Label == null ? (b.Label == null ? 0 : 1) : -1;
        return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
    }
}
