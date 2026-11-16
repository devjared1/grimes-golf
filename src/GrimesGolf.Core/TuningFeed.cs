using System.Text.Json.Nodes;

namespace GrimesGolf.Core;

/// <summary>
/// The physics tuning published for everyone (<see cref="Releases.TuningUrl"/>): an ordinary tuning file plus a
/// <c>"version"</c> number that goes up with each publication. The app replaces its start-up tuning with it only when
/// the version is higher than the last one it applied, so tuning changed since stays until the next publication.
/// </summary>
public static class TuningFeed
{
    /// <summary>A published tuning and its version.</summary>
    public sealed record Published(int Version, PhysicsTuning Tuning);

    /// <summary>Reads what was downloaded; null if it isn't a tuning file with a version and at least some known values.</summary>
    public static Published? Read(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["values"] is not JsonObject values) return null;
            if (root["version"] is not JsonValue v || !v.TryGetValue<int>(out int version) || version < 1) return null;
            // A file with none of our keys would read as "all defaults" and wipe the tuning: don't take it.
            if (!PhysicsTuning.Parameters.Any(p => values.ContainsKey(p.Key))) return null;
            return new Published(version, PhysicsTuning.FromJson(json));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The version last applied, from the text saved beside the data; 0 if there is none.</summary>
    public static int ParseApplied(string? text) => int.TryParse(text?.Trim(), out int n) && n > 0 ? n : 0;
}
