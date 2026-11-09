using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OutsideRecorderTests;

/// <summary>The 2026-11-09 review of the recorder: one refused recording or one unreadable file must not stop everything
/// else, and nothing may be copied, written or asked for again and again without need.</summary>
public sealed class ReliabilityTests
{
    static string NewRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"recorder-{name}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static GameEntry AddGame(string root, string folder, string save)
    {
        var game = new GameEntry { Identity = "files:" + folder, Folder = Path.Combine(root, folder), Context = GameIndex.NewContext(), LastSeen = DateTimeOffset.UtcNow };
        var saves = Directory.CreateDirectory(Path.Combine(game.Folder, "saves")).FullName;
        File.WriteAllBytes(Path.Combine(saves, save), new byte[128]);
        new GameIndex(root).Save(game);
        return game;
    }

    static Streamer SiteStreamer(string root, FakeSite site) =>
        new(new RecorderSettings { OutputFolder = root, Stream = "local", KeepCopies = true, SaveUpload = "full", SaveBatchLimit = 4 }, site);

    [Fact]
    public async Task ARecordingTheSiteRefusesDoesNotHoldUpTheNextOne()
    {
        var root = NewRoot("refused");
        try
        {
            var refused = AddGame(root, "session-a", "AutoSave_A.Civ5Save");
            AddGame(root, "session-b", "AutoSave_B.Civ5Save");
            var site = new FakeSite
            {
                Answer = r => r.RequestUri!.AbsolutePath.EndsWith("/saves") && r.Headers.GetValues("X-Recording-Id").Single() == refused.Context
                    ? new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("{\"error\":\"This recording expired\"}") } : null,
            };
            using var stream = SiteStreamer(root, site);
            await stream.SavesPassAsync(default);   // used to throw at the first recording, every pass, so B never went
            Assert.Equal("AutoSave_B.Civ5Save", Assert.Single(site.Saves).Name);
            Assert.Equal(410, stream.RefusalOf(Path.Combine(refused.Folder, "saves", "AutoSave_A.Civ5Save"))!.Status);
            int asked = site.Requests;
            await stream.SavesPassAsync(default);   // the refused one rests: not asked again at once
            Assert.Equal(asked, site.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ADeletionTheSiteRefusesDoesNotStopTheSaves()
    {
        var root = NewRoot("deletion");
        try
        {
            var game = AddGame(root, "session-a", "AutoSave_A.Civ5Save");
            var site = new FakeSite
            {
                Answer = r => r.RequestUri!.AbsolutePath.EndsWith("/delete") ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":\"no\"}") } : null,
            };
            using var stream = SiteStreamer(root, site);
            var other = GameIndex.NewContext();
            stream.QueueReplayDeletion(other);
            await stream.SavesPassAsync(default);
            Assert.Equal("AutoSave_A.Civ5Save", Assert.Single(site.Saves).Name);
            Assert.True(stream.ReplayDeletionQueued(other));   // kept for a later try
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ADeletionOfSomethingTheSiteNoLongerHasIsDone()
    {
        var root = NewRoot("deletion-gone");
        try
        {
            AddGame(root, "session-a", "AutoSave_A.Civ5Save");
            var site = new FakeSite();   // answers 404 for the delete route
            using var stream = SiteStreamer(root, site);
            var other = GameIndex.NewContext();
            stream.QueueReplayDeletion(other);
            await stream.SavesPassAsync(default);
            Assert.False(stream.ReplayDeletionQueued(other));
            Assert.Single(site.Saves);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeleteHidesTheReplayAtOnceWhileUploadsWait()
    {
        var root = NewRoot("hide-now");
        try
        {
            var game = AddGame(root, "session-a", "AutoSave_A.Civ5Save");
            var hides = new List<string>();
            var site = new FakeSite
            {
                Answer = r => r.RequestUri!.AbsolutePath.EndsWith($"/recordings/{game.Context}/state") && r.Method == HttpMethod.Post
                    ? Record(hides, r) : null,
            };
            using var stream = SiteStreamer(root, site);
            stream.CanContactServer = () => false;   // in a game: uploads wait for Civ's main menu
            stream.QueueReplayDeletion(game.Context, hold: Streamer.DeletionHold);
            await stream.FlushReplayDeletionsAsync(default);
            Assert.Empty(hides);                      // the regular pass waits, as before
            await stream.HideHeldDeletionsNowAsync(default);
            Assert.Equal("{\"private\":true}", Assert.Single(hides));
            Assert.Empty(site.Saves);
            Assert.True(stream.ReplayDeletionQueued(game.Context));   // the withdrawal itself waits for the hold
        }
        finally { Directory.Delete(root, true); }
    }

    static HttpResponseMessage Record(List<string> into, HttpRequestMessage r)
    {
        into.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"recording\",\"private\":true}") };
    }

    [Fact]
    public async Task AStreamFileHeldByAnotherProgramDoesNotSendItsSavesAgain()
    {
        var root = NewRoot("stream-held");
        try
        {
            var game = AddGame(root, "session-a", "AutoSave_A.Civ5Save");
            var progress = Path.Combine(game.Folder, "stream.json");
            File.WriteAllText(progress, JsonSerializer.Serialize(new StreamState { SentSaves = new() { ["AutoSave_A.Civ5Save"] = "already-there" } }));
            var site = new FakeSite();
            using var stream = SiteStreamer(root, site);
            using (new FileStream(progress, FileMode.Open, FileAccess.Read, FileShare.None))
                await stream.SavesPassAsync(default);   // read as "nothing sent", it used to send the save again
            Assert.Empty(site.Saves);
            Assert.Equal("already-there", JsonSerializer.Deserialize<StreamState>(File.ReadAllText(progress))!.SentSaves["AutoSave_A.Civ5Save"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AnOldGameUploadAfterARestartDoesNotReadTheSavesItSentAgain()
    {
        var root = NewRoot("fingerprints");
        try
        {
            OldSave Save(int n)
            {
                var file = Path.Combine(root, $"AutoSave_{n:D4}.Civ5Save");
                File.WriteAllBytes(file, Enumerable.Range(0, 64).Select(b => (byte)(b + n)).ToArray());
                return new OldSave { Path = file, Name = Path.GetFileName(file), TimeUtc = File.GetLastWriteTimeUtc(file), Size = 64 };
            }
            var output = Path.Combine(root, "output");
            var settings = new RecorderSettings { OutputFolder = output, Stream = "local", SaveUpload = "full", UploadKilobytesPerSecond = 0 };
            var site = new FakeSite { Answer = r => r.RequestUri!.AbsolutePath.EndsWith("/process") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") } : null };
            using var stream = new Streamer(settings, site);
            var first = new[] { Save(1), Save(2) };
            Assert.Equal((2, 0), await new OldGameUploader(settings, stream, new SaveFingerprints(output)).UploadAsync(
                new OldGame { Id = "old", Folder = root, Saves = [.. first] }, new HashSet<string>(), null, default));
            var third = Save(3);
            // the next start: a new fingerprint list read from disk, and the two sent saves cannot even be opened now
            using var lock1 = new FileStream(first[0].Path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var lock2 = new FileStream(first[1].Path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal((1, 2), await new OldGameUploader(settings, stream, new SaveFingerprints(output)).UploadAsync(
                new OldGame { Id = "old", Folder = root, Saves = [.. first, third] }, new HashSet<string>(), null, default));
            Assert.Equal(3, site.Saves.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AFolderMissingForAMomentDoesNotBringItsOldSavesBack()
    {
        var root = NewRoot("watch");
        try
        {
            var auto = Directory.CreateDirectory(Path.Combine(root, "multi", "auto")).FullName;
            File.WriteAllBytes(Path.Combine(auto, "AutoSave_0001.Civ5Save"), new byte[64]);
            var watcher = new AutosaveWatcher([auto]);
            Assert.Empty(watcher.Poll());
            var away = auto + "-away";
            Directory.Move(auto, away);          // OneDrive offline, a drive waking up
            Assert.Empty(watcher.Poll());
            Directory.Move(away, auto);
            Assert.Empty(watcher.Poll());        // the old save is still known: not copied again as new
            Assert.Empty(watcher.Poll());
            File.WriteAllBytes(Path.Combine(auto, "AutoSave_0002.Civ5Save"), new byte[64]);
            Assert.Empty(watcher.Poll());        // a new one settles for one poll, then counts
            Assert.Equal("AutoSave_0002.Civ5Save", Path.GetFileName(Assert.Single(watcher.Poll()).Path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(@"C:\Users\Single\Documents\My Games\Sid Meier's Civilization 5\Saves\multi\auto\AutoSave_0001.Civ5Save", false)]
    [InlineData(@"D:\single\Civ\ModdedSaves\multi\auto\AutoSave_0001.Civ5Save", false)]
    [InlineData(@"C:\Users\me\Documents\My Games\Sid Meier's Civilization 5\Saves\single\auto\AutoSave_0001.Civ5Save", true)]
    [InlineData(@"C:\Users\me\Documents\My Games\Sid Meier's Civilization 5\ModdedSaves\single\AutoSave_0001.Civ5Save", true)]
    public void OnlyTheSavesOwnFoldersMarkASinglePlayerGame(string path, bool single) =>
        Assert.Equal(single, RecorderSession.InSinglePlayerFolder(path));

    [Fact]
    public void SessionFolderNamesReadBackOnEveryWindowsLanguage()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");   // Buddhist calendar: the year would be 2569
            var name = RecorderSession.SessionFolderName("1234");
            Assert.StartsWith($"session-{DateTime.Now.Year:D4}", name);
            Assert.Equal(DateTime.Now.Year, RecorderSession.StartedOf(name)!.Value.Year);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void TheEndMarkerIsWrittenOncePerCapture()
    {
        var root = NewRoot("ended");
        try
        {
            var source = Path.Combine(root, "AutoSave_0001.Civ5Save");
            File.WriteAllBytes(source, new byte[256]);
            var capture = new SaveOnlyCapture(Path.Combine(root, "recordings"));
            Assert.NotNull(capture.Copy(new AutosaveSeen(source, 256, File.GetLastWriteTimeUtc(source))));
            var marker = Path.Combine(capture.Entry!.Folder, "capture-ended");
            capture.End();
            Assert.True(File.Exists(marker));
            File.Delete(marker);
            capture.End();                       // the loop asks every second while Civ is closed
            Assert.False(File.Exists(marker));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AKekSaveIsLeftOutForGoodNotRetried()
    {
        var kek = TestData.Saves(Path.Combine("single turns", "Game_9255_KEK_2.2_Turn_214.Civ5Save"));
        if (!File.Exists(kek)) return;   // the developer's own save: not on other PCs
        var root = NewRoot("kek");
        try
        {
            var capture = new SaveOnlyCapture(Path.Combine(root, "recordings"));
            Assert.Null(capture.Copy(new AutosaveSeen(kek, new FileInfo(kek).Length, File.GetLastWriteTimeUtc(kek)), out var excluded));
            Assert.True(excluded);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ABusyComputerSlowsTheRecorderButNeverStopsItsSaveCopies()
    {
        var start = DateTimeOffset.UtcNow;
        ResourceSample Busy(int s) => new() { At = start.AddSeconds(s), TickMs = s * 1000L, SystemCpuPercent = 99, RecorderCpuPercent = .1,
            AvailableMemoryBytes = 200L << 20, TotalMemoryBytes = 16L << 30 };
        var workload = new WorkloadPolicy();
        for (int s = 0; s <= 10; s += 2) workload.Observe(Busy(s));
        Assert.False(workload.CanStartHeavyWork);   // uploads and parsing wait
        Assert.True(workload.CanReadMemory);        // reading Civ slows to one look a second instead of stopping
        Assert.Equal(1000, workload.MinimumSampleMs);
        Assert.True(workload.CanCopySaves);         // Civ's autosaves are kept before Civ deletes the oldest
        workload.SetQuiet(true, "Giving Civ time to start the turn");
        Assert.False(workload.CanCopySaves);        // only the pause after a new turn holds them
    }

    [Fact]
    public void ADamagedGamesListIsKeptBesideBeforeANewOneStarts()
    {
        var root = NewRoot("index");
        try
        {
            File.WriteAllText(Path.Combine(root, "games.json"), "[{\"Identity\":");
            var entry = new GameEntry { Identity = "files:x", Folder = Path.Combine(root, "session-x"), Context = GameIndex.NewContext() };
            new GameIndex(root).Save(entry);
            Assert.Single(new GameIndex(root).Load());
            Assert.Equal("[{\"Identity\":", File.ReadAllText(Assert.Single(Directory.GetFiles(root, "games.json.damaged-*"))));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SavesAreListedOldestFirstWithTheirFolderPath()
    {
        var root = NewRoot("saves-of");
        try
        {
            var saves = Directory.CreateDirectory(Path.Combine(root, "saves")).FullName;
            var newer = Path.Combine(saves, "AutoSave_0001.Civ5Save");
            var older = Path.Combine(saves, "AutoSave_0002.Civ5Save");
            File.WriteAllBytes(newer, [1]); File.WriteAllBytes(older, [2]);
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(-1));
            Assert.Equal([older, newer], Streamer.SavesOf(root));
            Assert.Empty(Streamer.SavesOf(Path.Combine(root, "missing")));
        }
        finally { Directory.Delete(root, true); }
    }
}
