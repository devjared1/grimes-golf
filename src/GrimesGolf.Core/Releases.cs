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
            return Version.TryParse(version, out var latest) && Version.TryParse(current.TrimStart('v'), out var ours) && latest > ours ? new Update(version, page) : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
