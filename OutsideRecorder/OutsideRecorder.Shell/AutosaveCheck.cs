using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OutsideRecorderShell;

/// <summary>
/// Civ V's autosave setting in UserSettings.ini (from the 2026-10-19 uploader, card U05): the replay needs a save every
/// turn, and enough saves kept. The fix changes only the two numbers, keeps every other byte, makes a copy of the old
/// file first, and refuses while Civ runs (Civ writes the file itself when it closes).
/// </summary>
static class AutosaveCheck
{
    public const string TurnsKey = "TurnsBetweenAutosave", KeptKey = "NumAutosavesKept";
    public const int KeepAtLeast = 100;
    /// <summary>Latin-1 maps every byte to one char and back, so a read and write keeps the file byte for byte.</summary>
    static readonly Encoding Bytes = Encoding.Latin1;

    /// <summary>Documents\My Games\Sid Meier's Civilization 5\UserSettings.ini (Documents may be in OneDrive: asked from Windows).</summary>
    public static string IniPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sid Meier's Civilization 5", "UserSettings.ini");

    /// <summary>The two values (null = the line is missing), or null when there is no file (Civ never started).</summary>
    public static (int? Between, int? Kept)? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path, Bytes);
        return (Find(text, TurnsKey), Find(text, KeptKey));
    }

    static int? Find(string text, string key)
    {
        var m = Regex.Match(text, $@"^[ \t]*{key}[ \t]*=[ \t]*(-?\d+)", RegexOptions.Multiline);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary>True while any Civ V program runs.</summary>
    public static bool CivRunning() => Process.GetProcesses().Any(p =>
    {
        try { return p.ProcessName.StartsWith("CivilizationV", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
        finally { p.Dispose(); }
    });

    /// <summary>Sets every turn + at least <see cref="KeepAtLeast"/> kept. Returns the changed lines, or an error in plain words.</summary>
    public static (List<(string Key, string Was, string Now)> Changes, string? Backup, string? Error) Fix(string path)
    {
        if (CivRunning()) return ([], null, "civRunning");
        if (!File.Exists(path)) return ([], null, "Civ V has not made its settings file yet. Start Civ V once, close it, then try again.");
        var text = File.ReadAllText(path, Bytes);
        var changes = new List<(string, string, string)>();
        var between = Find(text, TurnsKey);
        var kept = Find(text, KeptKey);
        if (between != 1) { text = Set(text, TurnsKey, 1); changes.Add((TurnsKey, between?.ToString() ?? "(missing)", "1")); }
        if (kept is not int k || k < KeepAtLeast) { text = Set(text, KeptKey, KeepAtLeast); changes.Add((KeptKey, kept?.ToString() ?? "(missing)", KeepAtLeast.ToString())); }
        if (changes.Count == 0) return (changes, null, null);
        var backup = path + ".before-ffaciv";
        File.Copy(path, backup, overwrite: true);
        File.WriteAllText(path, text, Bytes);
        return (changes, backup, null);
    }

    /// <summary>Changes one number; a missing key goes under [AutoSave] (made when missing), in the file's own line ending.</summary>
    static string Set(string text, string key, int value)
    {
        var re = new Regex($@"^([ \t]*{key}[ \t]*=[ \t]*)(-?\d+)", RegexOptions.Multiline);
        if (re.IsMatch(text)) return re.Replace(text, m => m.Groups[1].Value + value, 1);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var section = Regex.Match(text, @"^\[AutoSave\][^\r\n]*\r?\n?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (section.Success) return text.Insert(section.Index + section.Length, $"{key} = {value}{nl}");
        return text + (text.Length == 0 || text.EndsWith('\n') ? "" : nl) + $"[AutoSave]{nl}{key} = {value}{nl}";
    }
}
