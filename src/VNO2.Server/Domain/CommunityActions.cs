using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public static class CommunityActions
{
    public static ActionResult Apply(WorldState state, AccountState account, CommunityRequest request, CommunityOptions options)
    {
        var context = new ActionContext(state, account, options);
        object result = request.Action switch
        {
            "room.join" or "room.leave" or "room.configure" or "room.reserve" => RoomActions.Apply(context, request.Action, request.Data),
            "character.select" or "dialogue.send" or "dialogue.clear" or "chat.send" or "stage.move" or "stage.clear" or "catalog.configure" => PerformanceActions.Apply(context, request.Action, request.Data),
            "inventory.give" or "inventory.discard" or "inventory.show" or "inventory.use" or "inventory.grant" or "credits.transfer" or "credits.grant" or "shop.buy" or "shop.configure" or "resources.set" => ResourceActions.Apply(context, request.Action, request.Data),
            "moderation.kick" or "moderation.ban" or "moderation.unban" or "moderation.mute" or "moderation.isolate" or "moderation.role" or "moderation.lookup" => ModerationActions.Apply(context, request.Action, request.Data),
            "director.actor" or "director.announce" or "director.background" or "director.music" or "director.image" or "director.effect" or "director.timer" or "director.timer.cancel" or "director.cue.save" or "director.cue.play" or "director.cue.list" or "director.preview" => DirectorActions.Apply(context, request.Action, request.Data),
            "dice.roll" or "arithmetic.evaluate" => RuleActions.Apply(context, request.Action, request.Data),
            "media.configure" => MediaCatalog.Configure(context, request.Data),
            "snapshot.get" or "ping" => new { ok = true },
            _ => throw new DomainException("unknown_action", "The requested action is not supported")
        };
        return new(result, context.Events, context.DisconnectAccountIds);
    }
}
