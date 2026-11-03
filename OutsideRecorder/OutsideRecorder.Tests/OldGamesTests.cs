using System.Net;
using System.Text.Json;

namespace OutsideRecorderTests;

/// <summary>Uploading old games (OR03 "Bulk upload"): finding them, grouping saves into games, sending whole games under a batch.</summary>
public sealed class OldGamesTests
{
    static readonly string SnowveSaves = TestData.Saves(@"outside-recorder 2026-10-27 snowve sp stream\session-20261027T152244-19836\saves");

    static OldSave S(string name, DateTime t, string dir = @"C:\x\Saves\single\auto") => new() { Path = Path.Combine(dir, name), Name = name, TimeUtc = t, Size = 1 };

    [Fact]
    public void SavesAreGroupedIntoGamesByTimeAndTurns()
    {
        var t0 = new DateTime(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc);
        var saves = new List<OldSave>();
        for (int i = 0; i <= 20; i++) saves.Add(S($"AutoSave_{i:0000} BC.Civ5Save", t0.AddMinutes(2 * i)));
        saves.Add(S("AutoSave_0015 BC (reload).Civ5Save", t0.AddMinutes(45)));            // a reload: same game
        for (int i = 0; i <= 8; i++) saves.Add(S($"AutoSave_{i:0000} AD.Civ5Save", t0.AddMinutes(75 + 2 * i)));   // a new game the same evening
        for (int i = 30; i <= 33; i++) saves.Add(S($"AutoSave_{i:0000} X.Civ5Save", t0.AddDays(2).AddMinutes(i))); // two days later
        saves.Add(S("AutoSave_0003 M.Civ5Save", t0.AddMinutes(5), @"C:\x\Saves\multi\auto"));                      // another folder
        var games = OldGameScanner.Group(saves);
        Assert.Equal(4, games.Count);
        Assert.Equal(33, games[0].LastTurn);                                                 // newest first
        Assert.Equal(22, games.Single(g => g.FirstTurn == 0 && g.LastTurn == 20).Saves.Count);
        Assert.Contains(games, g => g.LastTurn == 8 && g.Saves.Count == 9);
        Assert.Contains(games, g => g.Folder.EndsWith("multi\\auto"));
        Assert.Equal(games.Select(g => g.Id), OldGameScanner.Group(saves).Select(g => g.Id));  // the same ids on the next scan
    }

    [Fact]
    public void TheHeaderSaysMapSizeAndSpeed()
    {
        var head = System.Text.Encoding.Latin1.GetBytes("CIV5\0\0\01.0.3.279\0GAMESPEED_QUICK\0WORLDSIZE_SMALL\0Assets\\Maps\\Lekmap v6.3\\LekmapPangaeaFractalv6.3.lua\0LEKMOD v35.4.003\0");
        byte[] nqmod = Guid.Parse("13c6ca53-50ff-4a66-941b-5d5daa90c684").ToByteArray();   // NQMod's game-core DLL, as LEKMOD keeps it
        var l = OldGameScanner.LabelOf([.. head, .. nqmod, 0x78, 0x9C, (byte)'W', (byte)'O']);
        Assert.Equal(new SaveLabel("Lekmod 35.4.003", "Small", "Quick", "LekmapPangaeaFractalv6.3", "lekmod", "35.4.003"), l);
        Assert.Null(OldGameScanner.LabelOf([.. head, 0x78, 0x9C]).Mod);                     // no known DLL: not sure (the window says "Civ V with mods")
        Assert.Equal(new SaveLabel(null, null, null, null), OldGameScanner.LabelOf([1, 2, 3, 4, 5]));
    }

    [Fact]
    public void GamesOfACoreThisProgramDoesNotUploadAreNotListed()
    {
        var save = S("AutoSave_0001 BC.Civ5Save", DateTime.UtcNow);
        OldGame G(string? key) => new() { Id = "x", Folder = "f", Saves = [save], Label = new(null, null, null, null, key) };
        Assert.True(OldGameScanner.NotOffered(G("kek")));
        Assert.False(OldGameScanner.NotOffered(G("lekmod")));
        Assert.False(OldGameScanner.NotOffered(G(null)));
    }

    /// <summary>The same answer as the site's sniff (lib/uploader.js) on real saves of each mod.</summary>
    [Theory]
    [InlineData(@"single turns\Game_9255_KEK_2.2_Turn_214.Civ5Save", "kek")]
    [InlineData(@"lekmod\Lekgame one\AutoSave_Post_0001 BC-3940.Civ5Save", "lekmod")]
    // the site's sniff takes this Lekmod game (no LEKMOD tag in its first 64 KB) for Snowve too: the same answer, as asked
    [InlineData(@"outside-recorder 2026-10-27 lekmod mp\friend\OutsideRecorder\session-20261027T145722-10080\saves\AutoSave_Initial_0000 BC-4000.Civ5Save", "snowve")]
    [InlineData(@"outside-recorder 2026-10-27 snowve sp stream\session-20261027T151615-25200\saves\AutoSave_0000 BC-4000.Civ5Save", "snowve")]
    [InlineData(@"outside-recorder 2026-10-27 vanilla mp\saves\AutoSave_0001 BC-3940.Civ5Save", "vanilla")]
    public void TheModIsSniffedLikeTheSite(string file, string mod)
    {
        var path = TestData.Saves(file);
        if (!File.Exists(path)) return;
        var i = new FileInfo(path);
        Assert.Equal(mod, OldGameScanner.LabelOf(new OldSave { Path = path, Name = i.Name, TimeUtc = i.LastWriteTimeUtc, Size = i.Length }).ModKey);
    }

