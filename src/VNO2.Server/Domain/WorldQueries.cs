using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public static class WorldQueries
{
    public static bool IsAdministrator(AccountState account) => !account.Isolated && account.Role == "administrator";
    public static bool CanModerate(AccountState account) => !account.Isolated && account.Role is "administrator" or "moderator";
    public static bool CanDirect(AccountState account) => !account.Isolated && account.Role is "administrator" or "director";
    public static bool CanSee(AccountState viewer, AccountState subject) =>
        !subject.Isolated || viewer.Id == subject.Id || CanModerate(viewer);

    public static string[] Audience(WorldState state, string? roomId, AccountState? source = null) =>
        state.Accounts.Values.Where(a => !a.Banned && a.RoomId == roomId && roomId != null &&
            (source == null || CanSee(a, source))).Select(a => a.Id).ToArray();

    public static string[] Moderators(WorldState state) => state.Accounts.Values
        .Where(a => !a.Banned && CanModerate(a)).Select(a => a.Id).ToArray();

    public static RoomSummary[] Rooms(WorldState state, AccountState? viewer = null) => state.Rooms.Values.Select(r =>
        new RoomSummary(r.Id, r.Name, r.Description,
            state.Accounts.Values.Count(a => a.RoomId == r.Id && !a.Banned && (!a.Isolated || viewer != null && CanSee(viewer, a))),
            r.Capacity, r.PasswordHash != null, r.Locked, r.EntryCost, r.MinimumCredits, r.RequiredItemId)).ToArray();

    public static ActorSnapshot[] Actors(WorldState state, AccountState viewer) => state.Accounts.Values
        .Where(a => a.RoomId == viewer.RoomId && viewer.RoomId != null && !a.Banned && a.CharacterId != null &&
            a.Position != null && CanSee(viewer, a))
        .Select(a => new ActorSnapshot("actor-" + a.Id, a.Id, a.CharacterId!, a.Expression, a.Position!,
            state.Rooms[a.RoomId!].SpeakerAccountId == a.Id, a.ScaleX, a.ScaleY)).ToArray();

    public static CommunitySnapshot Snapshot(WorldState state, string accountId, long sequence, CommunityOptions options)
    {
        var account = state.Accounts[accountId];
        var room = account.RoomId != null ? state.Rooms[account.RoomId] : null;
        return new(sequence, account.Id, account.Role, account.RoomId, Rooms(state, account),
            state.Characters.Values.ToArray(),
            state.Accounts.Values.Where(a => a.RoomId == account.RoomId && account.RoomId != null && !a.Banned && CanSee(account, a))
                .Select(a => new ParticipantSnapshot(a.Id, a.DisplayName, a.Role, a.CharacterId, a.Muted,
                    a.Isolated && (a.Id == account.Id || CanModerate(account)))).ToArray(),
            Actors(state, account), room?.SpeakerAccountId is { } speaker && state.Accounts.TryGetValue(speaker, out var speakerAccount)
                && CanSee(account, speakerAccount) ? speaker : null,
            MediaCatalog.Visible(state, room?.Background, "background"), MediaCatalog.Visible(state, room?.Music, "music"),
            new(account.Credits, account.Hp, account.MaxHp, account.Mp, account.MaxMp, account.Cooldowns),
            account.Inventory.Where(i => i.Value > 0 && state.Items.ContainsKey(i.Key))
                .Select(i => new InventoryEntry(i.Key, state.Items[i.Key].Name, i.Value)).ToArray(),
            options.EconomyEnabled ? state.Items.Values.Where(i => i.ForSale).ToArray() : [],
            state.Timers.Where(t => t.RoomId == account.RoomId).Select(t => new TimerSnapshot(t.Id, t.Label, t.EndsAt)).ToArray(),
            Permissions(account, options))
        {
            MediaCatalog = state.Media.Values.Where(media => media.Enabled).ToArray(),
            CurrentDialogue = CurrentDialogue(state, account)
        };
    }

    private static DialogueSnapshot? CurrentDialogue(WorldState state, AccountState viewer)
    {
        if (viewer.RoomId == null) return null;
        var current = state.Rooms[viewer.RoomId].LastDialogue;
        foreach (var isolated in state.Accounts.Values.Where(account => account.RoomId == viewer.RoomId && account.Isolated &&
            !account.Banned && CanSee(viewer, account) && account.PrivateDialogueAudience.Contains(viewer.Id, StringComparer.Ordinal)))
        {
            if (isolated.PrivateDialogue is { } candidate && (current == null || candidate.Timestamp > current.Timestamp)) current = candidate;
        }
        return current;
    }

    private static string[] Permissions(AccountState account, CommunityOptions options)
    {
        var permissions = new List<string> { "room.join", "character.select", "dialogue.send", "dialogue.clear", "chat.send", "stage.move", "replay.get", "dice.roll", "arithmetic.evaluate" };
        if (CanModerate(account)) permissions.Add("moderation");
        if (CanDirect(account)) permissions.Add("director");
        if (IsAdministrator(account)) permissions.Add("administration");
        if (options.EconomyEnabled) permissions.Add("economy");
        if (options.FreeformResourcesEnabled) permissions.Add("resources.self");
        return permissions.ToArray();
    }
}
