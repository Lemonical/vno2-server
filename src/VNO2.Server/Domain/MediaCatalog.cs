using System.Text.Json;
using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public static class MediaCatalog
{
    public static void Seed(WorldState state)
    {
        state.Media.TryAdd("demo-music", new("demo-music", "Demo music", "music", "demo.wav"));
        state.Media.TryAdd("demo-sound", new("demo-sound", "Demo sound", "sound", "demo.wav"));
        state.Media.TryAdd("shake", new("shake", "Shake", "effect", "builtin:shake"));
        state.Media.TryAdd("flash", new("flash", "Flash", "effect", "builtin:flash"));
    }

    public static string? Require(WorldState state, string? id, string kind, bool allowEmpty = false)
    {
        if (string.IsNullOrEmpty(id))
        {
            if (allowEmpty) return null;
            throw new DomainException("media_required", $"An approved {kind} media ID is required");
        }
        if (!state.Media.TryGetValue(id, out var definition) || !definition.Enabled || definition.Kind != kind)
            throw new DomainException("media_not_permitted", $"This media ID is not approved for {kind}");
        return id;
    }

    public static string? Visible(WorldState state, string? id, string kind) =>
        id != null && state.Media.TryGetValue(id, out var definition) && definition.Enabled && definition.Kind == kind ? id : null;

    public static MediaDefinition Configure(ActionContext context, JsonElement data)
    {
        context.DemandAdministrator();
        var id = data.Text("mediaId", 100);
        var name = data.Text("name", 100);
        var kind = data.Text("kind", 20);
        var reference = data.Text("contentReference", 200);
        if (kind is not "music" and not "sound" and not "background" and not "image" and not "effect")
            throw new DomainException("invalid_media_kind", "Use music, sound, background, image, or effect");
        if (context.State.Media.TryGetValue(id, out var existing) && existing.Kind != kind)
            throw new DomainException("media_kind_conflict", "A media ID's kind cannot be changed; choose a new ID");
        if (kind == "effect")
        {
            if (reference is not "builtin:shake" and not "builtin:flash")
                throw new DomainException("invalid_reference", "The supported built-in effect references are builtin:shake and builtin:flash");
        }
        else if (reference.StartsWith('/') || reference.Contains('\\') || reference.Contains(':') || reference.Contains('?') || reference.Contains('#') ||
            reference.Any(char.IsControl) || reference.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new DomainException("invalid_reference", "Media content references must be relative local paths without traversal or URI components");
        if (!context.State.Media.ContainsKey(id) && context.State.Media.Count >= 500)
            throw new DomainException("limit_exceeded", "The media catalog limit has been reached");
        var definition = new MediaDefinition(id, name, kind, reference, data.Flag("enabled", true));
        context.State.Media[id] = definition;
        if (JsonSerializer.SerializeToUtf8Bytes(context.State.Media.Values, ProtocolJson.Options).Length > 262144)
            throw new DomainException("state_limit", "The media catalog exceeds its 256 KiB limit");
        context.PrivateEvent("media.catalog.changed", new { mediaId = id, kind, definition.Enabled }, context.State.Accounts.Keys.ToArray());
        return definition;
    }
}
