using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>Preserves autosaves and diagnostics without reading an unrecognized game's memory or uploading it.</summary>
public sealed class UnsupportedBackup : IDisposable
{
    readonly JsonlRecording log;
    readonly string? hash;
    readonly string reason;
    readonly DateTimeOffset detected = DateTimeOffset.UtcNow;
    readonly HashSet<string> copies = new(StringComparer.OrdinalIgnoreCase);
    public string Folder { get; }
    public int Saves => copies.Count;
    public bool KekExcluded { get; private set; }
    public DateTimeOffset? LastSavedAt { get; private set; }
    public string? CoreHash => hash;

    public UnsupportedBackup(string outputFolder, int processId, string? coreHash, string reason)
    {
        hash = coreHash; this.reason = reason;
        Folder = Path.Combine(outputFolder, $"unsupported-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{processId}-{Guid.NewGuid():N}");
        log = new JsonlRecording(Folder, "diagnostics.jsonl", () => (DateTimeOffset.UtcNow - detected).Ticks / 10);
        log.Write("unsupported_build", null, new { coreSha256 = coreHash, reason, processId, memoryRead = false, uploaded = false });
        SaveMetadata();
    }

    /// <summary>Copies the stable file exactly as written; null means retry later, false means KEK was excluded.</summary>
    public bool? Capture(AutosaveSeen save)
    {
        var label = OldGameScanner.LabelOf(new OldSave { Path = save.Path, Name = Path.GetFileName(save.Path), Size = save.Size, TimeUtc = save.WriteTimeUtc });
        if (label.ModKey == "kek")
        {
            KekExcluded = true;
            log.Write("backup_excluded", null, new { reason = "KEK Mod games are supplied by Oblast", file = Path.GetFileName(save.Path) });
            log.Flush(); return false;
        }
        KekExcluded = false;
        var copy = SafeFiles.CopySave(save.Path, Path.Combine(Folder, "saves"), save.WriteTimeUtc);
        if (copy is null) return null;
        if (copies.Add(copy))
        {
            LastSavedAt = DateTimeOffset.UtcNow;
            log.Write("autosave_backup", null, new { file = Path.GetFileName(copy), originalTime = save.WriteTimeUtc, bytes = new FileInfo(copy).Length,
                filenameTurn = RecorderSession.AutosaveTurn(save.Path), mod = label.Mod, observedCoreSha256 = hash, association = "unverified", memoryRead = false });
            SaveMetadata();
        }
        return true;
    }

    void SaveMetadata()
    {
        log.Flush();
        SafeFiles.Replace(Path.Combine(Folder, "backup.json"), JsonSerializer.Serialize(new
        {
            kind = "unsupported-backup", coreSha256 = hash, reason, detectedAt = detected, lastSavedAt = LastSavedAt,
            saves = Saves, realtimeEvents = false, automaticUpload = false,
        }));
    }
    public void Dispose()
    {
        log.Write("backup_closed", null, new { saves = Saves });
        SaveMetadata(); log.Dispose();
    }
}
