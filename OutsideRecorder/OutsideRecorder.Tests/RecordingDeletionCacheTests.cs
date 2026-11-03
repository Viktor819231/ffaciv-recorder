using System.Text.Json.Nodes;
using OutsideRecorderShell;

namespace OutsideRecorderTests;

public sealed class RecordingDeletionCacheTests
{
    [Fact]
    public void QueuedDeleteDisappearsFromHeldCardsAndStaleLibraryAnswersAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "held-deletion-cards-" + Guid.NewGuid().ToString("N"));
        var context = GameIndex.NewContext();
        var workload = new WorkloadPolicy(); workload.SetQuiet(true);
        var site = new FakeSite();
        var settings = new RecorderSettings { OutputFolder = root, Stream = "local", SafeCapture = true };
        try
        {
            using (var first = new Streamer(settings, site) { Workload = workload })
            {
                first.QueueReplayDeletion(context, "vanilla", "game-one");
                Assert.True(first.ReplayDeletionQueued(context));
            }
            using var restarted = new Streamer(settings, new FakeSite()) { Workload = workload };
            var filter = new RecordingDeletionCache();
            filter.Remember(restarted.QueuedReplayDeletions());
            var local = new JsonObject { ["id"] = "session-old", ["context"] = context, ["siteMod"] = "vanilla", ["siteGame"] = "game-one" };
            var remote = new JsonObject { ["mod"] = "vanilla", ["game"] = "game-one", ["pageUrl"] = "https://example.test/game-one" };
            var other = new JsonObject { ["id"] = "session-other", ["context"] = GameIndex.NewContext(), ["siteMod"] = "vanilla", ["siteGame"] = "game-two" };
            var cached = new JsonArray(local, remote, other);
            var visible = filter.Filter(cached);
            Assert.Equal("session-other", Assert.Single(visible)!["id"]!.GetValue<string>());
            Assert.Equal(3, cached.Count); // Also handles a later asynchronous response containing stale rows.
            Assert.Single(filter.Filter(cached));
            Assert.True(workload.CanStartHeavyWork == false);
            Assert.Equal(0, site.Requests);
            Assert.True(restarted.ReplayDeletionQueued(context));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ExactLocalDeletionDoesNotHideUnrelatedRowsAndMatchesCurrentContext()
    {
        var filter = new RecordingDeletionCache();
        var context = GameIndex.NewContext();
        filter.Remember(new JsonObject { ["id"] = "session-old", ["context"] = context, ["identity"] = "identity-one" });
        var cards = new JsonArray(new JsonObject { ["id"] = "session-old" }, new JsonObject { ["id"] = "session-other" });
        Assert.Equal("session-other", Assert.Single(filter.Filter(cards))!["id"]!.GetValue<string>());
        Assert.True(filter.Hides(new JsonObject { ["id"] = "live", ["context"] = context }));
        Assert.True(filter.Hides(new JsonObject { ["id"] = "other-copy", ["identity"] = "identity-one" }));
        Assert.False(filter.Hides(new JsonObject { ["id"] = "other-copy", ["identity"] = "identity-two" }));
    }
}
