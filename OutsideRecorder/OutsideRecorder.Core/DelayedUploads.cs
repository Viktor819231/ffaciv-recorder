using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OutsideRecorderCore;

/// <summary>A local upload choice, independent of the website's replay visibility.</summary>
public sealed record UploadDecision
{
    /// <summary>The recording's 32-character local/server context.</summary>
    public required string Context { get; init; }
    /// <summary>recording, waiting (uploads now, goes public at <see cref="DueAt"/>), local, or upload.</summary>
    public string State { get; init; } = "recording";
    /// <summary>When capture was observed ending, independent of website processing.</summary>
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>When a hidden replay goes public, after the warning grace.</summary>
    public DateTimeOffset? DueAt { get; init; }
    /// <summary>When the final warning was handed to the shell.</summary>
    public DateTimeOffset? WarningAt { get; init; }
    /// <summary>The shell already asked whether this capture has finished.</summary>
    public bool EndPrompted { get; init; }
    /// <summary>A user-postponed countdown must survive rejoining the game.</summary>
    public bool Postponed { get; init; }
    /// <summary>"hidden": the recorder made the site copy private until <see cref="DueAt"/>; "kept": the site copy stays as
    /// it was (already there, or the player chose); null: not settled yet, so an automatic upload of a waiting game waits.</summary>
    public string? Hold { get; init; }
}

/// <summary>Durable two-hour holds: a finished game uploads at once, hidden on the site, and goes public two hours later
/// (the user, 2026-11-02: "wait 2 hours to become visible" instead of 2 hours to upload). Restarts preserve choices and
/// never skip the five-minute warning before it goes public.</summary>
public sealed class DelayedUploads(string root, Func<DateTimeOffset>? utcNow = null)
{
    readonly object gate = new();
    readonly Func<DateTimeOffset> now = utcNow ?? (() => DateTimeOffset.UtcNow);
    /// <summary>Newly ended captures stay hidden two hours unless the user chooses otherwise.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromHours(2);
    /// <summary>A final warning leaves five minutes to postpone or delete.</summary>
    public static readonly TimeSpan WarningDelay = TimeSpan.FromMinutes(5);
    string Folder => Path.Combine(root, "upload-decisions");
    string FileOf(string context)
    {
        if (!Regex.IsMatch(context, "^[a-f0-9]{32}$")) throw new ArgumentException("Invalid recording.");
        return Path.Combine(Folder, context + ".json");
    }

