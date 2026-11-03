using System.Text.Json.Nodes;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>Applies accepted deletion choices to cached cards without rescanning saves during a resource hold.</summary>
sealed class RecordingDeletionCache
{
    readonly object gate = new();
    readonly HashSet<string> keys = new(StringComparer.Ordinal);

    public void Remember(JsonObject? card, string? context = null, string? identity = null)
    {
        lock (gate)
        {
            Add("id", Str(card, "id")); Add("context", context ?? Str(card, "context"));
            Add("identity", identity ?? Str(card, "identity"));
            Add("url", Str(card, "url") ?? Str(card, "pageUrl"));
            Add("site", Site(Str(card, "siteMod") ?? Str(card, "mod"), Str(card, "siteGame") ?? Str(card, "game")));
        }
    }

    public void Remember(IEnumerable<ReplayDeletionTarget> targets)
    {
        lock (gate)
            foreach (var target in targets) { Add("context", target.Context); Add("site", Site(target.Mod, target.Game)); }
    }

    /// <summary>Undo of a held deletion: the card shows again.</summary>
    public void Forget(JsonObject? card, string? context = null, string? identity = null, string? mod = null, string? game = null)
    {
        lock (gate)
        {
            Remove("id", Str(card, "id")); Remove("context", context ?? Str(card, "context"));
            Remove("identity", identity ?? Str(card, "identity"));
            Remove("url", Str(card, "url") ?? Str(card, "pageUrl"));
            Remove("site", Site(mod ?? Str(card, "siteMod") ?? Str(card, "mod"), game ?? Str(card, "siteGame") ?? Str(card, "game")));
        }
    }
    void Remove(string kind, string? value) { if (!string.IsNullOrEmpty(value)) keys.Remove(kind + ":" + value); }
    /// <summary>Also filters stale asynchronous library responses; it never needs disk access or worker permission.</summary>
    public JsonArray Filter(JsonArray cards)
    {
        lock (gate)
            return new JsonArray(cards.OfType<JsonObject>().Where(card => !Contains(card)).Select(card => (JsonNode)card.DeepClone()).ToArray());
    }

    public bool Hides(JsonObject card) { lock (gate) return Contains(card); }

    bool Contains(JsonObject card) => Has("id", Str(card, "id")) || Has("context", Str(card, "context")) || Has("identity", Str(card, "identity"))
        || Has("url", Str(card, "url") ?? Str(card, "pageUrl"))
        || Has("site", Site(Str(card, "siteMod") ?? Str(card, "mod"), Str(card, "siteGame") ?? Str(card, "game")));
    void Add(string kind, string? value) { if (!string.IsNullOrEmpty(value)) keys.Add(kind + ":" + value); }
    bool Has(string kind, string? value) => !string.IsNullOrEmpty(value) && keys.Contains(kind + ":" + value);
    static string? Site(string? mod, string? game) => string.IsNullOrWhiteSpace(mod) || string.IsNullOrWhiteSpace(game) ? null : mod + "/" + game;
    static string? Str(JsonObject? card, string key) => card?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
