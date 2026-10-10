using System.Text.Json;

namespace VNO2.Server.Domain;

public static class ModerationActions
{
    public static object Apply(ActionContext context, string action, JsonElement data)
    {
        var actor = context.Account;
        context.DemandModerator();
        if (action == "moderation.lookup")
        {
            var query = data.Text("query", 100, false);
            return new
            {
                accounts = context.State.Accounts.Values.Where(a => query.Length == 0 || a.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(100).Select(a => new { accountId = a.Id, a.DisplayName, a.Role, a.RoomId, a.Banned, a.Muted, a.Isolated }).ToArray()
            };
        }
        var target = context.Target(data.Text("targetAccountId", 100));
        if (target.Id == actor.Id) throw new DomainException("invalid_target", "You cannot moderate your own account");
        if (context.Options.AdministratorAccountIds.Contains(target.Id, StringComparer.Ordinal))
            throw new DomainException("protected_account", "A configured owner cannot be moderated");
        if (!WorldQueries.IsAdministrator(actor) && target.Role != "member")
            throw new DomainException("forbidden", "Only an administrator can moderate staff");
        var reason = data.Text("reason", 500, false);
        var enabled = data.Flag("enabled", true);
        switch (action)
        {
            case "moderation.role":
                context.DemandAdministrator();
                var role = data.Text("role", 30);
                if (role is not "member" and not "director" and not "moderator" and not "administrator")
                    throw new DomainException("invalid_role", "Use member, director, moderator, or administrator");
                target.Role = role;
                break;
            case "moderation.ban":
                target.Banned = true;
                goto case "moderation.kick";
            case "moderation.kick":
                var oldRoom = target.RoomId;
                RoomActions.Leave(context, target);
                context.DisconnectAccountIds.Add(target.Id);
                if (oldRoom != null) context.Events.Add(new("room.left", oldRoom, target.Id, new { accountId = target.Id }, WorldQueries.Audience(context.State, oldRoom, target)));
                break;
            case "moderation.unban": target.Banned = false; break;
            case "moderation.mute": target.Muted = enabled; break;
            case "moderation.isolate":
                target.Isolated = enabled;
                target.Position = null;
                if (target.RoomId is { } roomId && context.State.Rooms[roomId].SpeakerAccountId == target.Id)
                    context.State.Rooms[roomId].SpeakerAccountId = null;
                break;
        }
        context.PrivateEvent("moderation.applied", new { targetAccountId = target.Id, action, reason, enabled, target.Role }, WorldQueries.Moderators(context.State));
        context.PrivateEvent("moderation.notice", new { action, enabled, target.Role }, target.Id);
        return new { targetAccountId = target.Id, action, enabled, target.Role };
    }
}
