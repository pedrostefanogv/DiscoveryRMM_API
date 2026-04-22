namespace Discovery.Api.Services;

public sealed class RemoteDebugSessionCleanupService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly IRemoteDebugSessionManager _sessionManager;
    private readonly ILogger<RemoteDebugSessionCleanupService> _logger;

    public RemoteDebugSessionCleanupService(
        IRemoteDebugSessionManager sessionManager,
        ILogger<RemoteDebugSessionCleanupService> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cleaned = _sessionManager.CleanupExpiredSessions();
                if (cleaned > 0)
                    _logger.LogDebug("Remote debug cleanup processed {Count} sessions.", cleaned);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote debug cleanup failed.");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }
    }
}
