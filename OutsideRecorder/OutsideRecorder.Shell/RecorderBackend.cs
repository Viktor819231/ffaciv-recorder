using System.Text.Json.Nodes;

namespace OutsideRecorderShell;

/// <summary>
/// The one door between the window and the recorder (card OR02 "The contract"). The window only draws <see cref="BuildState"/>
/// and sends commands back; everything the recorder does (reading Civ, sending, publishing) stays behind this interface,
/// so the reader core and the window can change apart. JSON nodes rather than typed records: no reflection, so the
/// release host can be Native AOT.
/// </summary>
public interface IRecorderBackend : IDisposable
{
    /// <summary>The whole state the window shows (shape: OR02 "State"; ui/js/mock.js base() is a full example).</summary>
    JsonObject BuildState();
    /// <summary>Runs a command from the window or a pop-up; may answer with { dialog?, toast? } for the window.</summary>
    JsonObject? Handle(string cmd, JsonObject args);
    /// <summary>A pop-up to show (any thread): { kind, title, text, actions? }.</summary>
    event Action<JsonObject>? Toast;
    /// <summary>Starts the work (reading Civ, watching saves).</summary>
    void Start();
}

/// <summary>Small JSON helpers so the backends read like the JS state they build.</summary>
static class J
{
    public static JsonObject Obj(params (string Key, JsonNode? Value)[] items)
    {
        var o = new JsonObject();
        foreach (var (k, v) in items) o[k] = v;
        return o;
    }
    public static JsonArray Arr(IEnumerable<JsonNode?> items) => new(items.ToArray());
    public static JsonObject Toast(string kind, string title, string text) => Obj(("kind", kind), ("title", title), ("text", text));
    public static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    public static string? Str(this JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
