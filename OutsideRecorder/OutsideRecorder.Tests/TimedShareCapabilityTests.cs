using System.Text.Json;

namespace OutsideRecorderTests;

public sealed class TimedShareCapabilityTests
{
    [Fact]
    public void CapabilityIsUnknownBeforeConfig() => Assert.Null(new StreamStatus().TimedShares);

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"recorder\":{\"lifecycle\":true,\"controlsVersion\":2}}", false)]
    [InlineData("{\"recorder\":{\"timedShares\":false}}", false)]
    [InlineData("{\"recorder\":{\"timedShares\":true}}", true)]
    public void SuccessfulConfigRequiresExplicitTimedShareSupport(string json, bool supported)
    {
        using var config = JsonDocument.Parse(json);
        Assert.Equal(supported, Streamer.SupportsTimedShares(config.RootElement));
    }
}
