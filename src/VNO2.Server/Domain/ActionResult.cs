namespace VNO2.Server.Domain;

public sealed record ActionResult(object Result, IReadOnlyList<PendingEvent> Events,
    IReadOnlyList<string> DisconnectAccountIds);
