using System.Text;
using System.Text.Json;

namespace OutsideRecorderTests;

public sealed class UnsupportedBackupTests
{
    static byte[] Save(string? guid = null)
    {
        var bytes = new byte[256]; Encoding.ASCII.GetBytes("CIV5").CopyTo(bytes, 0);
        if (guid is not null) new Guid(guid).ToByteArray().CopyTo(bytes, 40);
        return bytes;
    }

    [Fact]
    public void UnknownBuildKeepsOriginalSaveBytesAndDiagnosticEvidenceWithoutJoiningTheUploadQueue()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "AutoSave_0012 BC-3000.Civ5Save"); var bytes = Save(); File.WriteAllBytes(source, bytes);
        string folder;
        try
        {
            using (var backup = new UnsupportedBackup(root, 123, new string('a', 64), "DLL fingerprint not recognized"))
            {
                folder = backup.Folder;
                var seen = new AutosaveSeen(source, bytes.Length, File.GetLastWriteTimeUtc(source));
                Assert.True(backup.Capture(seen)); Assert.True(backup.Capture(seen)); Assert.Equal(1, backup.Saves);
                Assert.Equal(bytes, File.ReadAllBytes(Directory.GetFiles(Path.Combine(folder, "saves")).Single()));
                Assert.Equal(bytes, File.ReadAllBytes(source));
                Assert.Empty(new GameIndex(root).Load());
            }
            var log = File.ReadAllText(Path.Combine(folder, "diagnostics.jsonl"));
            Assert.Contains("DLL fingerprint not recognized", log); Assert.Contains(new string('a', 64), log);
            Assert.Contains("autosave_backup", log); Assert.DoesNotContain("\"kind\":\"result\"", log); Assert.DoesNotContain("\"kind\":\"snapshot\"", log);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "backup.json")));
            Assert.False(metadata.RootElement.GetProperty("automaticUpload").GetBoolean());
            Assert.False(metadata.RootElement.GetProperty("realtimeEvents").GetBoolean());
            var offered = OldGameScanner.Group(OldGameScanner.Find([], [folder]));
            Assert.Single(offered); Assert.Single(offered[0].Saves);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void KekAutosavesAreExcludedFromUnsupportedBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-unknown-kek-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "AutoSave_0012.Civ5Save"); var bytes = Save("c0cb8757-afe6-459f-8164-32b5e0578a7c"); File.WriteAllBytes(source, bytes);
        try
        {
            using var backup = new UnsupportedBackup(root, 123, new string('b', 64), "Unknown DLL");
            Assert.False(backup.Capture(new AutosaveSeen(source, bytes.Length, File.GetLastWriteTimeUtc(source))));
            Assert.True(backup.KekExcluded); Assert.Equal(0, backup.Saves);
            Assert.False(Directory.Exists(Path.Combine(backup.Folder, "saves")));
            Assert.Equal(bytes, File.ReadAllBytes(source));
        }
        finally { Directory.Delete(root, true); }
    }
}
