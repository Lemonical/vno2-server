namespace VNO2.Server.Transport;

public sealed class CommunityTimerService(CommunityHub hub, ILogger<CommunityTimerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await hub.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { ServerLog.TimerFailed(logger, exception.GetType().Name); }
        }
    }
}
