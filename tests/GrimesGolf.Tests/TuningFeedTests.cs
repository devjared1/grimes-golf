using GrimesGolf.Core;
using Xunit;

namespace GrimesGolf.Tests;

public sealed class TuningFeedTests
{
    [Fact]
    public void APublishedTuningIsReadWithItsVersionAndClampedValues()
    {
        var p = TuningFeed.Read("""{"name":"Studio","version":3,"values":{"flight.drag":0.9,"flight.dragSpeedTilt":-9}}""");
        Assert.NotNull(p);
        Assert.Equal(3, p!.Version);
        Assert.Equal("Studio", p.Tuning.Name);
        Assert.Equal(0.9, p.Tuning["flight.drag"], 3);
        Assert.Equal(-0.5, p.Tuning["flight.dragSpeedTilt"], 3);   // out of range: clamped
        Assert.Equal(1, p.Tuning["flight.lift"], 3);                // missing: default
    }

    [Theory]
    [InlineData("""{"name":"x","values":{"flight.drag":0.9}}""")]                 // no version
    [InlineData("""{"version":0,"values":{"flight.drag":0.9}}""")]                // not a real version
    [InlineData("""{"version":2,"values":{"nonsense":1}}""")]                     // no known values: would reset everything
    [InlineData("""{"version":2}""")]
    [InlineData("""{"version":"two","values":{"flight.drag":0.9}}""")]
    [InlineData("<html>rate limited</html>")]
    [InlineData("[]")]
    public void NonsenseIsNotTaken(string json) => Assert.Null(TuningFeed.Read(json));

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("junk", 0)]
    [InlineData("4\n", 4)]
    public void TheAppliedVersionIsReadFromTheSavedText(string? text, int expected) => Assert.Equal(expected, TuningFeed.ParseApplied(text));
}
