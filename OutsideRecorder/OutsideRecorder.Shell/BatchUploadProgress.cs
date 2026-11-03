using System.Text.Json.Nodes;

namespace OutsideRecorderShell;

/// <summary>Only server acknowledgements advance a manual upload into parsing or a ready preview.</summary>
static class BatchUploadProgress
{
    public static JsonObject Read(JsonObject state)
    {
        int Count(string key) => Math.Max(0, state[key]?.GetValue<int>() ?? 0);
        int total = Count("saves"), parsed = Count("processed"), failed = Count("failed"), unsupported = Count("unsupported");
        var links = new JsonArray();
        bool complete = total > 0 && parsed >= total && failed == 0 && unsupported == 0;
        if (complete && state["games"] is JsonArray games)
            foreach (var link in games.OfType<JsonObject>().Where(g => !string.IsNullOrWhiteSpace(g["url"]?.GetValue<string>()))) links.Add(link.DeepClone());
        string phase = links.Count > 0 ? "ready" : unsupported > 0 ? "unsupported" : failed > 0 ? "error" : Count("held") > 0 && parsed < total ? "held" : complete ? "preview" : "parsing";
        string note = phase switch {
            "ready" => "Replay ready. Open the game page below.",
            "unsupported" => "Saves received. Waiting for website support for this mod version.",
            "error" => $"{failed} saves could not be parsed. Your local saves are kept; select this game to retry.",
            "held" => "Waiting for server capacity. Parsing resumes automatically.",
            "preview" => "Saves parsed. Creating the game preview page…",
            _ => "Saves received. The server is parsing the game…"
        };
        return new JsonObject { ["phase"] = phase, ["parsed"] = parsed, ["parseTotal"] = total, ["failed"] = failed, ["note"] = note, ["links"] = links };
    }
}
