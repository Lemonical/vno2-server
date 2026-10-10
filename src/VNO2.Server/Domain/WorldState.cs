using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public sealed class WorldState
{
    public Dictionary<string, RoomState> Rooms { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, AccountState> Accounts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CharacterDefinition> Characters { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ItemDefinition> Items { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, MediaDefinition> Media { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DirectorCue> Cues { get; set; } = new(StringComparer.Ordinal);
    public List<RoomTimer> Timers { get; set; } = [];
}
