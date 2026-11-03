using System.Security.Cryptography;

namespace OutsideRecorderCore;

/// <summary>
/// File reads and writes that several parts of the program (and Civ) do at the same time without tripping over each other:
/// the recorder writes games.json and the save copies, the stream writes stream.json, the window reads both every few
/// seconds. On Windows, replacing a file another program has open fails ("Access to the path is denied" / "being used by
/// another process") — these helpers share generously and retry briefly instead of failing the whole step.
/// </summary>
public static class SafeFiles
{
    /// <summary>Reads a small text file while others may write, replace or delete it.</summary>
    public static string ReadAllText(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(f).ReadToEnd();
    }

    /// <summary>Reads a whole file (a save) while others may still have it open; throws IOException/UnauthorizedAccessException when it cannot now.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var b = new byte[f.Length];
        f.ReadExactly(b);
        return b;
    }

    /// <summary>Replaces <paramref name="path"/> with <paramref name="text"/> in one step (a reader sees the old or the new
    /// file, never half), retrying for about 5 s while a reader holds the old one.</summary>
    public static void Replace(string path, string text)
    {
        var tmp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(tmp, text);
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && i < 100) { Thread.Sleep(50); }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }
    }

    /// <summary>
    /// Copies a save into <paramref name="dir"/> for keeping and sending. Never overwrites: the same name with the same
    /// bytes is already there (returns it); the same name with other bytes (a reload wrote that turn again) gets
    /// " (HHmmss)" added. Written as a .part file and renamed, so the stream never reads half a copy. Returns null when
    /// the save cannot be read right now (Civ still writing it): try again later.
    /// </summary>
    public static string? CopySave(string source, string dir, DateTime writeTimeUtc)
    {
        byte[] bytes;
        try { bytes = ReadAllBytes(source); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (bytes.Length == 0) return null;
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(source); var ext = Path.GetExtension(source);
        var target = Path.Combine(dir, name + ext);
        for (int n = 0; File.Exists(target); n++)
        {
            try { if (SHA256.HashData(ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bytes))) return target; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            target = Path.Combine(dir, $"{name} ({DateTime.Now:HHmmss}){(n > 0 ? $"-{n}" : "")}{ext}");
        }
        var part = target + ".part";
        File.WriteAllBytes(part, bytes);
        File.SetLastWriteTimeUtc(part, writeTimeUtc);
        File.Move(part, target);
        return target;
    }

    /// <summary>Copies with a bounded buffer and shared reads; a changing source is discarded and retried later.</summary>
    public static string? CopyStableSave(AutosaveSeen save, string dir)
    {
        string? part=null;
        try
        {
            var before=new FileInfo(save.Path);
            if (save.Size <= 0 || !before.Exists || before.Length!=save.Size || before.LastWriteTimeUtc!=save.WriteTimeUtc) return null;
            Directory.CreateDirectory(dir);
            var target=Path.Combine(dir,Path.GetFileName(save.Path));
            if (File.Exists(target)) target=Path.Combine(dir,Path.GetFileNameWithoutExtension(save.Path)+" ("+Guid.NewGuid().ToString("N")[..8]+")"+Path.GetExtension(save.Path));
            part=target+".part";
            using (var input=new FileStream(save.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.SequentialScan))
            using (var output=new FileStream(part,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.SequentialScan))
            {
                // Read only the observed size: a source that keeps growing must not extend this job indefinitely.
                var buffer = new byte[65536];
                long remaining = save.Size;
                while (remaining > 0)
                {
                    int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) return null;
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
            var after=new FileInfo(save.Path);
            if(!after.Exists || after.Length!=save.Size || after.LastWriteTimeUtc!=save.WriteTimeUtc || new FileInfo(part).Length!=save.Size) return null;
            File.SetLastWriteTimeUtc(part,save.WriteTimeUtc);
            File.Move(part,target); part=null;
            return target;
        }
        catch(IOException) {return null;}
        catch(UnauthorizedAccessException) {return null;}
        finally {if(part is not null)try{File.Delete(part);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
}
