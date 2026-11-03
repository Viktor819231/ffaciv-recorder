namespace OutsideRecorderCore;

/// <summary>Removes only this recorder's own direct session folders, including unindexed older captures.</summary>
public static class LocalRecordingDeletion
{
    /// <summary>Validates the full target and its tree before deleting a recorder copy. Civ's source saves are never targets.</summary>
    public static bool Delete(string outputFolder, string recordingFolder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputFolder));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingFolder));
        if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("session-", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only a recording inside the recorder's own output folder can be deleted.");
        if (!Directory.Exists(target)) return false;
        // Reject links before walking them; this also rejects an output root redirected into Civ's save folder.
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            RejectLink(ancestor);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(target));
        while (pending.TryPop(out var directory))
        {
            RejectLink(directory);
            foreach (var child in directory.EnumerateFileSystemInfos())
            {
                RejectLink(child);
                if (child is DirectoryInfo subdirectory) pending.Push(subdirectory);
            }
        }
        Directory.Delete(target, recursive: true);
        return true;
    }

    static void RejectLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("This recording contains a linked folder or file. Its files were kept for safety.");
    }
}
