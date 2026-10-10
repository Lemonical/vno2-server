using System.Text.Json;
using VNO2.Contracts.Community;
using VNO2.Server;

namespace VNO2.Tests;

public sealed class ProtocolJsonTests
{
    [Test]
    public async Task VersionOneBroadcastsOmitAbsentRequestIds()
    {
        var message = ProtocolJson.Message("error", null, 0, new CommunityError("server_error", "A controlled failure"));
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
        using var document = JsonDocument.Parse(json);

        await Assert.That(document.RootElement.TryGetProperty("requestId", out _)).IsFalse();
        await Assert.That(document.RootElement.GetProperty("version").GetInt32()).IsEqualTo(1);
        await Assert.That(document.RootElement.GetProperty("data").GetProperty("code").GetString()).IsEqualTo("server_error");
    }
}
