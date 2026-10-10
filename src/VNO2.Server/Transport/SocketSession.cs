using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using VNO2.Contracts.Community;

namespace VNO2.Server.Transport;

public sealed class SocketSession(WebSocket socket, CancellationToken connectionCancellation) : IDisposable
{
    private readonly Channel<CommunityMessage> outbound = Channel.CreateBounded<CommunityMessage>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation);
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string? AccountId { get; set; }
    public CancellationToken Cancellation => lifetime.Token;

    public bool Send(CommunityMessage message)
    {
        if (outbound.Writer.TryWrite(message)) return true;
        lifetime.Cancel();
        socket.Abort();
        return false;
    }

    public async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in outbound.Reader.ReadAllAsync(lifetime.Token))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
                if (bytes.Length > WebSocketProtocol.MaximumServerMessageBytes)
                {
                    bytes = JsonSerializer.SerializeToUtf8Bytes(ProtocolJson.Message("error", message.RequestId, message.Sequence,
                        new CommunityError("response_too_large", "The requested response exceeds the 1 MiB protocol limit")), ProtocolJson.Options);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        { lifetime.Cancel(); socket.Abort(); }
    }

    public void Complete() => outbound.Writer.TryComplete();

    public async Task CloseOutputAsync()
    {
        if (socket.State is not WebSocketState.Open and not WebSocketState.CloseReceived) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", timeout.Token);
    }

    public void Disconnect()
    {
        lifetime.Cancel();
        socket.Abort();
    }

    public void Dispose()
    {
        outbound.Writer.TryComplete();
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
