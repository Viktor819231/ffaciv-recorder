namespace OutsideRecorderTests;

public sealed class LocalRecordingDeletionTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "recorder-local-delete-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LegacyUnindexedRecordingDeletesOnlyItsOwnCopy()
    {
        var output = Path.Combine(root, "recordings");
        var legacy = Path.Combine(output, "session-20261027T180000");
        var original = Path.Combine(root, "Civ saves", "AutoSave_0050.Civ5Save");
        Directory.CreateDirectory(Path.Combine(legacy, "saves"));
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "Civ original");
        File.WriteAllText(Path.Combine(legacy, "events.jsonl"), "older recorder log without an index entry");
        File.Copy(original, Path.Combine(legacy, "saves", Path.GetFileName(original)));
        Assert.Empty(new GameIndex(output).Load());

        Assert.True(LocalRecordingDeletion.Delete(output, legacy));
        Assert.False(Directory.Exists(legacy));
        Assert.Equal("Civ original", File.ReadAllText(original));
        Assert.False(Directory.Exists(Path.Combine(output, "replay-deletions")));
        Assert.False(LocalRecordingDeletion.Delete(output, legacy));
    }

    [Theory]
    [InlineData("../session-outside")]
    [InlineData("../recordings-other/session-outside")]
    [InlineData("saves")]
    [InlineData("nested/session-outside")]
    [InlineData(".")]
    public void TargetsOutsideDirectRecorderSessionFoldersAreRejected(string relative)
    {
        var output = Path.Combine(root, "recordings");
        var target = Path.GetFullPath(Path.Combine(output, relative));
        Directory.CreateDirectory(target);
        var keep = Path.Combine(target, "keep.txt");
        File.WriteAllText(keep, "do not remove");

        Assert.Throws<IOException>(() => LocalRecordingDeletion.Delete(output, target));
        Assert.Equal("do not remove", File.ReadAllText(keep));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
