using System.Text.Json.Nodes;
using OutsideRecorderShell;

namespace OutsideRecorderTests;

public sealed class StreamingSetupPolicyTests
{
    [Fact]
    public void EndedStreamCanDrainFinalSaveOrHonorUploadNowWithoutAuthorizingOtherActiveGames()
    {
        Assert.True(StreamingSetupPolicy.RecordingAllowed(true,true,true,false,true,false));
        Assert.True(StreamingSetupPolicy.RecordingAllowed(true,false,false,false,true,true));
        Assert.False(StreamingSetupPolicy.RecordingAllowed(true,true,false,false,true,false));
        Assert.False(StreamingSetupPolicy.RecordingAllowed(true,true,true,false,false,false));
        Assert.False(StreamingSetupPolicy.RecordingAllowed(true,true,false,false,false,true));
        Assert.True(StreamingSetupPolicy.RecordingAllowed(true,true,true,true,false,false));
    }

    [Fact]
    public void MenuProofExpiresAndCannotGrantIdleUploadFromUnknownOrFutureObservations()
    {
        Assert.False(new RecorderStatus().HasRecentMenuObservation(1000));
        var status=new RecorderStatus {MenuObservedAt=1000};
        Assert.True(status.HasRecentMenuObservation(2000));
        Assert.False(status.HasRecentMenuObservation(4000));
        Assert.False(status.HasRecentMenuObservation(999));
    }
    static JsonObject Ready() => new() { ["recording"] = true, ["experimentalMemoryOptIn"] = true,
        ["trackLogs"] = true, ["stream"] = true, ["uploadTiming"] = "during", ["saveUpload"] = "full" };

    [Fact]
    public void UnreadableProgressCannotMakeAnOldBacklogLookEmpty()
    {
        var folder = Path.Combine(Path.GetTempPath(), "stream-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            File.WriteAllText(Path.Combine(folder, "stream.json"), "broken");
            Assert.Equal(int.MaxValue, StreamingSetupPolicy.QueuedSaves(folder));
        } finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ClosedGameCanPrepareSettingsAndInviteWithoutRequiringSteamToClose()
    {
        Assert.Null(StreamingSetupPolicy.EnableReason(new(), false, false, false, false, 100));
        Assert.Null(StreamingSetupPolicy.ChangeReason(new(), "trackLogs", JsonValue.Create(true), false, false));
        Assert.NotNull(StreamingSetupPolicy.EnableReason(new(), true, false, false, false, 0));
        Assert.Null(StreamingSetupPolicy.EnableReason(new(), true, true, false, false, 0));
    }

    [Fact]
    public void ActiveGameNeedsPreexistingSettingsAndSmallBacklog()
    {
        Assert.NotNull(StreamingSetupPolicy.EnableReason(new(), true, false, true, false, 0));
        Assert.NotNull(StreamingSetupPolicy.EnableReason(Ready(), true, false, true, false, 100));
        Assert.Null(StreamingSetupPolicy.EnableReason(Ready(), true, false, true, false, 100, latestOnly:true));
        Assert.NotNull(StreamingSetupPolicy.EnableReason(Ready(), true, false, true, false, int.MaxValue, latestOnly:true));
        Assert.NotNull(StreamingSetupPolicy.EnableReason(new(), true, false, true, false, 100, latestOnly:true));
        Assert.NotNull(StreamingSetupPolicy.EnableReason(Ready(), true, false, true, true, 100, latestOnly:true));
        Assert.Null(StreamingSetupPolicy.EnableReason(Ready(), true, false, true, false, 2));
        var after = Ready(); after["uploadTiming"] = "after";
        Assert.Null(StreamingSetupPolicy.EnableReason(after, true, false, true, false, 0));
        after["stream"] = false;
        Assert.Null(StreamingSetupPolicy.EnableReason(after, true, false, true, false, 0));
        Assert.NotNull(StreamingSetupPolicy.EnableReason(Ready(), true, false, true, true, 0));
    }

    [Fact]
    public void FutureLinkDoesNotReleaseExistingContextEvenAfterItsBacklogShrinks()
    {
        Assert.Null(StreamingSetupPolicy.FutureInviteReason(Ready(), true, false));
        Assert.NotNull(StreamingSetupPolicy.FutureInviteReason(new(), true, false));
        Assert.Null(StreamingSetupPolicy.FutureInviteReason(new(), false, false));
        var existing = new HashSet<string> { "current", "old" };
        Assert.True(StreamingSetupPolicy.AwaitFreshContext(true, "current", existing));
        Assert.True(StreamingSetupPolicy.AwaitFreshContext(true, "old", existing));
        Assert.False(StreamingSetupPolicy.AwaitFreshContext(true, "new", existing));
        Assert.False(StreamingSetupPolicy.AwaitFreshContext(false, "current", existing));
    }

    [Theory]
    [InlineData("trackLogs", "true")]
    [InlineData("captureMoves", "true")]
    [InlineData("uploadTiming", "\"during\"")]
    [InlineData("saveUpload", "\"delta\"")]
    [InlineData("stream", "true")]
    public void HeavyEnablingChangesAreBlockedDuringPlayButDisablingIsAllowed(string key, string json)
    {
        var settings = new JsonObject { ["saveUpload"] = "full", ["uploadTiming"] = "after", ["stream"] = false };
        Assert.NotNull(StreamingSetupPolicy.ChangeReason(settings, key, JsonNode.Parse(json), true, false));
        Assert.Null(StreamingSetupPolicy.ChangeReason(settings, key, JsonNode.Parse(json), true, true));
        Assert.Null(StreamingSetupPolicy.ChangeReason(Ready(), "trackLogs", JsonValue.Create(false), true, false));
        Assert.Null(StreamingSetupPolicy.ChangeReason(Ready(), "stream", JsonValue.Create(false), true, false));
        Assert.Null(StreamingSetupPolicy.ChangeReason(Ready(), "uploadTiming", JsonValue.Create("after"), true, false));
    }
}
