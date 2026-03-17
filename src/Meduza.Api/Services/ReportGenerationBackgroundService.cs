using Meduza.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Meduza.Api.Services;

public class ReportGenerationBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportGenerationBackgroundService> _logger;
    private readonly TimeSpan _activeInterval;
    private readonly TimeSpan _maxIdleInterval;
    private readonly TimeSpan _startupDelay;
    private readonly int _batchSize;

    public ReportGenerationBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ReportGenerationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _activeInterval = TimeSpan.FromSeconds(Math.Max(5,
            configuration.GetValue<int?>("BackgroundJobs:ReportGeneration:ActiveIntervalSeconds") ?? 15));
        _maxIdleInterval = TimeSpan.FromSeconds(Math.Max((int)_activeInterval.TotalSeconds,
            configuration.GetValue<int?>("BackgroundJobs:ReportGeneration:MaxIdleIntervalSeconds") ?? 600));
        _startupDelay = TimeSpan.FromSeconds(Math.Max(0,
            configuration.GetValue<int?>("BackgroundJobs:ReportGeneration:StartupDelaySeconds") ?? 10));
        _batchSize = Math.Max(1,
            configuration.GetValue<int?>("BackgroundJobs:ReportGeneration:BatchSize") ?? 10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(_startupDelay, stoppingToken);

        var currentDelay = _activeInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var reportService = scope.ServiceProvider.GetRequiredService<IReportService>();
                var processed = await reportService.ProcessPendingAsync(_batchSize, stoppingToken);

                if (processed.Count > 0)
                {
                    currentDelay = _activeInterval;
                    _logger.LogInformation("Processed {Count} pending report executions.", processed.Count);
                }
                else
                {
                    var nextDelaySeconds = Math.Min(currentDelay.TotalSeconds * 2, _maxIdleInterval.TotalSeconds);
                    currentDelay = TimeSpan.FromSeconds(nextDelaySeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                currentDelay = _activeInterval;
                _logger.LogError(ex, "Error while processing pending report executions.");
            }

            await Task.Delay(currentDelay, stoppingToken);
        }
    }
}
