namespace OutsideRecorderCore;

/// <summary>Preserves autosaves without opening the game process, parsing saves or writing realtime logs.</summary>
public sealed class SaveOnlyCapture
{
    readonly string root;
    readonly GameIndex index;
    GameEntry? entry;
    bool ended;
    /// <summary>The current local capture, whose identity is deliberately not claimed to be a game's seeds.</summary>
    public GameEntry? Entry => entry;
    /// <summary>Capture under the recorder's output folder.</summary>
    public SaveOnlyCapture(string root) { this.root = root; index = new GameIndex(root); }

    /// <summary>Copies one stable file. The server groups actual games by their save contents later.</summary>
    public string? Copy(AutosaveSeen save) => Copy(save, out _);

    /// <summary>Copies one stable file; null with <paramref name="excluded"/> false = try again later, with it true = never
    /// (a KEK Mod save: Oblast supplies those).</summary>
    public string? Copy(AutosaveSeen save, out bool excluded)
    {
        excluded = false;
        // The recorder deliberately excludes KEK; this header sniff does not parse game state.
        try
        {
        using (var source = new FileStream(save.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var head = new byte[(int)Math.Min(source.Length, OldGameScanner.SniffBytes)];
            source.ReadExactly(head);
            if (OldGameScanner.SniffMod(head).Mod == "kek") { excluded = true; return null; }
        }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        var context = entry?.Context ?? GameIndex.NewContext();
        var folder = entry?.Folder ?? Path.Combine(root, RecorderSession.SessionFolderName("files-" + context[..8]));
        var copied = SafeFiles.CopyStableSave(save, Path.Combine(folder, "saves"));
        if (copied is null) return null;
        entry = new GameEntry { Identity = "files:" + context, Context = context, Folder = folder, LastSeen = DateTimeOffset.UtcNow };
        index.Save(entry);
        File.Delete(Path.Combine(folder, "capture-ended"));
        ended = false;
        return copied;
    }

    /// <summary>New capture after a process closes or recording mode changes; existing copies stay queued.</summary>
    public void Close() { End(); entry = null; ended = false; }
    /// <summary>A small local completion marker, with no process or game log data. Written once per capture: the loop asks
    /// every second while Civ is closed.</summary>
    public void End()
    {
        if (ended || entry is null || !Directory.Exists(entry.Folder)) return;
        File.WriteAllText(Path.Combine(entry.Folder, "capture-ended"), "done");
        ended = true;
    }
}
