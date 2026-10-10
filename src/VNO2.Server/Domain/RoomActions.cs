using System.Text.Json;
using VNO2.Server.Security;

namespace VNO2.Server.Domain;

public static class RoomActions
{
    public static object Apply(ActionContext context, string action, JsonElement data)
    {
        var account = context.Account;
        var state = context.State;
        if (action == "room.leave")
        {
            var old = account.RoomId;
            if (old == null) return new { roomId = (string?)null };
            Leave(context, account);
            context.Events.Add(new("room.left", old, account.Id, new { accountId = account.Id }, WorldQueries.Audience(state, old, account).Append(account.Id).ToArray()));
            return new { roomId = (string?)null };
        }
        var id = data.Text("roomId", 80);
        if (action == "room.configure")
        {
            context.DemandAdministrator();
            if (!state.Rooms.TryGetValue(id, out var configured))
            {
                if (state.Rooms.Count >= 100) throw new DomainException("limit_exceeded", "The room limit has been reached");
                configured = new RoomState { Id = id };
            }
            var itemId = data.Text("requiredItemId", 80, false);
            if (itemId.Length > 0 && !state.Items.ContainsKey(itemId)) throw new DomainException("not_found", "The required item does not exist");
            configured.Name = data.Text("name", 100);
            configured.Description = data.Text("description", 1000, false);
            configured.Capacity = data.Number("capacity", 1, 256, 32);
            configured.Locked = data.Flag("locked");
            configured.EntryCost = data.Money("entryCost");
            configured.MinimumCredits = data.Money("minimumCredits");
            configured.RequiredItemId = itemId.Length == 0 ? null : itemId;
            configured.AllowPlayerMusic = data.Flag("allowPlayerMusic");
            if (data.TryGetProperty("password", out _))
            {
                var password = data.Text("password", 200, false);
                configured.PasswordHash = password.Length == 0 ? null : SecretHash.Password(password);
            }
            state.Rooms[id] = configured;
            context.PrivateEvent("room.configured", new { roomId = id }, WorldQueries.Moderators(state));
            return new { roomId = id };
        }
        if (!state.Rooms.TryGetValue(id, out var room)) throw new DomainException("not_found", "The room does not exist");
        if (action == "room.reserve")
        {
            context.DemandDirector();
            var reservations = data.Strings("accountIds", room.Capacity, 100);
            foreach (var target in reservations) context.Target(target);
            room.ReservedAccountIds = reservations.ToList();
            return new { roomId = id, reservedSeats = reservations.Length };
        }
        if (account.RoomId == id) return new { roomId = id, charged = 0 };
        if (account.Isolated) throw new DomainException("isolated", "Room changes are restricted while isolated");
        var staff = WorldQueries.CanDirect(account) || WorldQueries.CanModerate(account);
        if (room.Locked && !staff && !room.ReservedAccountIds.Contains(account.Id)) throw new DomainException("room_locked", "The room is locked");
        if (!staff && room.PasswordHash != null && !SecretHash.VerifyPassword(data.Text("password", 200, false), room.PasswordHash))
            throw new DomainException("room_password", "The room password is incorrect");
        var occupants = state.Accounts.Values.Count(a => a.RoomId == id && !a.Banned);
        var reservedMissing = room.ReservedAccountIds.Count(reserved => !state.Accounts.TryGetValue(reserved, out var occupant) || occupant.RoomId != id);
        if (occupants >= room.Capacity || !room.ReservedAccountIds.Contains(account.Id) && occupants + reservedMissing >= room.Capacity)
            throw new DomainException("room_full", "The room has no unreserved seats");
        if (room.RequiredItemId is { } required && account.Inventory.GetValueOrDefault(required) <= 0)
            throw new DomainException("required_item", "An item is required to enter this room");
        if (account.Credits < room.MinimumCredits || account.Credits < room.EntryCost)
            throw new DomainException("insufficient_credits", "You do not meet the room's balance or entry cost requirement");
        var oldRoom = account.RoomId;
        Leave(context, account);
        account.Credits -= room.EntryCost;
        account.RoomId = id;
        if (oldRoom != null) context.Events.Add(new("room.left", oldRoom, account.Id, new { accountId = account.Id }, WorldQueries.Audience(state, oldRoom)));
        context.RoomEvent("room.joined", new { accountId = account.Id, account.DisplayName });
        if (room.EntryCost > 0) context.PrivateEvent("credits.changed", new { account.Credits, reason = "room_entry" }, account.Id);
        return new { roomId = id, charged = room.EntryCost };
    }

    public static void Leave(ActionContext context, AccountState account)
    {
        if (account.RoomId is { } old && context.State.Rooms[old].SpeakerAccountId == account.Id)
            context.State.Rooms[old].SpeakerAccountId = null;
        account.RoomId = null;
        account.PrivateDialogue = null;
        account.PrivateDialogueAudience = [];
        account.Position = null;
    }
}
