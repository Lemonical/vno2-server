using System.Net.Http.Headers;
using System.Net.Http.Json;
using VNO2.Server.Transport;

namespace VNO2.Server.Security;

public sealed class MasterHeartbeatService(IHttpClientFactory clients, CommunityOptions options,
    CommunityHub hub, IHostEnvironment environment, ILogger<MasterHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(options.MasterBaseUrl, UriKind.Absolute, out var master) ||
            master.Scheme != "https" && !(environment.IsDevelopment() && master.IsLoopback && master.Scheme == "http") ||
            master.UserInfo.Length != 0 || master.Query.Length != 0 || master.Fragment.Length != 0 ||
            !Guid.TryParse(options.Id, out var serverId) || options.ServerCredential.Length == 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(master, $"/api/v1/servers/{serverId:D}/heartbeat"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServerCredential);
                request.Content = JsonContent.Create(new { onlineUsers = hub.OnlineUsers }, options: ProtocolJson.Options);
                using var response = await clients.CreateClient("MasterHeartbeat").SendAsync(request, stoppingToken);
                if (!response.IsSuccessStatusCode) ServerLog.HeartbeatFailed(logger, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            { ServerLog.HeartbeatFailed(logger, exception.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
