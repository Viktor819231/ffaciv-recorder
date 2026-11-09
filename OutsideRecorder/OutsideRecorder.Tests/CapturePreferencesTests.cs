using System.Text.Json.Nodes;
using OutsideRecorderShell;

namespace OutsideRecorderTests;

public sealed class CapturePreferencesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "capture-preferences-" + Guid.NewGuid().ToString("N"));
    RecorderSession Session() => new((new RecorderSettings { OutputFolder = root, SaveFolders = [] }).ForDesktop(false)) { FindCiv = false };
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void LegacyLoggingPreferenceCannotEnableMemoryWithoutExplicitPermission()
    {
        using var session = Session();
        CapturePreferences.Apply(session, new JsonObject { ["trackLogs"] = true }, false);
        Assert.False(session.TrackLogs);
        Assert.False(session.AllowExperimentalMemoryLogging);
    }

    [Fact]
    public void ExplicitPermissionUsesFourHzAndCanBeRevoked()
    {
        using var session = Session();
        var settings = new JsonObject { ["experimentalMemoryOptIn"] = true, ["trackLogs"] = true };
        CapturePreferences.Apply(session, settings, false);
        Assert.True(session.TrackLogs);
        Assert.Equal(250, session.SampleMs);
        settings["trackLogs"] = false;
        CapturePreferences.Apply(session, settings, false);
        Assert.False(session.TrackLogs);
        Assert.False(session.AllowExperimentalMemoryLogging);
    }

    [Fact]
    public void ManualCaptureDisablesReadsButRetainsTheUsersMemoryPreference()
    {
        using var session = Session();
        var settings = new JsonObject { ["experimentalMemoryOptIn"] = true, ["trackLogs"] = true, ["recording"] = false };
        CapturePreferences.Apply(session, settings, false);
        Assert.False(session.TrackLogs);
        Assert.False(session.RecordingEnabled);
        settings["recording"] = true;
        CapturePreferences.Apply(session, settings, false);
        Assert.True(session.TrackLogs);
        Assert.True(session.RecordingEnabled);
        CapturePreferences.Apply(session, settings, true);
        Assert.False(session.RecordingEnabled);
    }

    [Fact]
    public void OldGamesFollowTheRecordingMethodUntilThePlayerAnswersTheQuestion()
    {
        var settings = new JsonObject { ["saveUpload"] = "full" };
        Assert.Null(CapturePreferences.OldGamesChoice(settings));
        Assert.Equal("full", CapturePreferences.OldGamesFormat(settings));
        settings["oldGamesUpload"] = "delta";   // answered "Smaller upload" on the first Manual upload
        Assert.Equal("delta", CapturePreferences.OldGamesFormat(settings));
        Assert.Equal("full", CapturePreferences.Format(settings));   // live recording keeps its own method
        settings["oldGamesUpload"] = "nonsense";
        Assert.Null(CapturePreferences.OldGamesChoice(settings));
        Assert.Equal("full", CapturePreferences.OldGamesFormat(settings));
    }

    [Fact]
    public void ExplicitWhilePlayingChoiceControlsTheActualSenderGate()
    {
        var policy = new UploadPolicy { IsPlaying = () => true };
        var settings = new JsonObject { ["uploadTiming"] = "during" };
        CapturePreferences.ApplyUploadPolicy(policy, settings, false);
        Assert.True(policy.CanSend());
        settings["uploadTiming"] = "after";
        CapturePreferences.ApplyUploadPolicy(policy, settings, false);
        Assert.False(policy.CanSend());
        settings["uploadTiming"] = "during";
        CapturePreferences.ApplyUploadPolicy(policy, settings, true);
        Assert.False(policy.CanSend());
    }

    [Theory]
    [InlineData(null, "delta")]
    [InlineData("delta", "delta")]
    [InlineData("full", "full")]
    [InlineData("unknown", "delta")]
    public void UploadMethodDefaultsToSmallerLocalPreparation(string? value, string expected)
        => Assert.Equal(expected, CapturePreferences.Format(new JsonObject { ["saveUpload"] = value }));

    [Fact]
    public void SimpleSetupCopiesSavesAndUploadsThemWholeAfterTheGameWithoutMemory()
    {
        using var session = Session();
        var settings = new JsonObject { ["experimentalMemoryOptIn"] = true, ["trackLogs"] = true, ["captureMoves"] = true,
            ["recording"] = false, ["saveUpload"] = "delta", ["uploadTiming"] = "during", ["stream"] = false, ["startWithWindows"] = true };
        CapturePreferences.ApplySimpleSetup(settings);
        CapturePreferences.Apply(session, settings, false);
        Assert.True(session.RecordingEnabled);
        Assert.False(session.TrackLogs);
        Assert.False(session.AllowExperimentalMemoryLogging);
        Assert.False(CapturePreferences.MemoryRequested(settings));
        Assert.Equal("full", CapturePreferences.Format(settings));
        Assert.False(CapturePreferences.UploadDuringPlay(settings));
        Assert.True(settings["stream"]!.GetValue<bool>());
        Assert.True(settings["startWithWindows"]!.GetValue<bool>());
    }
}