    /// <summary>Unknown or unreadable decisions stay held instead of implicitly granting upload permission.</summary>
    public UploadDecision Get(string context)
    {
        lock (gate)
        {
            var file = FileOf(context);
            try
            {
                var o = JsonNode.Parse(SafeFiles.ReadAllText(file)) as JsonObject ?? throw new System.Text.Json.JsonException("Invalid upload decision.");
                return new UploadDecision
                {
                    Context = context, State = o["state"]?.GetValue<string>() ?? "local",
                    EndedAt = Date(o["endedAt"]), DueAt = Date(o["dueAt"]), WarningAt = Date(o["warningAt"]),
                    EndPrompted = o["endPrompted"]?.GetValue<bool>() == true,
                    Postponed = o["postponed"]?.GetValue<bool>() ?? (o["state"]?.GetValue<string>() == "waiting" && o["endPrompted"]?.GetValue<bool>() == true),
                    Hold = o["hold"]?.GetValue<string>(),
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
            { return new UploadDecision { Context = context, State = File.Exists(file) ? "local" : "recording" }; }
        }
    }
    /// <summary>Undo of a held deletion: the upload choice the recording had before Delete (a new final warning comes first).</summary>
    public void Restore(UploadDecision choice) { lock (gate) Save(choice with { WarningAt = null }); }
    static DateTimeOffset? Date(JsonNode? value) => value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.GetValue<long>());
    void Save(UploadDecision choice)
    {
        Directory.CreateDirectory(Folder);
        SafeFiles.Replace(FileOf(choice.Context), new JsonObject
        {
            ["state"] = choice.State, ["endedAt"] = choice.EndedAt?.ToUnixTimeMilliseconds(),
            ["dueAt"] = choice.DueAt?.ToUnixTimeMilliseconds(), ["warningAt"] = choice.WarningAt?.ToUnixTimeMilliseconds(),
            ["endPrompted"] = choice.EndPrompted,
            ["postponed"] = choice.Postponed,
            ["hold"] = choice.Hold,
        }.ToJsonString());
    }

    /// <summary>Starts the countdown once after capture ends; polling never extends an existing decision.</summary>
    public UploadDecision Schedule(string context)
    {
        lock (gate)
        {
            var choice = Get(context);
            if (choice.State != "recording") return choice;
            choice = choice with { State = "waiting", EndedAt = now(), DueAt = now() + DefaultDelay, WarningAt = null, EndPrompted = false };
            Save(choice); return choice;
        }
    }

    /// <summary>Rejoining a game withdraws its pending end confirmation and cancels its automatic countdown.</summary>
    public void Resume(string context)
    {
        lock (gate)
        {
            var choice = Get(context);
            // Explicit keep-local and upload-now choices survive a rejoin.
            if (choice.State is "local" or "upload" || choice.Postponed) return;
            Save(choice with { State = "recording", EndedAt = null, DueAt = null, WarningAt = null, EndPrompted = false });
        }
    }

    /// <summary>Records that the finish question has been shown so reopening the app does not repeat it.</summary>
    public void MarkEndPrompted(string context) { lock (gate) Save(Get(context) with { EndPrompted = true }); }
    /// <summary>Explicitly releases this game (public now); the global upload switch and process-idle gate still apply.</summary>
    public void UploadNow(string context) { lock (gate) Save(Get(context) with { State = "upload", DueAt = null, WarningAt = null, EndPrompted = true, Postponed = false, Hold = "kept" }); }
    /// <summary>Settles how the site copy is held: "hidden" (made private until DueAt) or "kept" (left as it was).</summary>
    public void MarkHold(string context, string hold) { lock (gate) Save(Get(context) with { Hold = hold }); }
    /// <summary>The player chose the replay's privacy by hand: that choice wins, nothing is made public automatically.</summary>
    public void EndHold(string context) { lock (gate) { var choice = Get(context); if (choice.Hold == "hidden") Save(choice with { Hold = "kept" }); } }
    /// <summary>The site confirmed the hidden replay is public now.</summary>
    public void Revealed(string context) { lock (gate) Save(Get(context) with { State = "upload", DueAt = null, WarningAt = null, Hold = "kept" }); }
    /// <summary>Keeps the game local until an explicit upload choice.</summary>
    public void KeepLocal(string context) { lock (gate) Save(Get(context) with { State = "local", DueAt = null, WarningAt = null, EndPrompted = true }); }
    /// <summary>Starts a fresh countdown, requiring a new warning before sending.</summary>
    public void Postpone(string context, TimeSpan delay)
    {
        if (delay < WarningDelay) throw new ArgumentOutOfRangeException(nameof(delay));
        lock (gate) Save(Get(context) with { State = "waiting", EndedAt = Get(context).EndedAt ?? now(), DueAt = now() + delay, WarningAt = null, EndPrompted = true, Postponed = true });
    }

    /// <summary>Returns each warning once. A late restart grants a full five minutes before any automatic upload.</summary>
    public IReadOnlyList<UploadDecision> TakeWarnings()
    {
        lock (gate)
        {
            if (!Directory.Exists(Folder)) return [];
            var warnings = new List<UploadDecision>();
            foreach (var file in Directory.GetFiles(Folder, "*.json"))
            {
                var context = Path.GetFileNameWithoutExtension(file);
                if (!Regex.IsMatch(context, "^[a-f0-9]{32}$")) continue;
                var choice = Get(context);
                if (choice.State != "waiting" || choice.WarningAt is not null || choice.DueAt is not { } due || due - now() > WarningDelay) continue;
                choice = choice with { WarningAt = now(), DueAt = due < now() + WarningDelay ? now() + WarningDelay : due };
                Save(choice); warnings.Add(choice);
            }
            return warnings;
        }
    }

    /// <summary>Hidden replays whose time is up; a countdown cannot expire before its warning was persisted.</summary>
    public IReadOnlyList<UploadDecision> TakeReveals()
    {
        lock (gate)
        {
            if (!Directory.Exists(Folder)) return [];
            return Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension)
                .Where(c => Regex.IsMatch(c!, "^[a-f0-9]{32}$")).Select(c => Get(c!))
                .Where(d => d.State == "waiting" && d.Hold == "hidden" && d.WarningAt is not null && d.DueAt <= now()).ToList();
        }
    }

    /// <summary>Per-recording sender gate.</summary>
    public bool CanUpload(GameEntry game) => CanUpload(game, automaticAllowed: true);

    /// <summary>Automatic-upload preferences hold timed schedules but leave explicit upload choices available.</summary>
    public bool CanUpload(GameEntry game, bool automaticAllowed)
        => CanUpload(game, automaticAllowed, activeRecording: false);

    /// <summary>While-playing permission applies only to the currently recorded game, never an explicit local hold. A finished
    /// (waiting) game uploads at once, but only after its site copy is settled as hidden or kept.</summary>
    public bool CanUpload(GameEntry game, bool automaticAllowed, bool activeRecording)
    {
        var choice = Get(game.Context);
        return choice.State == "upload" || automaticAllowed && (activeRecording && choice.State == "recording"
            || choice.State == "waiting" && choice.Hold is not null);
    }
}
