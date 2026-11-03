using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OutsideRecorderCore;

/// <summary>One save found on this PC: a file, or an entry of a zip file.</summary>
public sealed record OldSave
{
    /// <summary>The file, or the zip file the save is in.</summary>
    public required string Path { get; init; }
    /// <summary>The entry inside <see cref="Path"/> when it is a zip, else null.</summary>
    public string? ZipEntry { get; init; }
    /// <summary>The save's own name ("AutoSave_0013 BC-3220.Civ5Save").</summary>
    public required string Name { get; init; }
    /// <summary>When it was written (UTC).</summary>
    public required DateTime TimeUtc { get; init; }
    /// <summary>Bytes.</summary>
    public required long Size { get; init; }
    /// <summary>The turn in the autosave's name, or null (a save the player named).</summary>
    public int? Turn => RecorderSession.AutosaveTurn(Name);
    /// <summary>A key that names the save on this PC (path, or zip path + "!" + entry).</summary>
    public string Key => ZipEntry is null ? Path : Path + "!" + ZipEntry;

    /// <summary>The save's bytes.</summary>
    public byte[] Read()
    {
        if (ZipEntry is null) return SafeFiles.ReadAllBytes(Path);
        using var zip = ZipFile.OpenRead(Path);
        using var s = zip.GetEntry(ZipEntry)!.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}

/// <summary>
/// What the start of a save says (no parsing): the mod by the site's own byte patterns (<see cref="OldGameScanner.SniffMod"/>,
/// "kek", "lekmod", "snowve", "vanilla", "nqmod" or null), its version when the save names it, and the header's strings.
/// </summary>
public sealed record SaveLabel(string? Mod, string? MapSize, string? Speed, string? MapScript, string? ModKey = null, string? ModVersion = null);

/// <summary>
/// A game found among old saves: saves of one folder (or zip) written close together in time. Grouping without reading
/// the saves (the game's seeds are deep in the pre-game data, read by the server's parser): a new game starts after a
/// break of <see cref="GapHours"/> h or when the turns start again from 0/1 after a later turn. Two games played back to
/// back may end up in one group; the server sorts the saves into games by their seeds anyway.
/// </summary>
public sealed record OldGame
{
    /// <summary>12 hex digits, from the folder and the first save (stable between scans while the first save is there).</summary>
    public required string Id { get; init; }
    /// <summary>The folder or zip the saves are in.</summary>
    public required string Folder { get; init; }
    /// <summary>The saves, oldest first.</summary>
    public required IReadOnlyList<OldSave> Saves { get; init; }
    /// <summary>What the newest save's header says.</summary>
    public SaveLabel Label { get; init; } = new(null, null, null, null);
    /// <summary>First and last save time (UTC).</summary>
    public DateTime From => Saves[0].TimeUtc;
    /// <summary>Last save time (UTC).</summary>
    public DateTime To => Saves[^1].TimeUtc;
    /// <summary>The lowest and highest autosave turn, or null.</summary>
    public int? FirstTurn => Saves.Select(s => s.Turn).Where(t => t is not null).Min();
    /// <summary>The highest autosave turn, or null.</summary>
    public int? LastTurn => Saves.Select(s => s.Turn).Where(t => t is not null).Max();
    /// <summary>Saves in a modded folder (ModdedSaves).</summary>
    public bool Modded => Folder.Contains("ModdedSaves", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Finds old games among the saves on this PC (Civ's save folders, any folder the player adds, zip files in it).</summary>
public static class OldGameScanner
{
    /// <summary>Hours without a save that start a new game.</summary>
    public const double GapHours = 6;

    /// <summary>Civ's save folders: autosaves (multi, single, hot seat) of the game with and without mods.</summary>
    public static IReadOnlyList<string> CivSaveFolders()
    {
        var civ = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sid Meier's Civilization 5");
        return [.. from root in new[] { "Saves", "ModdedSaves" } from sub in new[] { @"multi\auto", @"single\auto", @"hotseat\auto", "multi", "single", "hotseat" } select System.IO.Path.Combine(civ, root, sub)];
    }

    /// <summary>Every save in <paramref name="folders"/> (not below them, except a player-added folder: two levels and zips).</summary>
    public static List<OldSave> Find(IEnumerable<string> folders, IEnumerable<string>? deepFolders = null, Func<bool>? canContinue = null)
    {
        CheckWork(canContinue);
        var list = new List<OldSave>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string dir, SearchOption deep)
        {
            CheckWork(canContinue);
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.Civ5Save", new EnumerationOptions { RecurseSubdirectories = deep == SearchOption.AllDirectories, MaxRecursionDepth = 2, IgnoreInaccessible = true }))
                {
                    CheckWork(canContinue);
                    if (seen.Add(f)) { var i = new FileInfo(f); if (i.Length > 0) list.Add(new OldSave { Path = f, Name = i.Name, TimeUtc = i.LastWriteTimeUtc, Size = i.Length }); }
                }
                if (deep == SearchOption.AllDirectories)
                    foreach (var z in Directory.EnumerateFiles(dir, "*.zip", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }))
                    {
                        CheckWork(canContinue);
                        if (seen.Add(z)) list.AddRange(InZip(z, canContinue));
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var d in folders) Add(d, SearchOption.TopDirectoryOnly);
        foreach (var d in deepFolders ?? []) Add(d, SearchOption.AllDirectories);
        CheckWork(canContinue);
        return list;
    }

    static IEnumerable<OldSave> InZip(string zip, Func<bool>? canContinue)
    {
        CheckWork(canContinue);
        try
        {
            using var z = ZipFile.OpenRead(zip);
            var saves = new List<OldSave>();
            foreach (var e in z.Entries)
            {
                CheckWork(canContinue);
                if (e.Name.EndsWith(".Civ5Save", StringComparison.OrdinalIgnoreCase) && e.Length > 0)
                    saves.Add(new OldSave { Path = zip, ZipEntry = e.FullName, Name = e.Name, TimeUtc = e.LastWriteTime.UtcDateTime, Size = e.Length });
            }
            return saves;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The saves as games, newest first (see <see cref="OldGame"/>).</summary>
    public static List<OldGame> Group(IEnumerable<OldSave> saves, Func<bool>? canContinue = null)
    {
        CheckWork(canContinue);
        var games = new List<OldGame>();
        foreach (var folder in saves.GroupBy(s => s.ZipEntry is null ? System.IO.Path.GetDirectoryName(s.Path)! : s.Path + "!" + System.IO.Path.GetDirectoryName(s.ZipEntry), StringComparer.OrdinalIgnoreCase))
        {
            CheckWork(canContinue);
            var cur = new List<OldSave>();
            int maxTurn = -1;
            foreach (var s in folder.OrderBy(s => s.TimeUtc).ThenBy(s => s.Name))
            {
                CheckWork(canContinue);
                bool gap = cur.Count > 0 && (s.TimeUtc - cur[^1].TimeUtc).TotalHours > GapHours;
                bool restart = cur.Count > 0 && s.Turn is <= 1 && maxTurn > 5 && (s.TimeUtc - cur[^1].TimeUtc).TotalMinutes > 10;
                if (gap || restart) { games.Add(Make(folder.Key, cur, canContinue)); cur = []; maxTurn = -1; }
                cur.Add(s);
                if (s.Turn is int t) maxTurn = Math.Max(maxTurn, t);
            }
            if (cur.Count > 0) games.Add(Make(folder.Key, cur, canContinue));
        }
        CheckWork(canContinue);
        return [.. games.OrderByDescending(g => g.To)];
    }

    static void CheckWork(Func<bool>? canContinue)
    {
        if (canContinue?.Invoke() == false) throw new WorkDeferredException("Scanning waits until the game and computer are ready.");
    }

    static OldGame Make(string folder, List<OldSave> saves, Func<bool>? canContinue)
    {
        CheckWork(canContinue);
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{folder}|{saves[0].Name}|{saves[0].TimeUtc.Ticks}")))[..12];
        SaveLabel label = new(null, null, null, null);
        try { label = LabelOf(saves[^1]); } catch { }
        return new OldGame { Id = id, Folder = folder, Saves = saves, Label = label };
    }

    /// <summary>How much of a save's start the site's sniff looks at (lib/uploader.js SniffBytes).</summary>
    public const int SniffBytes = 64 * 1024;

    /// <summary>The header's plain strings of a save (its first 64 KB): mod, map size, speed, map script.</summary>
    public static SaveLabel LabelOf(OldSave save)
    {
        byte[] head;
        if (save.ZipEntry is null)
        {
            using var f = new FileStream(save.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            head = new byte[(int)Math.Min(SniffBytes, f.Length)];
            f.ReadExactly(head);
        }
        else
        {
            using var zip = ZipFile.OpenRead(save.Path);
            var entry = zip.GetEntry(save.ZipEntry) ?? throw new FileNotFoundException("The save is no longer in the archive.");
            using var stream = entry.Open();
            head = new byte[(int)Math.Min(SniffBytes, entry.Length)];
            stream.ReadExactly(head);
        }
        return LabelOf(head);
    }

    /// <summary>See <see cref="LabelOf(OldSave)"/>; only text that stands in the header as it is, nothing guessed.</summary>
    public static SaveLabel LabelOf(byte[] head)
    {
        if (head.Length < 4 || Encoding.ASCII.GetString(head, 0, 4) != "CIV5") return new(null, null, null, null);
        var all = Encoding.Latin1.GetString(head);
        int end = head.AsSpan().IndexOf([(byte)0x78, (byte)0x9C]);   // the packed game starts: the header strings are before it
        var text = end > 0 ? all[..end] : all;
        var (key, version) = SniffMod(head);
        string? mod = key switch
        {
            "lekmod" => ("Lekmod " + version).Trim(), "snowve" => ("Snowve " + version).Trim(), "vanilla" => "Civ V (no mods)", "nqmod" => "NQMod",
            _ => null,
        };
        string? Pretty(string? k) => k is null ? null : string.Join(' ', k.Split('_').Select(w => w.Length == 0 ? w : w[0] + w[1..].ToLowerInvariant()));
        var size = Regex.Match(text, @"WORLDSIZE_([A-Z]+)") is { Success: true } w ? Pretty(w.Groups[1].Value) : null;
        var speed = Regex.Match(text, @"GAMESPEED_([A-Z]+)") is { Success: true } g ? Pretty(g.Groups[1].Value) : null;
        var script = Regex.Match(text, @"Assets\\Maps\\(?:[^\\]+\\)?([^\\]+?)\.lua") is { Success: true } m ? m.Groups[1].Value : null;
        return new(mod, size, speed, script, key, version);
    }

    static readonly (string Mod, string Guid)[] DllGuids =
    [
        ("kek", "c0cb8757-afe6-459f-8164-32b5e0578a7c"), ("nqmod", "13c6ca53-50ff-4a66-941b-5d5daa90c684"), ("vanilla", "83f34ff0-3d60-43e7-aedb-82c79867ee3f"),
    ];
    static readonly string[] SnowveOptions = ["GAMEOPTION_STRAT_REVEAL", "GAMEOPTION_STRAT_COUNT", "GAMEOPTION_HIDE_ENEMY_SCORES", "GAMEOPTION_SCORE_REVEAL_TURN"];

    /// <summary>A GUID's 16 bytes as Windows writes them (first three fields little-endian), as a Latin-1 text to search for.</summary>
    static string GuidText(string guid) => Encoding.Latin1.GetString(Guid.Parse(guid).ToByteArray());

    /// <summary>
    /// The mod of a save from its first <see cref="SniffBytes"/> bytes, exactly as the site's uploader decides it
    /// (tools/site/pages/lib/uploader.js sniffMod: the game-core DLL GUID in the pre-game, then LEKMOD's lobby tag, a
    /// Lekmap script, Snowve's DLL stamp, a Snowve-only option or a Weevee script). (null, null) when no known DLL is there.
    /// </summary>
    public static (string? Mod, string? Version) SniffMod(byte[] head)
    {
        var text = Encoding.Latin1.GetString(head, 0, Math.Min(head.Length, SniffBytes));
        var dll = DllGuids.Select(g => (g.Mod, Text: GuidText(g.Guid))).FirstOrDefault(g => text.Contains(g.Text, StringComparison.Ordinal));
        if (dll.Mod is null) return (null, null);
        if (dll.Mod != "nqmod") return (dll.Mod, null);
        if (Regex.Match(text, @"LEKMOD\s*v(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase) is { Success: true } tag) return ("lekmod", tag.Groups[1].Value);
        if (Regex.IsMatch(text, "lekmap", RegexOptions.IgnoreCase)) return ("lekmod", null);
        int at = text.IndexOf(dll.Text, StringComparison.Ordinal) + dll.Text.Length;
        if (at + 12 <= text.Length && Regex.IsMatch(text.Substring(at, 12), @"^[\x05-\x08]\x00\x00\x00[78]\.\d+\.\d+")) return ("snowve", null);
        if (SnowveOptions.Any(o => text.Contains(o, StringComparison.Ordinal))) return ("snowve", null);
        if (Regex.IsMatch(text, "weevee", RegexOptions.IgnoreCase)) return ("snowve", null);
        return ("nqmod", null);
    }

    /// <summary>Game cores whose saves this program never uploads (they reach the site another way).</summary>
    static readonly HashSet<string> NotOfferedMods = ["kek"];

    /// <summary>True when the game is of a game core this program does not upload: it is not listed at all.</summary>
    public static bool NotOffered(OldGame g) => g.Label.ModKey is { } m && NotOfferedMods.Contains(m);

    /// <summary>MD5 of a save's bytes (the site finds the same save again by it).</summary>
    public static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
}
