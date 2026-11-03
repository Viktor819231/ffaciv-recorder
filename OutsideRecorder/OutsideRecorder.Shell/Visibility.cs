using System.Text;
using System.Text.Json;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>One game's Block choice: what the player wants, and whether the live site has taken it.</summary>
sealed record VisibilityChoice
{
    public bool Blocked { get; init; }
    public bool Confirmed { get; init; }
    public string? Recording { get; init; }
    public DateTimeOffset Changed { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// Block / Unblock of a finished game (OR03 "Visibility"): Blocked = hidden from everyone on the site. The choice is kept on
/// this PC (visibility.json next to the recordings) and sent to the local receiver until it takes it, every 5 s, so a server
/// that is off for a while (or a game not converted yet: 404) still gets it later: POST &lt;stream url&gt;games/&lt;game&gt;/visibility
/// {"blocked"} with the install's key (<see cref="Streamer.SetVisibilityAsync"/>).
/// </summary>
sealed class Visibility : IDisposable
{
    readonly string file;
    readonly Func<Streamer?> streamer;
    readonly object gate = new();
    readonly System.Threading.Timer timer;
    bool sending;

    public Visibility(RecorderSettings settings, Func<Streamer?> streamer, bool preview)
    {
        file = Path.Combine(settings.OutputFolder, "visibility.json");
        this.streamer = streamer;
        timer = new System.Threading.Timer(_ => _ = SendAsync(), null, preview ? Timeout.Infinite : 2000, preview ? Timeout.Infinite : 5000);
    }

    /// <summary>Every choice by game identity.</summary>
    public Dictionary<string, VisibilityChoice> All()
    {
        lock (gate)
        {
            try { return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, VisibilityChoice>>(SafeFiles.ReadAllText(file)) ?? [] : []; }
            catch { return []; }
        }
    }

    /// <summary>Blocks or unblocks a game; sent at once and again until the site takes it.</summary>
    public void Set(string identity, string? recording, bool blocked)
    {
        lock (gate)
        {
            var all = All();
            all[identity] = new VisibilityChoice { Blocked = blocked, Recording = recording, Changed = DateTimeOffset.Now };
            SafeFiles.Replace(file, JsonSerializer.Serialize(all));
        }
        _ = SendAsync();
    }

    async Task SendAsync()
    {
        lock (gate) { if (sending) return; sending = true; }
        try
        {
            foreach (var (id, c) in All().Where(x => !x.Value.Confirmed))
            {
                string? error = null;
                try
                {
                    if (streamer() is not { } s) error = "live streaming is off in Settings";
                    else
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var (code, body) = await s.SetVisibilityAsync(id, c.Blocked, cts.Token);
                        error = code is >= 200 and < 300 ? null : code switch
                        {
                            404 => "the test server has not made this game yet",
                            403 => "the test server says this PC sent nothing of this game",
                            401 => "the test server did not know this PC's key",
                            _ => $"{code} {body}".Trim(),
                        };
                    }
                }
                catch (Exception e) { error = e is HttpRequestException or TaskCanceledException ? "the test server is not reachable" : e.Message; }
                lock (gate)
                {
                    var all = All();
                    if (!all.TryGetValue(id, out var now) || now.Changed != c.Changed) continue;   // changed meanwhile: the next round sends that
                    all[id] = now with { Confirmed = error is null, LastError = error };
                    SafeFiles.Replace(file, JsonSerializer.Serialize(all));
                }
            }
        }
        catch { }
        finally { lock (gate) sending = false; }
    }

    public void Dispose() => timer.Dispose();
}
