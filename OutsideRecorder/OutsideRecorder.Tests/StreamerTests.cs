using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OutsideRecorderTests;

/// <summary>A stand-in for the site's uploader routes, with the same rules for chunks (one offset, one content).</summary>
sealed class FakeSite : HttpMessageHandler
{
    public readonly SortedDictionary<long, byte[]> Chunks = new();
    public readonly List<(string Name, bool Delta, int Bytes)> Saves = new();
    public bool Down;
    public bool RefuseDeltas;
    public int Requests;
    public Action? SaveReceived;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        if (Down) throw new HttpRequestException("server down");
        var path = request.RequestUri!.AbsolutePath;
        HttpResponseMessage Json(HttpStatusCode code, object body) => new(code) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        if (path.EndsWith("/register")) return Json(HttpStatusCode.OK, new { id = "u_0123456789abcdef", key = "u_0123456789abcdef.k" });
        var m = Regex.Match(path, @"/recordings/([0-9a-f]{32})/chunks/(\d+)$");
        if (m.Success)
        {
            var wire = await request.Content!.ReadAsByteArrayAsync(ct);
            using var gz = new GZipStream(new MemoryStream(wire), CompressionMode.Decompress);
            using var plain = new MemoryStream(); gz.CopyTo(plain);
            var bytes = plain.ToArray();
            long offset = long.Parse(m.Groups[2].Value);
            if (Chunks.TryGetValue(offset, out var old))
                return old.AsSpan().SequenceEqual(bytes) ? Json(HttpStatusCode.OK, new { duplicate = true }) : Json(HttpStatusCode.Conflict, new { error = "different bytes" });
            Chunks[offset] = bytes;
            return Json(HttpStatusCode.Accepted, new { stored = true });
        }
        if (path.EndsWith("/saves"))
        {
            bool delta = request.Headers.Contains("X-Save-Encoding");
            if (delta && RefuseDeltas) return Json(HttpStatusCode.Conflict, new { error = "base unknown", needFull = true });
            var name = Uri.UnescapeDataString(request.Headers.GetValues("X-File-Name").Single());
            Saves.Add((name, delta, (int)(request.Content!.Headers.ContentLength ?? 0)));
            SaveReceived?.Invoke();
            return Json(HttpStatusCode.Accepted, new { id = $"20261027120000-{Saves.Count:x8}" });
        }
        return Json(HttpStatusCode.NotFound, new { error = "no" });
    }

    public byte[] Joined() { using var o = new MemoryStream(); long at = 0; foreach (var (k, v) in Chunks) { if (k != at) break; o.Write(v); at += v.Length; } return o.ToArray(); }
}

public sealed class StreamerTests
{
    [Fact]
    public async Task FinalResyncAfterGameExitDrainsWithoutReuploadingFortyAcknowledgedCopies()
    {
        var (root,game)=Setup();
        try {
            var dir=Directory.CreateDirectory(Path.Combine(game.Folder,"saves")).FullName;
            var receipts=new Dictionary<string,string>();
            for(int i=0;i<40;i++) {var name=$"AutoSave_{i:D4}.Civ5Save";File.WriteAllBytes(Path.Combine(dir,name),new byte[128]);receipts[name]="server-"+i;}
            var resync="AutoSave_Resync.Civ5Save";File.WriteAllBytes(Path.Combine(dir,resync),new byte[128]);
            File.WriteAllText(Path.Combine(game.Folder,"stream.json"),JsonSerializer.Serialize(new StreamState{SentSaves=receipts}));
            bool idle=false;var site=new FakeSite();
            using var stream=new Streamer(new RecorderSettings{OutputFolder=root,Stream="local",KeepCopies=true,SaveUpload="full",SaveBatchLimit=4},site) {
                CanUploadGame=_=>OutsideRecorderShell.StreamingSetupPolicy.RecordingAllowed(true,false,false,false,idle,true)
            };
            await stream.SavesPassAsync(default);Assert.Empty(site.Saves);
            idle=true;await stream.SavesPassAsync(default);
            Assert.Equal(resync,Assert.Single(site.Saves).Name);
            Assert.Equal(41,JsonSerializer.Deserialize<StreamState>(File.ReadAllText(Path.Combine(game.Folder,"stream.json")))!.SentSaves.Count);
        } finally {Directory.Delete(root,true);}
    }

