using System.Text.Json;

namespace VNO2.Server.Domain;

public static class DirectorActions
{
    public static object Apply(ActionContext context, string action, JsonElement data)
    {
        if (action != "director.music" || !context.Room.AllowPlayerMusic) context.DemandDirector();
        context.DemandSpeak();
        var room = context.Room;
        if (context.Account.Isolated) throw new DomainException("isolated", "Director actions are unavailable while isolated");
        switch (action)
        {
            case "director.actor":
                var target = context.Target(data.Text("targetAccountId", 100));
                if (target.Banned || target.RoomId != room.Id || !WorldQueries.CanSee(context.Account, target))
                    throw new DomainException("invalid_target", "The actor is not available in this room");
                var characterId = data.Text("characterId", 100, false);
                if (characterId.Length == 0) characterId = target.CharacterId ?? "";
                if (!context.State.Characters.TryGetValue(characterId, out var character))
                    throw new DomainException("character_not_permitted", "Select a character in the server catalog");
                PerformanceActions.DemandReservation(character, target.Id);
                var expression = data.Text("expression", 100, false);
                if (expression.Length == 0) expression = character.DefaultExpression;
                if (!character.Expressions.Contains(expression, StringComparer.Ordinal))
                    throw new DomainException("expression_not_permitted", "Select an expression in the server catalog");
                var position = data.Text("position", 20, false);
                if (position.Length == 0) position = target.Position ?? "center";
                if (position is not "left" and not "center" and not "right")
                    throw new DomainException("invalid_position", "Position must be left, center, or right");
                var remove = data.Flag("remove");
                foreach (var displaced in context.State.Accounts.Values.Where(a => !remove && a.Id != target.Id && a.RoomId == room.Id && a.Position == position && !a.Isolated && !target.Isolated))
                    displaced.Position = null;
                target.CharacterId = characterId;
                target.Expression = expression;
                target.Position = remove ? null : position;
                target.ScaleX = data.Scale("scaleX", target.ScaleX);
                target.ScaleY = data.Scale("scaleY", target.ScaleY);
                if (data.Flag("speaker") && !target.Isolated) room.SpeakerAccountId = target.Id;
                if (target.Position == null && room.SpeakerAccountId == target.Id) room.SpeakerAccountId = null;
                context.StageEvent("stage.changed");
                return new { targetAccountId = target.Id, characterId, expression, position = target.Position };
            case "director.announce":
                context.RoomEvent("announcement", new { text = data.Text("text", 4000) });
                return new { sent = true };
            case "director.background":
                room.Background = MediaCatalog.Require(context.State, data.Text("reference", 100, false), "background", true);
                context.StageEvent("stage.changed");
                return new { room.Background };
            case "director.music":
                room.Music = MediaCatalog.Require(context.State, data.Text("reference", 100, false), "music", true);
                context.RoomEvent("music.changed", new { reference = room.Music, loop = data.Flag("loop", true) });
                return new { room.Music };
            case "director.image":
            case "director.effect":
                context.RoomEvent(action == "director.image" ? "image.shown" : "effect.played", new { reference = MediaCatalog.Require(context.State, data.Text("reference", 100), action == "director.image" ? "image" : "effect"), durationMilliseconds = data.Number("durationMilliseconds", 100, 60000, 3000) });
                return new { played = true };
            case "director.timer":
                if (context.State.Timers.Count(t => t.RoomId == room.Id) >= 20) throw new DomainException("limit_exceeded", "This room already has 20 timers");
                var timer = new RoomTimer(Guid.NewGuid().ToString("N"), room.Id, data.Text("label", 100), DateTimeOffset.UtcNow.AddSeconds(data.Number("seconds", 1, 86400, 60)));
                context.State.Timers.Add(timer);
                context.RoomEvent("timer.started", new { timerId = timer.Id, timer.Label, timer.EndsAt });
                return new { timerId = timer.Id, timer.EndsAt };
            case "director.timer.cancel":
                var id = data.Text("timerId", 100);
                var existing = context.State.Timers.FirstOrDefault(t => t.Id == id && t.RoomId == room.Id)
                    ?? throw new DomainException("not_found", "The timer does not exist in this room");
                context.State.Timers.Remove(existing);
                context.RoomEvent("timer.cancelled", new { timerId = id });
                return new { timerId = id };
            case "director.cue.save":
                var cueId = data.Text("cueId", 100);
                if (!context.State.Cues.ContainsKey(cueId) && context.State.Cues.Count >= 200) throw new DomainException("limit_exceeded", "Cue limit reached");
                var cue = new DirectorCue(cueId, data.Text("name", 100), MediaCatalog.Require(context.State, data.Text("background", 100, false), "background", true), MediaCatalog.Require(context.State, data.Text("music", 100, false), "music", true),
                    data.Text("announcement", 4000, false), MediaCatalog.Require(context.State, data.Text("image", 100, false), "image", true), MediaCatalog.Require(context.State, data.Text("effect", 100, false), "effect", true));
                context.State.Cues[cueId] = cue;
                return cue;
            case "director.cue.list":
                var offset = data.Number("offset", 0, 200);
                var limit = data.Number("limit", 1, 50, 20);
                return new { cues = context.State.Cues.Values.Skip(offset).Take(limit).ToArray(), total = context.State.Cues.Count };
            case "director.preview":
            case "director.cue.play":
                var selected = context.State.Cues.GetValueOrDefault(data.Text("cueId", 100))
                    ?? throw new DomainException("not_found", "The cue does not exist");
                MediaCatalog.Require(context.State, selected.Background, "background", true);
                MediaCatalog.Require(context.State, selected.Music, "music", true);
                MediaCatalog.Require(context.State, selected.Image, "image", true);
                MediaCatalog.Require(context.State, selected.Effect, "effect", true);
                if (action == "director.preview") return selected;
                if (selected.Background != null) room.Background = selected.Background;
                if (selected.Music != null) room.Music = selected.Music;
                context.StageEvent("cue.played", selected);
                return new { cueId = selected.Id };
            default: throw new DomainException("unknown_action", "The director action does not exist");
        }
    }

}
