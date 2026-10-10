using System.Text.Json;
using System.Text.Json.Serialization;
using VNO2.Contracts.Community;

namespace VNO2.Server;

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static CommunityMessage Message(string type, string? requestId, long sequence, object data) =>
        new(1, type, requestId, sequence, Element(data));
}
