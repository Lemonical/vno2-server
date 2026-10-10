namespace VNO2.Server;

public sealed class CommunityOptions
{
    public string Id { get; set; } = "local-community";
    public string Name { get; set; } = "VNO2 Community";
    public string DatabasePath { get; set; } = "data/community.db";
    public bool AllowGuestAuthentication { get; set; }
    public bool EconomyEnabled { get; set; } = true;
    public bool FreeformResourcesEnabled { get; set; }
    public string ServerCredential { get; set; } = "";
    public string MasterBaseUrl { get; set; } = "";
    public string[] AdministratorAccountIds { get; set; } = [];
    public long StartingCredits { get; set; } = 100;
    public string[] TrustedProxies { get; set; } = [];
    public int ReconnectGraceSeconds { get; set; } = 180;
    public int LocalCapacity { get; set; } = 64;
    public int MaxConnections { get; set; } = 512;
}
