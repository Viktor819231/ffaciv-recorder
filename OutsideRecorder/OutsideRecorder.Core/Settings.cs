namespace OutsideRecorderCore;

/// <summary>
/// settings.txt next to the exe: "key = value" lines, '#' starts a comment. Missing file or key = the default.
/// </summary>
public sealed record RecorderSettings
{
    /// <summary>Milliseconds between samples (clamped 50..5000; 50 = 20 looks a second).</summary>
    public int SampleMs { get; init; } = 50;
    /// <summary>Conservative desktop release: settled save copies only; defer all server work until Civ exits.</summary>
    public bool SafeCapture { get; init; }
    /// <summary>Opt-in lightweight memory/livestream experiment, separate from the save-only desktop release.</summary>
    public bool MemoryTest { get; init; }

    /// <summary>Desktop profiles preserve safe defaults; the memory experiment starts at four samples per second.</summary>
    public RecorderSettings ForDesktop(bool memoryTest) => this with
    {
        MemoryTest = memoryTest, SafeCapture = !memoryTest, TrackLogs = memoryTest,
        SampleMs = 250, UploadTiming = memoryTest ? "during" : "after",
        Tape = false, SaveUpload = "full", SnapshotEachTurn = memoryTest, KeepCopies = true,
    };

    /// <summary>Supported UI rates; invalid or older values fall back to four samples per second.</summary>
    public static int NormalizeLooksPerSecond(int value) => value is 1 or 2 or 4 or 5 or 10 or 20 ? value : 4;
    /// <summary>Combined upload request-body budget in KiB/s; zero disables pacing.</summary>
    public int UploadKilobytesPerSecond { get; init; } = 256;
    /// <summary>Optional OS ceiling as a percentage of total machine CPU; zero leaves the adaptive budget in charge.</summary>
    public int CpuLimitPercent { get; init; }
    /// <summary>Where recordings go.</summary>
    public string OutputFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFACiv", "OutsideRecorder");
    /// <summary>Folders watched for new autosaves.</summary>
    public IReadOnlyList<string> SaveFolders { get; init; } = DefaultSaveFolders();
    /// <summary>Show the pop-up notices.</summary>
    public bool Notifications { get; init; } = true;
    /// <summary>Write a full snapshot (every unit and city) at each new turn.</summary>
    public bool SnapshotEachTurn { get; init; } = true;
    /// <summary>Tape mode: keep the raw bytes of every read (changed parts only, compressed) so the game can be re-read later.</summary>
    public bool Tape { get; init; } = true;
    /// <summary>"off" (default), "local" (a test server on this PC) or "site" (ffaciv.com).</summary>
    public string Stream { get; init; } = "off";
    /// <summary>Where the stream goes; empty = the default for <see cref="Stream"/>.</summary>
    public string StreamUrl { get; init; } = "";
    /// <summary>The user shell waits for server-side retention/privacy support before uploading any recording.</summary>
    public bool RequireReplayLifecycle { get; init; }
    /// <summary>The server must support these recorder controls before this shell sends recordings.</summary>
    public int RequiredControlsVersion { get; init; } = 1;
    /// <summary>Seconds between stream sends (1..60).</summary>
    public int StreamIntervalS { get; init; } = 2;
    /// <summary>"full" (every save whole) or "delta" (the change from the previous save; whole every <see cref="FullSaveEvery"/>th).</summary>
    public string SaveUpload { get; init; } = "delta";
    /// <summary>A whole save at least every this many saves (the rest as deltas).</summary>
    public int FullSaveEvery { get; init; } = 10;
    /// <summary>Maximum saves per recording in one sender pass; private streams use one.</summary>
    public int SaveBatchLimit { get; init; } = int.MaxValue;
    /// <summary>Maximum event chunks in one sender pass; private streams use four.</summary>
    public int EventChunkLimit { get; init; } = int.MaxValue;

    /// <summary>The live site that shows a streamed game (its replay folder); empty = from the stream: the local test
    /// site for "local", the site the uploader address belongs to for "site" (https://ffaciv.com/replay/).</summary>
    public string LiveSiteUrl { get; init; } = "";
    /// <summary>Keep the save copies and recordings on this PC after the server confirmed them (off in the user build).</summary>
    public bool KeepCopies { get; init; } = true;
    /// <summary>Read game memory and write realtime logs; off captures save files only.</summary>
    public bool TrackLogs { get; init; } = true;
    /// <summary>Upload and server processing during play, or after Civ closes.</summary>
    public string UploadTiming { get; init; } = "during";

    /// <summary>The live site's address in force (see <see cref="LiveSiteUrl"/>).</summary>
    public string LiveSiteAddress => LiveSiteUrl.Length > 0 ? LiveSiteUrl
        : Stream == "site" ? (StreamAddress.EndsWith("api/uploader/", StringComparison.OrdinalIgnoreCase) ? StreamAddress[..^"api/uploader/".Length] : "https://ffaciv.com/replay/")
        : "http://127.0.0.1:5632/replay/";

    /// <summary>The stream's address for the chosen mode.</summary>
    public string StreamAddress => StreamUrl.Length > 0 ? StreamUrl
        : Stream == "site" ? "https://ffaciv.com/replay/api/uploader/" : "http://127.0.0.1:5631/replay/api/uploader/";

