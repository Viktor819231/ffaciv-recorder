using System.Text.Json.Nodes;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>Checks release announcements on startup/hourly and notifies once per version, including in the tray.</summary>
sealed class ReleaseNotifications : IDisposable
{
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly string version, preferencesFile;
    readonly Uri configUrl;
    readonly Func<bool> notify;
    readonly object gate = new();
    readonly System.Threading.Timer timer;
    readonly CancellationTokenSource stop = new();
    RecorderRelease? available;
    JsonObject preferences;
    long? checkedAt;
    string? error, dismissThisRun;
    int checking;
    public event Action<JsonObject>? Toast;
    public Func<bool> CanCheck {get;set;} = ()=>true;

    public ReleaseNotifications(string version, RecorderSettings settings, bool preview, Func<bool> notify)
    {
        this.version = version; this.notify = notify;
        configUrl = new Uri(new Uri(settings.StreamAddress), "config");
        preferencesFile = Path.Combine(settings.OutputFolder, "app", "release-notifications.json");
        try { preferences = JsonNode.Parse(SafeFiles.ReadAllText(preferencesFile))!.AsObject(); } catch { preferences = new(); }
        timer = new System.Threading.Timer(_ => Check(), null, preview ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(2), TimeSpan.FromHours(1));
    }

    public void Check() => _ = CheckAsync();
    async Task CheckAsync()
    {
        if (!CanCheck() || UploadPolicy.CivRunning()) return;
        if (Interlocked.Exchange(ref checking, 1) != 0) return;
        try
        {
            var release = await new ReleaseChecker(http, version).CheckAsync(configUrl, stop.Token);
            JsonObject? toast = null;
            lock (gate)
            {
                available = release; checkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); error = null;
                if (release is not null && notify() && !Snoozed(release.Version) && preferences.Str("notified") != release.Version)
                {
                    preferences["notified"] = release.Version; Save();
                    toast = J.Toast(release.Required ? "warn" : "info", $"Recorder {release.Version} is available",
                        release.Required ? "This update is needed to keep sending games. Your recordings wait safely on this PC."
                        : "A new version is ready. Download it when you are ready to update.");
                    toast["actions"] = new JsonArray(J.Obj(("label", "Download update"), ("cmd", "updateNow"), ("primary", true)));
                }
            }
            if (toast is not null) Toast?.Invoke(toast);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { lock (gate) error = "Could not check for updates. Try again when you are online."; }
        finally { Interlocked.Exchange(ref checking, 0); }
    }

    bool Snoozed(string target) => dismissThisRun == target || preferences.Str("snoozeVersion") == target
        && preferences["snoozeUntil"]?.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Later(string choice)
    {
        lock (gate)
        {
            if (available is null) return;
            long until;
            if (choice == "next") { dismissThisRun = available.Version; until = 0; }
            else if (choice == "tonight")
            {
                var tonight = DateTime.Today.AddHours(22); if (tonight <= DateTime.Now) tonight = tonight.AddDays(1);
                until = new DateTimeOffset(tonight).ToUnixTimeMilliseconds();
            }
            else until = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
            preferences["snoozeVersion"] = available.Version; preferences["snoozeUntil"] = until;
            preferences["later"] = choice; preferences["notified"] = null; Save();
        }
    }

    public string DownloadUrl { get { lock (gate) return available?.DownloadUrl ?? "https://ffaciv.com/replay/recorder/"; } }
    public JsonObject State()
    {
        lock (gate)
        {
            var release = available;
            return J.Obj(("current", version), ("checking", checking != 0), ("checkedAt", checkedAt), ("error", error),
                ("available", release is null ? null : J.Obj(("version", release.Version), ("required", release.Required),
                    ("url", release.DownloadUrl), ("notes", J.Arr(release.Notes.Select(n => (JsonNode?)JsonValue.Create(n)))),
                    ("later", Snoozed(release.Version) ? preferences.Str("later") : null))), ("changelog", new JsonArray()));
        }
    }
    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(preferencesFile)!);
        SafeFiles.Replace(preferencesFile, preferences.ToJsonString());
    }
    public void Dispose() { timer.Dispose(); stop.Cancel(); http.Dispose(); }
}
