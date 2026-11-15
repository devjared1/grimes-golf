using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GrimesGolf.Online.Session;

namespace GrimesGolf.Online.Protocol;

/// <summary>
/// How to read and write the online messages and the host's saved round, generated when the assembly is built, for
/// builds compiled ahead of time (the iPad's). Every other build uses reflection, as it always has. See
/// <c>CoreJsonContext</c> in GrimesGolf.Core.
/// </summary>
[JsonSerializable(typeof(OnlineMessage))]
[JsonSerializable(typeof(OnlineSave))]
internal sealed partial class OnlineJsonContext : JsonSerializerContext
{
    /// <summary>For <see cref="JsonSerializerOptions.TypeInfoResolver"/>: null (reflection) unless the build has it turned off.</summary>
    public static IJsonTypeInfoResolver? Resolver => JsonSerializer.IsReflectionEnabledByDefault ? null : Default;
}
