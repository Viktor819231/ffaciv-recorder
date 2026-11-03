using System.Text.Json.Nodes;
using OutsideRecorderShell;

namespace OutsideRecorderTests;

public sealed class BatchUploadProgressTests
{
    [Theory]
    [InlineData(0, 0, "parsing")]
    [InlineData(4, 2, "parsing")]
    [InlineData(4, 4, "preview")]
    public void DistinguishesParsingFromPreview(int total, int parsed, string phase)
    {
        var result = BatchUploadProgress.Read(new JsonObject { ["saves"] = total, ["processed"] = parsed });
        Assert.Equal(phase, result["phase"]!.GetValue<string>());
        Assert.Empty(result["links"]!.AsArray());
    }

    [Theory]
    [InlineData(2, 0, "parsing")]
    [InlineData(4, 1, "error")]
    [InlineData(4, 0, "ready")]
    public void LinkRequiresAllSavesAndNoFailures(int parsed, int failures, string phase)
    {
        var result = BatchUploadProgress.Read(new JsonObject { ["saves"] = 4, ["processed"] = parsed, ["failed"] = failures,
            ["games"] = new JsonArray(new JsonObject { ["url"] = "https://example.test/game" }) });
        Assert.Equal(phase, result["phase"]!.GetValue<string>());
        Assert.Equal(phase == "ready" ? 1 : 0, result["links"]!.AsArray().Count);
    }
}
