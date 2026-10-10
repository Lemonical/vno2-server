using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public sealed class AccountState
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "member";
    public string? GuestTokenHash { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public string? RoomId { get; set; }
    public string? CharacterId { get; set; }
    public string Expression { get; set; } = "default";
    public string? Position { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public DialogueSnapshot? PrivateDialogue { get; set; }
    public string[] PrivateDialogueAudience { get; set; } = [];
    public bool Banned { get; set; }
    public bool Muted { get; set; }
    public bool Isolated { get; set; }
    public long Credits { get; set; }
    public int Hp { get; set; } = 100;
    public int MaxHp { get; set; } = 100;
    public int Mp { get; set; } = 100;
    public int MaxMp { get; set; } = 100;
    public Dictionary<string, int> Inventory { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTimeOffset> Cooldowns { get; set; } = new(StringComparer.Ordinal);
}
