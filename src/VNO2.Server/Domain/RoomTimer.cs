namespace VNO2.Server.Domain;

public sealed record RoomTimer(string Id, string RoomId, string Label, DateTimeOffset EndsAt);
