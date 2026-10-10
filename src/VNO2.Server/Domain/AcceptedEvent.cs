using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public sealed record AcceptedEvent(CommunityEvent Event, IReadOnlyList<string> Audience);
