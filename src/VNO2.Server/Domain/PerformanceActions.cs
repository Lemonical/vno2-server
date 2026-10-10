using System.Text.Json;
using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public static class PerformanceActions
{
    public static object Apply(ActionContext context, string action, JsonElement data)
    {
        var account = context.Account;
        var state = context.State;
        if (action == "catalog.configure")
        {
            context.DemandAdministrator();
            var id = data.Text("characterId", 100);
            var content = data.Text("contentReference", 200);
            var segments = content.Split('/');
            if (segments.Length != 2 || segments.Any(s => s.Length == 0 || s is "." or ".." || s.Contains('\\') || s.Contains(':')))
                throw new DomainException("invalid_reference", "Content references must be Category/Character");
            var expressions = data.Strings("expressions", 200, 100);
            if (expressions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expressions.Length)
                throw new DomainException("invalid_expression", "Expression names must be unique ignoring case");
            var defaultExpression = data.Text("defaultExpression", 100, false);
            if (expressions.Length == 0) expressions = ["default"];
            if (defaultExpression.Length == 0) defaultExpression = expressions[0];
            if (!expressions.Contains(defaultExpression, StringComparer.Ordinal)) throw new DomainException("invalid_expression", "The default expression must exist");
            if (state.Characters.Values.Any(c => c.Id != id && string.Equals(c.ContentReference, content, StringComparison.OrdinalIgnoreCase)))
                throw new DomainException("content_collision", "Another character uses this case-insensitive content reference");
            if (!state.Characters.ContainsKey(id) && state.Characters.Count >= 1000) throw new DomainException("limit_exceeded", "Catalog limit reached");
            var reservation = state.Characters.GetValueOrDefault(id)?.ReservedAccountId;
            if (data.TryGetProperty("reservedAccountId", out _))
            {
                var requested = data.Text("reservedAccountId", 100, false);
                reservation = requested.Length == 0 ? null : requested;
            }
            if (reservation != null) context.Target(reservation);
            state.Characters[id] = new(id, data.Text("name", 100), content, expressions, defaultExpression) { ReservedAccountId = reservation };
            foreach (var revoked in state.Accounts.Values.Where(a => a.CharacterId == id && reservation != null && a.Id != reservation))
            {
                revoked.CharacterId = null;
                revoked.Position = null;
                if (revoked.RoomId is { } occupiedRoom && state.Rooms[occupiedRoom].SpeakerAccountId == revoked.Id)
                    state.Rooms[occupiedRoom].SpeakerAccountId = null;
            }
            foreach (var participant in state.Accounts.Values.Where(a => a.CharacterId == id && !expressions.Contains(a.Expression, StringComparer.Ordinal)))
                participant.Expression = defaultExpression;
            context.PrivateEvent("catalog.changed", new { characterId = id }, state.Accounts.Keys.ToArray());
            return state.Characters[id];
        }
        context.DemandSpeak();
        var room = context.Room;
        if (action == "chat.send")
        {
            var channel = data.Text("channel", 20, false);
            if (channel.Length == 0) channel = "ooc";
            if (channel is not "ooc" and not "event") throw new DomainException("invalid_channel", "Use the ooc or event channel");
            context.RoomEvent("chat.sent", new { accountId = account.Id, account.DisplayName, text = data.Text("text", 4000), channel });
            return new { sent = true };
        }
        if (action is "dialogue.clear" or "stage.clear")
        {
            var clearRoom = WorldQueries.CanDirect(account);
            if (action == "stage.clear") context.DemandDirector();
            if (!clearRoom && !account.Isolated && room.LastDialogue is { } current && current.AccountId != account.Id)
                throw new DomainException("forbidden", "You can only clear your own current dialogue");
            if (!clearRoom && (account.Isolated || room.LastDialogue?.AccountId != account.Id) && account.PrivateDialogue == null)
                return new { cleared = true };
            if (clearRoom || !account.Isolated && room.LastDialogue?.AccountId == account.Id)
            {
                room.LastDialogue = null;
                if (clearRoom || room.SpeakerAccountId == account.Id) room.SpeakerAccountId = null;
            }
            foreach (var participant in state.Accounts.Values.Where(a => a.RoomId == room.Id && (clearRoom || a.Id == account.Id)))
            {
                participant.PrivateDialogue = null;
                participant.PrivateDialogueAudience = [];
                if (action == "stage.clear") participant.Position = null;
            }
            context.StageEvent("dialogue.cleared", new { accountId = account.Id });
            if (action == "stage.clear") context.StageEvent("stage.changed");
            return new { cleared = true };
        }
        if (action == "character.select")
        {
            var id = data.Text("characterId", 100);
            if (!state.Characters.TryGetValue(id, out var definition)) throw new DomainException("character_not_permitted", "This character is not in the server catalog");
            DemandReservation(definition, account.Id);
            var expression = data.Text("expression", 100, false);
            if (expression.Length == 0) expression = definition.DefaultExpression;
            CheckExpression(definition, expression);
            account.CharacterId = id;
            account.Expression = expression;
            context.RoomEvent("character.selected", new { accountId = account.Id, characterId = id, expression });
            return new { characterId = id, expression };
        }
        if (account.CharacterId == null || !state.Characters.TryGetValue(account.CharacterId, out var character))
            throw new DomainException("character_required", "Select a permitted character first");
        DemandReservation(character, account.Id);
        var nextExpression = data.Text("expression", 100, false);
        if (nextExpression.Length > 0) { CheckExpression(character, nextExpression); account.Expression = nextExpression; }
        var position = data.Text("position", 20, false);
        if (position.Length == 0) position = account.Position ?? "center";
        if (position is not "left" and not "center" and not "right") throw new DomainException("invalid_position", "Position must be left, center, or right");
        account.ScaleX = data.Scale("scaleX", account.ScaleX);
        account.ScaleY = data.Scale("scaleY", account.ScaleY);
        var displaced = state.Accounts.Values.FirstOrDefault(a => a.Id != account.Id && a.RoomId == room.Id && a.Position == position &&
            !a.Isolated && !account.Isolated);
        if (displaced != null) displaced.Position = null;
        account.Position = position;
        if (action == "dialogue.send")
        {
            if (!account.Isolated) room.SpeakerAccountId = account.Id;
            var dialogue = DialogueValidation.Read(data);
            var sound = data.Text("sound", 200, false);
            var effect = data.Text("effect", 80, false);
            MediaCatalog.Require(state, sound, "sound", true);
            MediaCatalog.Require(state, effect, "effect", true);
            var line = new DialogueSnapshot(account.Id, account.DisplayName, character.Id, account.Expression,
                dialogue.Text, dialogue.Spans, DateTimeOffset.UtcNow, sound.Length == 0 ? null : sound, effect.Length == 0 ? null : effect);
            if (account.Isolated)
            {
                account.PrivateDialogue = line;
                account.PrivateDialogueAudience = WorldQueries.Audience(state, room.Id, account);
            }
            else room.LastDialogue = line;
            context.StageEvent("dialogue.accepted", new
            {
                accountId = account.Id,
                account.DisplayName,
                characterId = character.Id,
                expression = account.Expression,
                text = dialogue.Text,
                spans = dialogue.Spans,
                timestamp = line.Timestamp,
                sound,
                effect,
                displacedActorId = displaced == null ? null : "actor-" + displaced.Id
            });
            return new { spoken = true };
        }
        context.StageEvent("stage.changed", new { movedActorId = "actor-" + account.Id, displacedActorId = displaced == null ? null : "actor-" + displaced.Id });
        return new { position };
    }

    public static void DemandReservation(CharacterDefinition character, string accountId)
    {
        if (character.ReservedAccountId != null && character.ReservedAccountId != accountId)
            throw new DomainException("character_reserved", "This character is reserved for another account");
    }

    private static void CheckExpression(CharacterDefinition character, string expression)
    {
        if (!character.Expressions.Contains(expression, StringComparer.Ordinal))
            throw new DomainException("expression_not_permitted", "This expression is not in the permitted character definition");
    }
}
