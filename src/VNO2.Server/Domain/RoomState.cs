using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public sealed class RoomState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Capacity { get; set; } = 32;
    public bool Locked { get; set; }
    public string? PasswordHash { get; set; }
    public string? RequiredItemId { get; set; }
    public long EntryCost { get; set; }
    public long MinimumCredits { get; set; }
    public List<string> ReservedAccountIds { get; set; } = [];
    public DialogueSnapshot? LastDialogue { get; set; }
    public string? SpeakerAccountId { get; set; }
    public string? Background { get; set; }
    public string? Music { get; set; }
    public bool AllowPlayerMusic { get; set; }
}
