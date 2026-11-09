using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>Delete with Undo (the user, 2026-10-31: "a way to regret deleting replays"): the held card, the list of held deletions and bringing one back.</summary>
sealed partial class LiveBackend
{
    /// <summary>What a held deletion keeps of the recording's card: enough to show it on Home and under Recently deleted.</summary>
    JsonObject HeldCard(JsonObject? game, string? context, string? identity)
    {
        game ??= context is null ? null : games.OfType<JsonObject>().FirstOrDefault(g => g.Str("context") == context);
        var card = new JsonObject();
        foreach (var key in new[] { "id", "mod", "modVersion", "date", "turns", "firstTurn", "lastTurn", "savedTurns", "players", "siteGame", "siteMod", "url" })
            if (game?[key] is { } v) card[key] = v.DeepClone();
        card["context"] = context;
        card["identity"] = identity;
        if (game is null) { card["id"] = context ?? "live"; card["mod"] = "Civ V"; card["date"] = J.Ms(DateTimeOffset.Now); card["turns"] = session.Status.Turn; card["players"] = new JsonArray(); }
        return card;
    }

    /// <summary>The deletions still held (Undo possible), newest first, each with the card it had.</summary>
    JsonArray HeldDeletions()
    {
        var list = new JsonArray();
        if (streamer is not { } sender) return list;
        foreach (var t in sender.QueuedReplayDeletions().Where(t => t.Due is not null && t.Due > DateTimeOffset.UtcNow).OrderByDescending(t => t.DeletedAt))
            list.Add(J.Obj(("key", t.Key), ("deletedAt", t.DeletedAt is { } at ? J.Ms(at) : null), ("due", J.Ms(t.Due!.Value)), ("card", t.Card?.DeepClone())));
        return list;
    }

    /// <summary>Undo: the held deletion leaves the queue, the recording shows and records again, its upload choice and privacy come back.</summary>
    JsonObject? UndoDelete(string? key)
    {
        if (streamer is not { } sender || key is null) return J.Obj(("toast", J.Toast("warn", "Recorder is starting", "Try again in a moment.")));
        _ = Task.Run(async () =>
        {
            try
            {
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var command = await sender.CancelReplayDeletionAsync(key, ct.Token);
                if (command is null) { Toast?.Invoke(J.Toast("warn", "Too late to undo", "The hold had ended: the recording was deleted.")); return; }
                var card = command["card"] as JsonObject;
                string? context = command["context"]?.GetValue<string>(), mod = command["mod"]?.GetValue<string>(), game = command["game"]?.GetValue<string>();
                if (card?.Str("identity") is { } identity) session.RestoreGameIdentity(identity);
                if (context is not null && card?["uploadDecision"] is JsonObject d && d.Str("state") is { } state)
                    delayedUploads.Restore(new UploadDecision
                    {
                        Context = context, State = state, Postponed = d["postponed"]?.GetValue<bool>() == true, EndPrompted = d["endPrompted"]?.GetValue<bool>() == true,
                        EndedAt = d["endedAt"] is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetValue<long>()) : null,
                        DueAt = d["dueAt"] is { } du ? DateTimeOffset.FromUnixTimeMilliseconds(du.GetValue<long>()) : null,
                        Hold = d.Str("hold"),
                    });
                deletedCards.Forget(card, context, card?.Str("identity"), mod, game);
                factsStale = true;
                nextLibrary = nextChecks = 0;
                // the site hid it at Delete: it shows again as it was
                if (command["hidden"]?.GetValue<bool>() == true && command["wasPrivate"]?.GetValue<bool>() != true)
                {
                    var choice = new JsonObject { ["private"] = false };
                    if (context is not null) await sender.ApplyReplayChoiceAsync(context, choice, ct.Token);
                    else if (mod is not null && game is not null) await sender.SetReplayAccessAsync(mod, game, choice, ct.Token);
                }
                Toast?.Invoke(J.Toast("good", "Recording brought back", "It is in your list again, with the visibility it had before."));
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
            {
                Toast?.Invoke(J.Toast("info", "Brought back · visibility pending", "The recording is back in your list. The website did not answer yet: check its visibility in Replay settings."));
            }
        });
        return J.Obj(("toast", J.Toast("info", "Bringing it back…", "Undoing the deletion.")));
    }
}
