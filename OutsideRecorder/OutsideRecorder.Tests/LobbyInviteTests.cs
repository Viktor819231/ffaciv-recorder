namespace OutsideRecorderTests;

public sealed class LobbyInviteTests
{
    [Fact]
    public async Task LobbyConsentDoesNotAuthorizeTheFirstGameBeforeItsOwnPrivateAcknowledgement()
    {
        var share = new LiveShareSession();
        var channel = new string('c', 32);
        var context = new string('a', 32);
        share.Start("player", 4, channel);
        Assert.False(share.CanSendLobby);
        Assert.True(await share.PrepareAsync(channel, () => Task.FromResult(true)));
        Assert.True(await share.BindAsync(channel, _ => Task.FromResult(new LiveShareInvite(new string('b', 32), "https://ffaciv.com/replay/live/share/example", DateTimeOffset.UtcNow.AddHours(4))), _ => Task.CompletedTask));
        Assert.True(share.CanSendLobby);
        Assert.False(share.CanSend(context));
        Assert.False(await share.PrepareAsync(context, () => Task.FromResult(false), "11111111-22222222"));
        Assert.False(share.CanSend(context));
        share.Stop();
        Assert.False(share.CanSendLobby);
    }

    [Fact]
    public void RefreshDoesNotEnableMemoryReadingInSaveOnlyMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-refresh-" + Guid.NewGuid().ToString("N"));
        using var session = new RecorderSession(new RecorderSettings { OutputFolder = root, SafeCapture = true, TrackLogs = false, SaveFolders = [] }) { FindCiv = false };
        session.RequestLobbyRefresh();
        Assert.False(session.TrackLogs);
        Assert.Null(session.Status.Lobby);
    }
}
