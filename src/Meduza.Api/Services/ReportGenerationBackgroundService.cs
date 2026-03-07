using Meduza.Core.Interfaces;

namespace Meduza.Api.Services;

public class ReportGenerationBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportGenerationBackgroundService> _logger;

    public ReportGenerationBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<ReportGenerationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var reportService = scope.ServiceProvider.GetRequiredService<IReportService>();
                var processed = await reportService.ProcessPendingAsync(10, stoppingToken);

                if (processed.Count > 0)
                {
                    _logger.LogInformation("Processed {Count} pending report executions.", processed.Count);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error while processing pending report executions.");
            }
        }
    }
}
