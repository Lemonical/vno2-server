namespace VNO2.Server.Domain;

public sealed record DirectorCue(string Id, string Name, string? Background, string? Music,
    string? Announcement, string? Image, string? Effect);
