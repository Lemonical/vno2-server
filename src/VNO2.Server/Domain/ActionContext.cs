using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public sealed class ActionContext(WorldState state, AccountState account, CommunityOptions options)
{
    public WorldState State { get; } = state;
    public AccountState Account { get; } = account;
    public CommunityOptions Options { get; } = options;
    public List<PendingEvent> Events { get; } = [];
    public List<string> DisconnectAccountIds { get; } = [];

    public RoomState Room => Account.RoomId is { } id && State.Rooms.TryGetValue(id, out var room)
        ? room : throw new DomainException("not_in_room", "Join a room first");

    public void DemandSpeak()
    {
        if (Account.Muted) throw new DomainException("muted", "You are muted in this community");
    }

    public void DemandDirector()
    {
        if (Account.Isolated) throw new DomainException("isolated", "Privileged actions are unavailable while isolated");
        if (!WorldQueries.CanDirect(Account)) throw new DomainException("forbidden", "A director role is required");
    }

    public void DemandAdministrator()
    {
        if (Account.Isolated) throw new DomainException("isolated", "Privileged actions are unavailable while isolated");
        if (!WorldQueries.IsAdministrator(Account)) throw new DomainException("forbidden", "An administrator role is required");
    }

    public void DemandModerator()
    {
        if (Account.Isolated) throw new DomainException("isolated", "Privileged actions are unavailable while isolated");
        if (!WorldQueries.CanModerate(Account)) throw new DomainException("forbidden", "A moderation role is required");
    }

    public void DemandEconomy()
    {
        if (!Options.EconomyEnabled) throw new DomainException("feature_disabled", "Optional resources are disabled on this server");
    }

    public AccountState Target(string accountId) => State.Accounts.TryGetValue(accountId, out var target)
        ? target : throw new DomainException("not_found", "The target account does not exist");

    public void RoomEvent(string kind, object data, string? roomId = null, AccountState? source = null) =>
        Events.Add(new(kind, roomId ?? Room.Id, Account.Id, data,
            WorldQueries.Audience(State, roomId ?? Room.Id, source ?? Account)));

    public void PrivateEvent(string kind, object data, params string[] accounts) =>
        Events.Add(new(kind, Account.RoomId, Account.Id, data, accounts));

    public void StageEvent(string kind, object? detail = null)
    {
        var actors = State.Accounts.Values.Where(a => a.RoomId == Room.Id && a.Position != null && a.CharacterId != null &&
            !a.Banned && (!a.Isolated || Account.Isolated && a.Id == Account.Id))
            .Select(a => new ActorSnapshot("actor-" + a.Id, a.Id, a.CharacterId!, a.Expression, a.Position!,
                Room.SpeakerAccountId == a.Id, a.ScaleX, a.ScaleY)).ToArray();
        var speaker = Room.SpeakerAccountId is { } id && !State.Accounts[id].Isolated ? id : null;
        RoomEvent(kind, new { actors, speakerAccountId = speaker, background = MediaCatalog.Visible(State, Room.Background, "background"), music = MediaCatalog.Visible(State, Room.Music, "music"), detail });
    }
}