    /// <summary>Civ's autosave folders (multiplayer, modded multiplayer, single player, modded single player).</summary>
    public static IReadOnlyList<string> DefaultSaveFolders()
    {
        var civ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sid Meier's Civilization 5");
        return [
            Path.Combine(civ, "Saves", "multi", "auto"),
            Path.Combine(civ, "ModdedSaves", "multi", "auto"),
            Path.Combine(civ, "Saves", "single", "auto"),
            Path.Combine(civ, "ModdedSaves", "single", "auto"),
        ];
    }

    /// <summary>Reads settings.txt; unknown keys and bad values are ignored (the default stays).</summary>
    public static RecorderSettings Load(string path)
    {
        var s = new RecorderSettings();
        if (!File.Exists(path)) return s;
        var folders = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var val = Environment.ExpandEnvironmentVariables(line[(eq + 1)..].Trim());
            switch (key)
            {
                case "sample_ms" when int.TryParse(val, out var ms): s = s with { SampleMs = Math.Clamp(ms, 50, 5000) }; break;
                case "upload_kib_per_second" when int.TryParse(val, out var kb): s = s with { UploadKilobytesPerSecond = Math.Clamp(kb, 0, 16384) }; break;
                case "cpu_limit_percent" when int.TryParse(val, out var cpu): s = s with { CpuLimitPercent = Math.Clamp(cpu, 0, 100) }; break;
                case "output_folder" when val.Length > 0: s = s with { OutputFolder = val }; break;
                case "save_folder" when val.Length > 0: folders.Add(val); break;
                case "notifications": s = s with { Notifications = IsOn(val) }; break;
                case "snapshot_each_turn": s = s with { SnapshotEachTurn = IsOn(val) }; break;
                case "tape": s = s with { Tape = IsOn(val) }; break;
                case "stream" when val.ToLowerInvariant() is "off" or "local" or "site": s = s with { Stream = val.ToLowerInvariant() }; break;
                case "stream_url": s = s with { StreamUrl = val }; break;
                case "stream_interval_s" when int.TryParse(val, out var si): s = s with { StreamIntervalS = Math.Clamp(si, 1, 60) }; break;
                case "save_upload" when val.ToLowerInvariant() is "full" or "delta": s = s with { SaveUpload = val.ToLowerInvariant() }; break;
                case "live_site_url": s = s with { LiveSiteUrl = val }; break;
                case "keep_copies": s = s with { KeepCopies = IsOn(val) }; break;
                case "track_logs": s = s with { TrackLogs = IsOn(val) }; break;
                case "upload_timing" when val.ToLowerInvariant() is "during" or "after": s = s with { UploadTiming = val.ToLowerInvariant() }; break;
                case "full_save_every" when int.TryParse(val, out var fe): s = s with { FullSaveEvery = Math.Clamp(fe, 1, 100) }; break;
            }
        }
        return folders.Count > 0 ? s with { SaveFolders = folders } : s;
    }

    static bool IsOn(string v) => v.ToLowerInvariant() is "1" or "on" or "yes" or "true";

    /// <summary>The settings.txt written on first start, with every key explained.</summary>
    /// <summary>The settings.txt for a flavour: "site" (the user build: ffaciv.com, tape off, copies deleted once sent),
    /// "local" (our tests: the test server on this PC, tape on, copies kept) or "off".</summary>
    public static string DefaultFileText(string stream = "off") => DefaultText.Replace("stream = off", "stream = " + stream)
        .Replace("tape = on", stream == "site" ? "tape = off" : "tape = on")
        .Replace("keep_copies = on", stream == "site" ? "keep_copies = off" : "keep_copies = on");

    const string DefaultText = """
        # FFACiv Outside Recorder settings. Lines starting with # are notes.
        # How often to look at the game, in milliseconds (50 to 5000; 50 = 20 times a second). The window's setting wins.
        sample_ms = 50
        # Combined upload body budget in KiB/s (0 = no bandwidth limit).
        upload_kib_per_second = 256
        # Optional Windows hard CPU ceiling, percent of the whole machine (0 = adaptive control only).
        cpu_limit_percent = 0
        # Where recordings are written. %LOCALAPPDATA% works.
        output_folder = %LOCALAPPDATA%\FFACiv\OutsideRecorder
        # Pop-up notices (on/off).
        notifications = on
        # Write every unit and city at the start of each turn (on/off).
        snapshot_each_turn = on
        # Tape: also keep the raw memory bytes that were read (changed parts only, compressed), for checking later (on/off).
        tape = on
        # Where recordings and saves go: site (ffaciv.com, or the address below), local (a test server on this PC) or off.
        stream = off
        # Leave empty for the default (site: https://ffaciv.com/replay/api/uploader/; local: http://127.0.0.1:5631/replay/api/uploader/).
        # A test environment: its own uploader address here.
        stream_url =
        # Seconds between sends.
        stream_interval_s = 2
        # Saves: full (every save whole) or delta (only the change from the previous save; a whole one every 10th).
        save_upload = delta
        full_save_every = 10
        # The live site that shows a streamed game (the "Get livestream link" button gives <this>live/<game>/watch).
        # Leave empty: the site of the address above (local: http://127.0.0.1:5632/replay/).
        live_site_url =
        # Keep a copy of each save and recording on this PC after the server confirmed it (on/off).
        keep_copies = on
        # Off: copy autosaves only, without opening Civ's memory or writing realtime logs.
        track_logs = on
        # during: upload/process while playing; after: keep locally until Civ closes.
        upload_timing = during
        # Autosave folders to watch. Leave out to use Civ's normal ones; one line per folder:
        # save_folder = %USERPROFILE%\Documents\My Games\Sid Meier's Civilization 5\ModdedSaves\multi\auto
        """;
}
