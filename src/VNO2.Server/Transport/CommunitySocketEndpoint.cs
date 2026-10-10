using System.Net;
using System.Net.WebSockets;
using VNO2.Contracts.Community;
using VNO2.Server.Domain;
using VNO2.Server.Persistence;
using VNO2.Server.Security;

namespace VNO2.Server.Transport;

public sealed class CommunitySocketEndpoint(CommunityHub hub, CommunityStore store, JoinTicketVerifier verifier,
    CommunityOptions options, IHostEnvironment environment, ILogger<CommunitySocketEndpoint> logger) : IDisposable
{
    private readonly SemaphoreSlim admission = new(options.MaxConnections, options.MaxConnections);

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (!environment.IsDevelopment() && !context.Request.IsHttps)
        { context.Response.StatusCode = StatusCodes.Status426UpgradeRequired; return; }
        if (!await admission.WaitAsync(0, context.RequestAborted))
        { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var session = new SocketSession(socket, context.RequestAborted);
            var sender = session.SendLoopAsync();
            string? authenticationRequestId = null;
            try
            {
                using var authenticationDeadline = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation);
                authenticationDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                var authenticate = await WebSocketProtocol.ReceiveAsync(socket, authenticationDeadline.Token);
                authenticationRequestId = authenticate?.RequestId;
                if (authenticate?.Action != "authenticate") throw new DomainException("authentication_required", "Authenticate before sending community actions");
                CommunityIdentity? identity = null;
                var ticket = authenticate.Data.Text("ticket", 2048, false);
                var guestName = authenticate.Data.Text("guestName", 80, false);
                var guestToken = authenticate.Data.Text("guestToken", 200, false);
                if (ticket.Length > 0) identity = await verifier.VerifyAsync(ticket, authenticationDeadline.Token);
                else if (!environment.IsDevelopment() || !options.AllowGuestAuthentication)
                    throw new DomainException("authentication_required", "A valid account-bound master join ticket is required");
                else
                {
                    var remote = context.Connection.RemoteIpAddress;
                    if (remote == null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
                        throw new DomainException("guest_local_only", "Development guest authentication is restricted to loopback connections");
                    if (guestToken.Length == 0 && guestName.Length == 0)
                        throw new DomainException("invalid_guest", "Choose a guest display name");
                }
                var result = await store.AuthenticateAsync(identity, guestName, guestToken, authenticationDeadline.Token);
                await hub.InitializeAsync(session, result.Account.Id, result.Account.DisplayName, result.GuestToken, authenticate.RequestId, identity?.ServerCapacity);
                var requestCount = 0;
                var windowStart = DateTimeOffset.UtcNow;
                while (socket.State == WebSocketState.Open && !session.Cancellation.IsCancellationRequested)
                {
                    CommunityRequest? request = null;
                    try
                    {
                        if (DateTimeOffset.UtcNow - windowStart > TimeSpan.FromSeconds(10)) { requestCount = 0; windowStart = DateTimeOffset.UtcNow; }
                        if (++requestCount > 100) throw new DomainException("rate_limited", "Please wait before sending more actions");
                        request = await WebSocketProtocol.ReceiveAsync(socket, session.Cancellation);
                        if (request == null) break;
                        await hub.ExecuteAsync(session, request);
                    }
                    catch (DomainException exception)
                    {
                        session.Send(ProtocolJson.Message("error", request?.RequestId, 0, new CommunityError(exception.Code, exception.Message)));
                        if (exception.Code is "message_too_large" or "text_required" or "banned" or "rate_limited") break;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
                    {
                        session.Send(ProtocolJson.Message("error", request?.RequestId, 0, new CommunityError("invalid_request", "One or more request values are invalid")));
                    }
                }
            }
            catch (DomainException exception)
            {
                session.Send(ProtocolJson.Message("error", authenticationRequestId, 0, new CommunityError(exception.Code, exception.Message)));
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
            catch (Exception exception)
            {
                ServerLog.ConnectionFailed(logger, exception.GetType().Name);
                session.Send(ProtocolJson.Message("error", null, 0, new CommunityError("server_error", "The server could not complete this request")));
            }
            finally
            {
                hub.Remove(session);
                session.Complete();
                try
                {
                    await sender.WaitAsync(TimeSpan.FromSeconds(2));
                    await session.CloseOutputAsync();
                }
                catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or WebSocketException)
                { session.Disconnect(); }
            }
        }
        finally { admission.Release(); }
    }

    public void Dispose() => admission.Dispose();
}