    [Fact]
    public async Task IdleBatchReturnsToOneSaveWhenGameplayResumesDuringThePass()
    {
        var (root,game)=Setup();
        try {
            var dir=Directory.CreateDirectory(Path.Combine(game.Folder,"saves")).FullName;
            for(int i=0;i<10;i++)File.WriteAllBytes(Path.Combine(dir,$"AutoSave_{i:D4}.Civ5Save"),new byte[128]);
            bool idle=true;var site=new FakeSite {SaveReceived=()=>idle=false};
            using var stream=new Streamer(new RecorderSettings{OutputFolder=root,Stream="local",KeepCopies=true,SaveUpload="full",SaveBatchLimit=1},site) {SavePassLimit=()=>idle?4:1};
            await stream.SavesPassAsync(default);
            Assert.Single(site.Saves);
        } finally {Directory.Delete(root,true);}
    }
    static (string Root, GameEntry Game) Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "outside-recorder-stream-" + Guid.NewGuid().ToString("N"));
        var game = new GameEntry { Identity = "1a2b3c4d-5e6f7a8b", Folder = Path.Combine(root, "s1"), Context = GameIndex.NewContext(), LastSeen = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(game.Folder);
        new GameIndex(root).Save(game);
        return (root, game);
    }

    static void Lines(string folder, int from, int count)
    {
        var sb = new StringBuilder();
        for (int i = from; i < from + count; i++) sb.Append($"{{\"schema\":2,\"kind\":\"result\",\"sequence\":{i},\"pad\":\"{new string('x', 200)}\"}}\r\n");
        File.AppendAllText(Path.Combine(folder, "events.jsonl"), sb.ToString());
    }

    [Fact]
    public async Task StartAtCurrentSaveSkipsBacklogKeepsReceiptsAndContinuesWithNewSaves()
    {
        var (root, game) = Setup();
        try {
            var folder = Directory.CreateDirectory(Path.Combine(game.Folder, "saves")).FullName;
            var files = Enumerable.Range(0,100).Select(i=>Path.Combine(folder,$"AutoSave_{i:D4}.Civ5Save")).ToList();
            foreach (var f in files) File.WriteAllBytes(f,new byte[128]);
            var initial = new StreamState {SentSaves=new() {[Path.GetFileName(files[0])]="already-on-server"},BaseFile=Path.GetFileName(files[0]),BaseId="already-on-server",SentOffset=123};
            File.WriteAllText(Path.Combine(game.Folder,"stream.json"),JsonSerializer.Serialize(initial));
            var window=new LiveSaveWindow(game.Context,files);
            var site=new FakeSite();
            using var stream=new Streamer(new RecorderSettings {OutputFolder=root,Stream="local",SaveUpload="delta",SaveBatchLimit=1,KeepCopies=true,UploadKilobytesPerSecond=0},site) {
                CanUploadSave=(g,f)=>window.Includes(g.Context,f), ForceFullSave=(g,f)=>window.NeedsFullBaseline(g.Context,f)
            };
            // An excluded copy cannot even be read; it must never be used as the delta base.
            using(var locked=new FileStream(files[0],FileMode.Open,FileAccess.Read,FileShare.None)) await stream.SavesPassAsync(default);
            Assert.Equal("AutoSave_0099.Civ5Save",Assert.Single(site.Saves).Name);
            Assert.False(site.Saves[0].Delta);
            await stream.SavesPassAsync(default);
            Assert.Single(site.Saves);
            var next=Path.Combine(folder,"AutoSave_0100.Civ5Save"); File.WriteAllBytes(next,new byte[128]);
            await stream.SavesPassAsync(default);
            Assert.Equal(2,site.Saves.Count);
            Assert.Equal("AutoSave_0100.Civ5Save",site.Saves[1].Name);
            var state=JsonSerializer.Deserialize<StreamState>(File.ReadAllText(Path.Combine(game.Folder,"stream.json")))!;
            Assert.Equal(123,state.SentOffset);
            Assert.Equal("already-on-server",state.SentSaves[Path.GetFileName(files[0])]);
            Assert.Equal(3,state.SentSaves.Count);
            Assert.Equal(101,Directory.GetFiles(folder).Length);
            Assert.True(window.Includes("other-recording",files[1]));
            Assert.Throws<InvalidOperationException>(()=>new LiveSaveWindow(game.Context,[]));
        } finally { Directory.Delete(root,true); }
    }

    [Fact]
    public async Task CurrentSaveAlreadyAcknowledgedIsReusedWithoutUploadingAgain()
    {
        var (root,game)=Setup();
        try {
            var folder=Directory.CreateDirectory(Path.Combine(game.Folder,"saves")).FullName;
            var file=Path.Combine(folder,"AutoSave_0048.Civ5Save");File.WriteAllBytes(file,new byte[128]);
            File.WriteAllText(Path.Combine(game.Folder,"stream.json"),JsonSerializer.Serialize(new StreamState {SentSaves=new() {[Path.GetFileName(file)]="server-save"},BaseFile=Path.GetFileName(file),BaseId="server-save"}));
            var window=new LiveSaveWindow(game.Context,[file]);var site=new FakeSite();
            using var stream=new Streamer(new RecorderSettings {OutputFolder=root,Stream="local",KeepCopies=true},site) {CanUploadSave=(g,f)=>window.Includes(g.Context,f),ForceFullSave=(g,f)=>window.NeedsFullBaseline(g.Context,f)};
            await stream.SavesPassAsync(default);
            Assert.Empty(site.Saves);
        } finally {Directory.Delete(root,true);}
    }

    [Fact]
    public async Task PrivateStreamLimitsOneSaveAndFourChunksPerPassWithoutLosingProgress()
    {
        var (root, game) = Setup();
        try {
            var saves = Directory.CreateDirectory(Path.Combine(game.Folder, "saves")).FullName;
            for (int i = 0; i < 100; i++) File.WriteAllBytes(Path.Combine(saves, $"AutoSave_{i:D4}.Civ5Save"), new byte[128]);
            Lines(game.Folder, 1, 6000);
            var site = new FakeSite();
            using var stream = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local", SaveUpload = "full",
                KeepCopies = true, SaveBatchLimit = 1, EventChunkLimit = 4, UploadKilobytesPerSecond = 0 }, site);
            await stream.SavesPassAsync(default);
            Assert.Single(site.Saves);
            await stream.EventsPassAsync(default);
            Assert.Equal(4, site.Chunks.Count);
            Assert.True(site.Joined().Length < new FileInfo(Path.Combine(game.Folder, "events.jsonl")).Length);
            await stream.SavesPassAsync(default);
            Assert.Equal(2, site.Saves.Count);
            await stream.EventsPassAsync(default);
            Assert.Equal(File.ReadAllBytes(Path.Combine(game.Folder, "events.jsonl")), site.Joined());
        } finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EveryByteArrivesOnceThroughOutagesAndRestarts()
    {
        var (root, game) = Setup();
        try
        {
            var site = new FakeSite();
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/", SaveUpload = "full" };
            Lines(game.Folder, 1, 3000);                                     // ~650 KB: three chunks
            using (var s = new Streamer(settings, site)) await s.PassAsync(default);
            Lines(game.Folder, 3001, 500);
            site.Down = true;                                                // outage
            using (var s = new Streamer(settings, site)) await Assert.ThrowsAsync<HttpRequestException>(() => s.PassAsync(default));
            site.Down = false;
            Lines(game.Folder, 3501, 500);
            using (var s = new Streamer(settings, site)) await s.PassAsync(default); // a new program run: continues from stream.json
            Assert.Equal(File.ReadAllBytes(Path.Combine(game.Folder, "events.jsonl")), site.Joined());
            Assert.All(site.Chunks.Values, c => Assert.True(c.Length <= Streamer.ChunkBytes && c[^1] == (byte)'\n'));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AChunkCutShortByACrashIsSentAgainTheSame()
    {
        var (root, game) = Setup();
        try
        {
            var site = new FakeSite();
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/" };
            Lines(game.Folder, 1, 100);
            // the program sent this chunk, the server stored it, then the program died before writing stream.json
            var first = File.ReadAllBytes(Path.Combine(game.Folder, "events.jsonl"));
            site.Chunks[0] = first;
            File.WriteAllText(Path.Combine(game.Folder, "stream.json"), JsonSerializer.Serialize(new StreamState { SentOffset = 0, PendingLength = first.Length }));
            Lines(game.Folder, 101, 100);                                    // the file grew meanwhile
            using (var s = new Streamer(settings, site)) await s.PassAsync(default);
            Assert.Equal(File.ReadAllBytes(Path.Combine(game.Folder, "events.jsonl")), site.Joined()); // no 409 at offset 0
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeltaRefusedMeansWholeSave()
    {
        var dir = TestData.Saves(@"outside-recorder 2026-10-27 vanilla mp\saves");
        if (!Directory.Exists(dir)) return;
        var (root, game) = Setup();
        try
        {
            var saves = Path.Combine(game.Folder, "saves"); Directory.CreateDirectory(saves);
            var files = Directory.GetFiles(dir, "AutoSave_00*.Civ5Save").Order().Skip(10).Take(3).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                var copy = Path.Combine(saves, Path.GetFileName(files[i])); File.Copy(files[i], copy);
                File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(-10 + i));
            }
            var site = new FakeSite();
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/", SaveUpload = "delta" };
            using (var s = new Streamer(settings, site)) await s.PassAsync(default);
            Assert.Equal([false, true, true], site.Saves.Select(x => x.Delta));       // whole, then deltas
            Assert.True(site.Saves[1].Bytes < new FileInfo(files[1]).Length / 4);
            site.RefuseDeltas = true;
            var next = Directory.GetFiles(dir, "AutoSave_00*.Civ5Save").Order().Skip(13).First();
            File.Copy(next, Path.Combine(saves, Path.GetFileName(next)));
            File.SetLastWriteTimeUtc(Path.Combine(saves, Path.GetFileName(next)), DateTime.UtcNow.AddMinutes(-1));
            using (var s = new Streamer(settings, site)) await s.PassAsync(default);
            Assert.False(site.Saves[^1].Delta);                                        // the server asked for the whole save
            Assert.Equal(4, site.Saves.Count);
        }
        finally { Directory.Delete(root, true); }
    }
}
