using System.Diagnostics;

namespace OutsideRecorderTests;

/// <summary>Settings, autosave watching on a temp folder, and the read-only process tools on a real 32-bit process.</summary>
public sealed class SystemTests
{
    [Fact]
    public void SettingsFileIsReadAndBadValuesIgnored()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "sample_ms = 250 # comment\nnotifications = off\nsave_folder = C:\\x\nsave_folder = C:\\y\nbogus = 1\nsnapshot_each_turn=maybe\n");
            var s = RecorderSettings.Load(f);
            Assert.Equal(250, s.SampleMs);
            Assert.False(s.Notifications);
            Assert.Equal(["C:\\x", "C:\\y"], s.SaveFolders);
            Assert.False(s.SnapshotEachTurn);
            File.WriteAllText(f, "sample_ms = 1");
            Assert.Equal(50, RecorderSettings.Load(f).SampleMs);
            File.WriteAllText(f, RecorderSettings.DefaultFileText());
            Assert.Equal(50, RecorderSettings.Load(f).SampleMs);   // 20 looks a second (the user, 2026-10-27)
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void AutosaveReportedOnceWhenComplete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "old.Civ5Save"), "old");
            var w = new AutosaveWatcher([dir, Path.Combine(dir, "missing")]);
            Assert.Empty(w.Poll());                                         // existing save ignored
            var path = Path.Combine(dir, "AutoSave_0010 AD-2000.Civ5Save");
            File.WriteAllBytes(path, new byte[1000]);
            Assert.Empty(w.Poll());                                         // still being written?
            var seen = Assert.Single(w.Poll());                             // unchanged for one poll: done
            Assert.Equal(1000, seen.Size);
            Assert.Empty(w.Poll());
            File.WriteAllBytes(path, new byte[2000]);                       // rewritten (same name, next turn)
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            w.Poll();
            Assert.Single(w.Poll());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadsModulesAndPeHeaderOfA32BitProcessReadOnly()
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "cmd.exe");
        if (!File.Exists(cmd)) return; // no 32-bit Windows layer on this PC
        using var p = Process.Start(new ProcessStartInfo(cmd, "/c ping -n 6 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            List<ModuleInfo> mods = [];
            for (int i = 0; i < 50 && !mods.Any(m => m.Name.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)); i++)
            {
                Thread.Sleep(50);
                try { mods = GameFinder.Modules(p.Id); } catch { }
            }
            var k32 = mods.Single(m => m.Name.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("SysWOW64", k32.Path, StringComparison.OrdinalIgnoreCase);
            var file = File.ReadAllBytes(k32.Path);
            int pe = BitConverter.ToInt32(file, 0x3C);
            var layout = KnownLayouts.Lekmod354 with { PeTimeDateStamp = BitConverter.ToUInt32(file, pe + 8), PeSizeOfImage = BitConverter.ToUInt32(file, pe + 24 + 56) };
            using var mem = new ProcessMemory(p.Id);
            Assert.True(mem.IsAlive);
            Assert.True(GameFinder.PeHeaderMatches(mem, k32.Base, layout));
            Assert.False(GameFinder.PeHeaderMatches(mem, k32.Base, KnownLayouts.Lekmod354));
        }
        finally { try { p.Kill(); } catch { } }
    }
}

/// <summary>The whole recorder loop without Civ: it waits for Civ and still notices autosaves.</summary>
public sealed class SessionTests
{
    [Fact]
    public void SessionWithoutCivNoticesAutosave()
    {
        var root = Path.Combine(Path.GetTempPath(), "outside-recorder-session-" + Guid.NewGuid().ToString("N"));
        var saves = Path.Combine(root, "auto");
        Directory.CreateDirectory(saves);
        try
        {
            using var s = new RecorderSession(new RecorderSettings { OutputFolder = Path.Combine(root, "rec"), SaveFolders = [saves], SampleMs = 100 }) { FindCiv = false };   // never read a Civ that happens to run
            s.Start();
            Thread.Sleep(500);
            File.WriteAllBytes(Path.Combine(saves, "AutoSave_0001 BC-4000.Civ5Save"), new byte[5000]);
            var sw = Stopwatch.StartNew();
            while (s.Status.LastAutosave is null && sw.ElapsedMilliseconds < 8000) Thread.Sleep(100);
            Assert.True(s.Status.LastAutosave is not null, s.Status.ToString());
            Assert.Contains("AutoSave_0001", s.Status.LastAutosave);
            // (the state line depends on whether Civ happens to run on this PC, so it is not checked here)
        }
        finally { Directory.Delete(root, true); }
    }
}
