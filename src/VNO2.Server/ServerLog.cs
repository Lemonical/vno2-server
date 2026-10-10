namespace VNO2.Server;

internal static partial class ServerLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Community connection failed ({ErrorType})")]
    public static partial void ConnectionFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Community timer processing failed ({ErrorType})")]
    public static partial void TimerFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Community master heartbeat failed ({Status})")]
    public static partial void HeartbeatFailed(ILogger logger, string status);
}
