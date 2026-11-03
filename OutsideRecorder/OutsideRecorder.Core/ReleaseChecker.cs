using System.Text.Json.Nodes;

namespace OutsideRecorderCore;

/// <summary>A public release announcement; checking never downloads or executes an installer.</summary>
public sealed record RecorderRelease(string Version, bool Required, string DownloadUrl, IReadOnlyList<string> Notes);

/// <summary>Reads the site's existing config so new releases can notify every running recorder.</summary>
public sealed class ReleaseChecker(HttpClient http, string currentVersion)
{
    /// <summary>Returns the newest applicable release, or null when this version is current.</summary>
    public async Task<RecorderRelease?> CheckAsync(Uri configUrl, CancellationToken ct)
    {
        using var response = await http.GetAsync(configUrl, ct);
        response.EnsureSuccessStatusCode();
        var config = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
        var latest = config["latestVersion"]?.GetValue<string>();
        var minimum = config["minVersion"]?.GetValue<string>();
        bool required = Compare(minimum, currentVersion) > 0;
        var target = Compare(minimum, latest) > 0 ? minimum : latest;
        if (Compare(target, currentVersion) <= 0) return null;
        var link = config["downloadUrl"]?.GetValue<string>();
        if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme != "https" || !url.IsDefaultPort
            || url.UserInfo.Length != 0 || !(url.IdnHost.Equals("ffaciv.com", StringComparison.OrdinalIgnoreCase)
                || url.IdnHost.Equals("www.ffaciv.com", StringComparison.OrdinalIgnoreCase)))
            link = "https://ffaciv.com/replay/recorder/";
        var notes = (config["releaseNotes"] as JsonArray ?? []).OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var text) ? text : null).OfType<string>().Take(12).ToArray();
        return new RecorderRelease(target!, required, link!, notes);
    }

    /// <summary>Normalizes dotted versions so 0.6 and 0.6.0 compare equally.</summary>
    public static int Compare(string? a, string? b)
    {
        static Version Parse(string? v) => Version.TryParse(v, out var n)
            ? new Version(n.Major, n.Minor, Math.Max(0, n.Build), Math.Max(0, n.Revision)) : new Version(0, 0, 0, 0);
        return Parse(a).CompareTo(Parse(b));
    }
}
