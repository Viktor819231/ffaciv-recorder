namespace OutsideRecorderTests;

public sealed class DelayedUploadsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "recorder-upload-hold-" + Guid.NewGuid().ToString("N"));
    DateTimeOffset now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    readonly string context = GameIndex.NewContext();
    DelayedUploads Queue() => new(root, () => now);
    GameEntry Game => new() { Context = context, Identity = "files:" + context, Folder = Path.Combine(root, "session-test") };

    [Fact]
    public void DuringPlayOnlyReleasesActiveRecordingAndRespectsPerGameHolds()
    {
        var queue = Queue();
        Assert.True(queue.CanUpload(Game, true, activeRecording: true));
        Assert.False(queue.CanUpload(Game, false, activeRecording: true));
        Assert.False(queue.CanUpload(Game, true, activeRecording: false));
        queue.KeepLocal(context);
        Assert.False(queue.CanUpload(Game, true, activeRecording: true));
        queue.Postpone(context, TimeSpan.FromHours(2));
        queue.Resume(context);
        Assert.True(queue.Get(context).Postponed);
        Assert.False(queue.CanUpload(Game, true, activeRecording: true));
        File.WriteAllText(Path.Combine(root, "upload-decisions", context + ".json"), "{}");
        Assert.False(queue.CanUpload(Game, true, activeRecording: true));
        File.WriteAllText(Path.Combine(root, "upload-decisions", context + ".json"), "{broken");
        Assert.False(queue.CanUpload(Game, true, activeRecording: true));
    }

    [Fact]
    public void AutomaticOffHoldsSchedulesButExplicitManualUploadRemainsAvailable()
    {
        var queue = Queue();
        queue.Schedule(context);
        queue.MarkHold(context, "hidden");
        Assert.True(queue.CanUpload(Game, automaticAllowed: true));
        Assert.False(queue.CanUpload(Game, automaticAllowed: false));
        queue.UploadNow(context);
        Assert.True(queue.CanUpload(Game, automaticAllowed: false));
        Assert.Equal("kept", queue.Get(context).Hold);
    }

    [Fact]
    public void FinishedGameUploadsOnceHiddenAndGoesPublicAfterTwoHoursAndAWarning()
    {
        // the user, 2026-11-02: "wait 2 hours to become visible" instead of waiting 2 hours to upload
        var queue = Queue();
        Assert.False(queue.CanUpload(Game));
        var scheduled = queue.Schedule(context);
        Assert.False(queue.CanUpload(Game));            // not before the site copy is hidden
        queue.MarkHold(context, "hidden");
        Assert.True(Queue().CanUpload(Game));            // uploads at once, survives a restart
        now = now.AddHours(1);
        Assert.Equal(scheduled.DueAt, Queue().Schedule(context).DueAt);
        now = now.AddMinutes(55);
        Assert.Empty(Queue().TakeReveals());
        var warning = Assert.Single(Queue().TakeWarnings());
        Assert.Equal(now.AddMinutes(5), warning.DueAt);
        Assert.Empty(Queue().TakeWarnings());
        now = now.AddMinutes(4);
        Assert.Empty(Queue().TakeReveals());
        now = now.AddMinutes(1);
        Assert.Equal(context, Assert.Single(Queue().TakeReveals()).Context);
        Queue().Revealed(context);
        Assert.Empty(Queue().TakeReveals());
        Assert.Equal("upload", Queue().Get(context).State);
        Assert.True(Queue().CanUpload(Game, automaticAllowed: false));
    }

    [Fact]
    public void OverdueRestartGrantsFullFiveMinutesAndHidingLongerRequiresNewWarning()
    {
        Queue().Schedule(context);
        Queue().MarkHold(context, "hidden");
        now = now.AddDays(1);
        Assert.Empty(Queue().TakeReveals());
        var warning = Assert.Single(Queue().TakeWarnings());
        Assert.Equal(now.AddMinutes(5), warning.DueAt);
        Queue().Postpone(context, TimeSpan.FromDays(1));
        Assert.True(Queue().CanUpload(Game));            // hiding longer does not stop the upload
        now = now.AddDays(1);
        Assert.Empty(Queue().TakeReveals());
        Assert.Single(Queue().TakeWarnings());
        now = now.AddMinutes(5);
        Assert.Single(Queue().TakeReveals());
    }

    [Fact]
    public void KeptOrHandChosenVisibilityIsNeverMadePublicAutomatically()
    {
        Queue().Schedule(context);
        Queue().MarkHold(context, "kept");               // already on the site: it keeps its visibility
        Assert.True(Queue().CanUpload(Game));
        now = now.AddHours(3);
        Assert.Single(Queue().TakeWarnings());
        now = now.AddMinutes(5);
        Assert.Empty(Queue().TakeReveals());
        var other = GameIndex.NewContext();
        Queue().Schedule(other); Queue().MarkHold(other, "hidden");
        Queue().EndHold(other);                          // the player picked private or public by hand
        Assert.Equal("kept", Queue().Get(other).Hold);
    }

    [Fact]
    public void RejoinCancelsCountdownAndExplicitLocalChoiceSurvivesScheduling()
    {
        Queue().Schedule(context);
        Queue().MarkHold(context, "hidden");
        Queue().MarkEndPrompted(context);
        Queue().Resume(context);
        Assert.Equal("recording", Queue().Get(context).State);
        Assert.Null(Queue().Get(context).DueAt);
        Assert.False(Queue().Get(context).EndPrompted);
        Assert.False(Queue().CanUpload(Game));            // a rejoined game sends nothing more until it ends again
        Assert.Equal("hidden", Queue().Schedule(context).Hold);   // still hidden: uploads again at once when it ends
        Assert.True(Queue().CanUpload(Game));
        Queue().Resume(context);
        Queue().KeepLocal(context);
        Assert.Equal("local", Queue().Schedule(context).State);
        Queue().Resume(context);
        Assert.False(Queue().CanUpload(Game));
        Queue().UploadNow(context);
        Assert.True(Queue().CanUpload(Game));
    }

    [Fact]
    public async Task HeldRecordingSendsNeitherSavesNorEventsAndGlobalOffStillWins()
    {
        Directory.CreateDirectory(Path.Combine(Game.Folder, "saves"));
        File.WriteAllText(Path.Combine(Game.Folder, "events.jsonl"), "{\"kind\":\"test\"}\n");
        File.WriteAllBytes(Path.Combine(Game.Folder, "saves", "AutoSave_0001.Civ5Save"), new byte[32]);
        new GameIndex(root).Save(Game);
        var site = new FakeSite();
        var queue = Queue();
        using var stream = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local", SaveUpload = "full" }, site)
        { CanUploadGame = queue.CanUpload };
        queue.Schedule(context);
        await stream.PassAsync(default);
        Assert.Empty(site.Saves); Assert.Empty(site.Chunks);
        queue.UploadNow(context);
        stream.CanUpload = () => false;
        await stream.PassAsync(default);
        Assert.Empty(site.Saves); Assert.Empty(site.Chunks);
        stream.CanUpload = () => true;
        await stream.PassAsync(default);
        Assert.Single(site.Saves); Assert.Single(site.Chunks);
    }

    [Fact]
    public void InvalidOrCorruptDecisionsFailClosed()
    {
        Directory.CreateDirectory(Path.Combine(root, "upload-decisions"));
        File.WriteAllText(Path.Combine(root, "upload-decisions", context + ".json"), "{broken");
        Assert.False(Queue().CanUpload(Game));
        Assert.Throws<ArgumentException>(() => Queue().UploadNow("../outside"));
    }

    [Fact]
    public void MainMenuEndsCaptureUntilTheGameResumes()
    {
        Directory.CreateDirectory(Game.Folder);
        var events = Path.Combine(Game.Folder, "events.jsonl");
        File.WriteAllText(events, "{\"kind\":\"lifecycle\",\"state\":\"game_left\"}\n");
        Assert.True(Streamer.HasEnded(Game));
        File.AppendAllText(events, "{\"kind\":\"lifecycle\",\"state\":\"game_resumed\"}\n");
        Assert.False(Streamer.HasEnded(Game));
        File.AppendAllText(events, "{\"kind\":\"snapshot\",\"turn\":12}\n");
        Assert.False(Streamer.HasEnded(Game));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
