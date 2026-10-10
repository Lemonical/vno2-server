using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public sealed record CommandOutcome(CommunityMessage Reply, IReadOnlyList<AcceptedEvent> Events,
    IReadOnlyList<string> DisconnectAccountIds, bool Duplicate = false);
