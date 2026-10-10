using System.Net.WebSockets;
using System.Text.Json;
using VNO2.Contracts.Community;
using VNO2.Server.Domain;

namespace VNO2.Server.Transport;

public static class WebSocketProtocol
{
    public const int MaximumMessageBytes = 65536;
    public const int MaximumServerMessageBytes = 1048576;

    public static async Task<CommunityRequest?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        using var fragmentDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fragments = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), fragmentDeadline.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new DomainException("text_required", "Only JSON text messages are supported");
            if (++fragments == 1) fragmentDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            if (fragments > 128 || message.Length + result.Count > MaximumMessageBytes)
                throw new DomainException("message_too_large", "Messages must not exceed 64 KiB or 128 fragments");
            message.Write(buffer.AsSpan(0, result.Count));
            if (result.EndOfMessage) break;
        }
        CommunityRequest? request;
        try { request = JsonSerializer.Deserialize<CommunityRequest>(message.GetBuffer().AsSpan(0, checked((int)message.Length)), ProtocolJson.Options); }
        catch (JsonException) { throw new DomainException("invalid_json", "The message is not valid protocol JSON"); }
        if (request == null || request.Version != 1) throw new DomainException("unsupported_version", "Protocol version 1 is required");
        if (string.IsNullOrEmpty(request.RequestId) || request.RequestId.Length > 80 || request.RequestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new DomainException("invalid_request_id", "Use a unique request ID of 1 to 80 letters, digits, hyphens, or underscores");
        if (string.IsNullOrEmpty(request.Action) || request.Action.Length > 64 || request.Action.Any(c => !char.IsAsciiLetterLower(c) && c != '.') || request.Data.ValueKind != JsonValueKind.Object)
            throw new DomainException("invalid_request", "A supported action and data object are required");
        return request;
    }
}
