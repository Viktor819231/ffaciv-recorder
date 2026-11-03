namespace OutsideRecorderTests;

public sealed class SafeCaptureTests
{
    [Fact]
    public void ChangingFilesMustSettleAgainForFiveSeconds()
    {
        var root=Path.Combine(Path.GetTempPath(),"settled-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var now=DateTime.UtcNow;var watcher=new AutosaveWatcher([root],settleFor:TimeSpan.FromSeconds(5),utcNow:()=>now);
            var file=Path.Combine(root,"AutoSave_0001.Civ5Save");File.WriteAllBytes(file,new byte[128]);
            Assert.Empty(watcher.Poll());now=now.AddSeconds(4);Assert.Empty(watcher.Poll());
            File.AppendAllText(file,"still writing");Assert.Empty(watcher.Poll());
            now=now.AddSeconds(4);Assert.Empty(watcher.Poll());now=now.AddSeconds(1);var save=Assert.Single(watcher.Poll());
            var copy=SafeFiles.CopyStableSave(save,Path.Combine(root,"stored"));Assert.NotNull(copy);
            Assert.Equal(File.ReadAllBytes(file),File.ReadAllBytes(copy!));Assert.Empty(watcher.Poll());
            File.AppendAllText(file,"changed again");Assert.Null(SafeFiles.CopyStableSave(save,Path.Combine(root,"stored")));
            Assert.Empty(Directory.GetFiles(Path.Combine(root,"stored"),"*.part"));
        } finally {Directory.Delete(root,true);}
    }
    [Fact]
    public void RelaunchingCivRestartsTheUploadGrace()
    {
        bool playing=true;long clock=0;
        var policy=new UploadPolicy{AfterPlaying=true,ExitGrace=TimeSpan.FromSeconds(30),IsPlaying=()=>playing,Clock=()=>clock};
        Assert.False(policy.CanSend());playing=false;clock=29000;Assert.False(policy.CanSend());
        playing=true;Assert.False(policy.CanSend());playing=false;clock=58000;Assert.False(policy.CanSend());clock=87000;Assert.False(policy.CanSend());clock=88000;Assert.True(policy.CanSend());
    }
    [Fact]
    public async Task ManualAndLibraryRequestsAreAlsoBlockedWhilePlaying()
    {
        var root=Path.Combine(Path.GetTempPath(),"held-network-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var receiver=new FakeSite();using var stream=new Streamer(new RecorderSettings{OutputFolder=root,Stream="local"},receiver){CanContactServer=()=>false};
            await Assert.ThrowsAsync<HttpRequestException>(()=>stream.GetReplayLibraryAsync(default));Assert.Equal(0,receiver.Requests);
        } finally {Directory.Delete(root,true);}
    }
    [Fact]
    public void SafeCaptureCannotEnableMemoryReading()
    {
        var root=Path.Combine(Path.GetTempPath(),"no-memory-"+Guid.NewGuid().ToString("N"));
        using var session=new RecorderSession(new RecorderSettings{SafeCapture=true,TrackLogs=true,OutputFolder=root,SaveFolders=[]}){FindCiv=false};
        Assert.False(session.TrackLogs);session.TrackLogs=true;Assert.False(session.TrackLogs);
    }
}
