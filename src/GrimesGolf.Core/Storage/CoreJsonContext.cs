using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.OpenConnect;

namespace GrimesGolf.Core.Storage;

/// <summary>
/// How to read and write this assembly's JSON types, generated when it's built. A build compiled ahead of time (the
/// iPad's) can't work that out by reflection while it runs, so it uses this; every other build uses reflection, as
/// it always has. A type saved or sent as JSON must be listed here, or the iPad build fails where it's used
/// (<c>tests/GrimesGolf.AotSmoke</c> checks).
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SavedRound))]
[JsonSerializable(typeof(ShotRecord))]
[JsonSerializable(typeof(MatchSnapshot))]
[JsonSerializable(typeof(OpenConnectMessage))]
[JsonSerializable(typeof(OpenConnectResponse))]
[JsonSerializable(typeof(CourseCatalog.Document))]
// (Two nested types share a name, so each is given its own.)
[JsonSerializable(typeof(SavedRound.Position), TypeInfoPropertyName = "SavedRoundPosition")]
[JsonSerializable(typeof(MatchSnapshot.Position), TypeInfoPropertyName = "MatchSnapshotPosition")]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
    /// <summary>For <see cref="JsonSerializerOptions.TypeInfoResolver"/>: null (reflection) unless the build has it turned off.</summary>
    public static IJsonTypeInfoResolver? Resolver => JsonSerializer.IsReflectionEnabledByDefault ? null : Default;
}
