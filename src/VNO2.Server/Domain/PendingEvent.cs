namespace VNO2.Server.Domain;

public sealed record PendingEvent(string Kind, string? RoomId, string? ActorAccountId,
    object Data, IReadOnlyList<string> Audience);
