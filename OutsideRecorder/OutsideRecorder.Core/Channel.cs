using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>
/// The player's own live CHANNEL (card OR03 "Lobby / channel"): one id per install (channel.json next to the recordings),
/// because a game has no identity (seeds) until it is launched. The channel's events.jsonl (folder channel-&lt;id&gt;) gets
/// "lobby" / "lobby_changed" / "lobby_closed" lines from the staging room and a "game_started" line that links the channel
/// to the game's identity when it launches. It is streamed like a recording (games.json entry "channel:&lt;id&gt;", recording
/// id = the channel id), so the site can show the lobby before the game and the game after.
/// </summary>
public sealed class Channel : IDisposable
{
    readonly JsonlRecording rec;
    LobbySnapshot? last;
    string? lastJson;

    /// <summary>32 hex digits, kept for this install.</summary>
    public string Id { get; }
    /// <summary>channel-&lt;id&gt; under the output folder.</summary>
    public string Folder { get; }
    /// <summary>The staging room open now (null when none).</summary>
    public LobbySnapshot? Lobby => last;

    /// <summary>Opens (or makes) the channel of the install whose recordings are in <paramref name="outputFolder"/>.</summary>
    public Channel(string outputFolder, Func<long> nowUs)
    {
        Id = IdOf(outputFolder);
        Folder = Path.Combine(outputFolder, "channel-" + Id);
        var index = new GameIndex(outputFolder);
        if (index.Load().All(e => e.Identity != "channel:" + Id))
            index.Save(new GameEntry { Identity = "channel:" + Id, Folder = Folder, Context = Id, LastSeen = DateTimeOffset.UtcNow });
        rec = new JsonlRecording(Folder, "events.jsonl", nowUs);
    }

    /// <summary>The install's channel id (made once, kept in channel.json).</summary>
    public static string IdOf(string outputFolder)
    {
        var f = Path.Combine(outputFolder, "channel.json");
        try
        {
            if (File.Exists(f) && JsonSerializer.Deserialize<Dictionary<string, string>>(SafeFiles.ReadAllText(f)) is { } d
                && d.TryGetValue("id", out var id) && System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{32}$")) return id;
        }
        catch { }
        var made = GameIndex.NewContext();
        Directory.CreateDirectory(outputFolder);
        SafeFiles.Replace(f, JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = made }));
        return made;
    }

    /// <summary>
    /// A look at the pre-game: writes "lobby" when a staging room appears, "lobby_changed" when something in it changed,
    /// "lobby_closed" when it went away without a game. Returns true when a staging room was new (for a pop-up).
    /// </summary>
    public bool See(LobbySnapshot? l, string? mod, long timeMs)
    {
        if (l is not { IsStagingRoom: true })
        {
            if (last is not null && l is { GameStarted: false }) { rec.Write("lobby_closed", null, new Dictionary<string, object?> { ["channel"] = Id }); rec.Flush(); }
            // a started game keeps the lobby until game_started is written. The last line's text goes with the lobby: an
            // unreadable look (null) that kept it made the same lobby seen again count as nothing new, so Lobby stayed
            // empty, its closing was never written and its next change was written as a new "lobby"
            if (l is null || !l.GameStarted) { last = null; lastJson = null; }
            return false;
        }
        var data = Data(l, mod);
        var json = JsonSerializer.Serialize(data);
        if (json == lastJson) return false;
        bool isNew = last is null;
        var line = new Dictionary<string, object?>(data);
        if (!isNew) line["changes"] = Changes(last!, l);
        rec.Write(isNew ? "lobby" : "lobby_changed", null, line);
        rec.Flush();
        last = l; lastJson = json;
        return isNew;
    }

    /// <summary>The game launched (or this PC joined it): links the channel to the game's identity and recording.</summary>
    public void GameStarted(string identity, string recordingId, int turn, string? mod, TurnTimerState? timer, bool rejoined)
    {
        rec.Write("game_started", turn, new Dictionary<string, object?>
        {
            ["channel"] = Id, ["identity"] = identity, ["recording"] = recordingId, ["mod"] = mod, ["turn"] = turn,
            ["rejoined"] = rejoined, ["turnTimerSeconds"] = timer?.TurnSeconds,
        });
        rec.Flush();
        last = null; lastJson = null;
    }

    /// <summary>The lobby line's fields (OR03 "Lobby / channel").</summary>
    public Dictionary<string, object?> Data(LobbySnapshot l, string? mod) => new()
    {
        ["channel"] = Id, ["mod"] = mod, ["gameName"] = l.GameName, ["mapScript"] = l.MapScript, ["worldSize"] = l.WorldSize,
        ["gameSpeed"] = l.GameSpeed, ["era"] = l.Era, ["activePlayer"] = l.ActivePlayer, ["host"] = null,
        ["turnTimer"] = l.TurnTimer?.Data(),
        ["turnTimerSeconds"] = l.TurnTimer?.TurnSeconds,
        ["slots"] = l.Slots.Select(s => new Dictionary<string, object?>
        {
            ["player"] = s.Id, ["status"] = s.Status, ["statusName"] = Differ.SlotName(s.Status),
            ["nickname"] = s.Nickname, ["name"] = s.Name, ["steamId"] = s.SteamId, ["inUse"] = s.InUse, ["civ"] = s.Civ, ["civKey"] = s.CivKey, ["random"] = s.Civ == -1, ["leader"] = s.Leader,
            ["team"] = s.Team, ["handicap"] = s.Handicap, ["you"] = s.Id == l.ActivePlayer,
        }).ToList(),
        ["options"] = l.Options,
    };

    /// <summary>What changed between two looks: who joined or left, who picked another civ or team, which settings changed.</summary>
    public static List<string> Changes(LobbySnapshot a, LobbySnapshot b)
    {
        var r = new List<string>();
        var before = a.Slots.ToDictionary(s => s.Id);
        foreach (var s in b.Slots)
        {
            before.TryGetValue(s.Id, out var o);
            if (o is null || o.Status != s.Status) r.Add(s.Status == 3 ? $"joined:{s.Id}" : o?.Status == 3 ? $"left:{s.Id}" : $"slot:{s.Id}");
            else if (o.Civ != s.Civ || o.Leader != s.Leader) r.Add($"civ:{s.Id}");
            if (o is not null && o.Team != s.Team) r.Add($"team:{s.Id}");
            if (o is not null && o.Nickname != s.Nickname && s.Status == 3 && o.Status == 3) r.Add($"name:{s.Id}");
        }
        foreach (var o in a.Slots.Where(o => b.Slots.All(s => s.Id != o.Id))) r.Add(o.Status == 3 ? $"left:{o.Id}" : $"slot:{o.Id}");
        if (a.MapScript != b.MapScript) r.Add("mapScript");
        if (a.WorldSize != b.WorldSize) r.Add("worldSize");
        if (a.GameSpeed != b.GameSpeed) r.Add("gameSpeed");
        if (a.Era != b.Era) r.Add("era");
        if (a.TurnTimer != b.TurnTimer) r.Add("turnTimer");
        if (!a.Options.OrderBy(x => x.Key).SequenceEqual(b.Options.OrderBy(x => x.Key))) r.Add("options");
        return r;
    }

    /// <inheritdoc/>
    public void Dispose() => rec.Dispose();
}