    [Fact]
    public void SavesInAZipAreFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var z = System.IO.Compression.ZipFile.Open(Path.Combine(dir, "old.zip"), System.IO.Compression.ZipArchiveMode.Create))
                for (int i = 0; i < 3; i++) using (var w = new StreamWriter(z.CreateEntry($"game/AutoSave_000{i} BC.Civ5Save").Open())) w.Write("CIV5 " + i);
            var found = OldGameScanner.Find([], [dir]);
            Assert.Equal(3, found.Count);
            Assert.Equal("CIV5 2", System.Text.Encoding.ASCII.GetString(found.Single(s => s.Turn == 2).Read()));
            Assert.Single(OldGameScanner.Group(found));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The site's routes for a bulk upload: POST saves (whole or delta, X-Batch), POST batches/&lt;id&gt;/process.</summary>
    sealed class BatchSite : HttpMessageHandler
    {
        public readonly List<(string Name, string? Batch, string? Kind, bool Delta)> Saves = [];
        public readonly List<string> Processed = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            HttpResponseMessage Json(HttpStatusCode c, object b) => new(c) { Content = new StringContent(JsonSerializer.Serialize(b)) };
            var path = r.RequestUri!.AbsolutePath;
            string? H(string k) => r.Headers.TryGetValues(k, out var v) ? v.Single() : null;
            if (path.EndsWith("/register")) return Json(HttpStatusCode.OK, new { id = "u_1", key = "u_1.k" });
            if (path.EndsWith("/process")) { Processed.Add(Uri.UnescapeDataString(path.Split('/')[^2])); return Json(HttpStatusCode.Accepted, new { saves = Saves.Count }); }
            if (path.EndsWith("/saves"))
            {
                bool delta = H("X-Save-Encoding") is not null;
                await r.Content!.ReadAsByteArrayAsync(ct);
                Saves.Add((Uri.UnescapeDataString(H("X-File-Name")!), H("X-Batch"), H("X-Upload-Kind"), delta));
                return Json(HttpStatusCode.Accepted, new { id = $"20261027120000-{Saves.Count:x8}" });
            }
            return Json(HttpStatusCode.NotFound, new { });
        }
    }

    [Fact]
    public async Task AWholeGameGoesUpUnderOneBatchAndGoesOnAfterAStop()
    {
        if (!Directory.Exists(SnowveSaves)) return;
        var root = Path.Combine(Path.GetTempPath(), "outside-recorder-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = Directory.GetFiles(SnowveSaves, "AutoSave_00*.Civ5Save").Where(f => !f.Contains('(')).Order().Take(5).ToList();
            var saves = files.Select((f, i) => new OldSave { Path = f, Name = Path.GetFileName(f), TimeUtc = new DateTime(2026, 10, 27, 15, i, 0, DateTimeKind.Utc), Size = new FileInfo(f).Length }).ToList();
            var game = OldGameScanner.Group(saves).Single();
            var site = new BatchSite();
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/" };
            using var streamer = new Streamer(settings, site);
            var up = new OldGameUploader(settings, streamer);
            var known = new HashSet<string> { OldGameScanner.Md5(File.ReadAllBytes(files[1])) };   // already recorded on this PC
            using (var stop = new CancellationTokenSource())
            {
                int seen = 0;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => up.UploadAsync(game, known, (d, _) => { if (++seen == 3) stop.Cancel(); }, stop.Token));
            }
            Assert.Equal(2, site.Saves.Count);                 // save 0 whole, save 1 skipped, save 2 as a delta: then stopped
            Assert.Empty(site.Processed);
            var (sent, skipped) = await up.UploadAsync(game, known, null, default);
            Assert.Equal(2, sent); Assert.Equal(3, skipped);   // goes on: saves 3 and 4
            Assert.Equal(4, site.Saves.Count);
            Assert.All(site.Saves, s => { Assert.Equal($"old-{game.Id}", s.Batch); Assert.Equal("old-game", s.Kind); });
            Assert.False(site.Saves[0].Delta);
            Assert.Contains(site.Saves, s => s.Delta);
            Assert.Equal([$"old-{game.Id}"], site.Processed);
            var again = await up.UploadAsync(game, known, null, default);   // nothing new: nothing sent, no batch
            Assert.Equal((0, 5), again);
            Assert.Single(site.Processed);
        }
        finally { Directory.Delete(root, true); }
    }
}
