using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Discovery.Api.Services;

public class AgentLabelingReconciliationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AgentLabelingReconciliationBackgroundService> _logger;
    private readonly TimeSpan _activeInterval;
    private readonly TimeSpan _idleInterval;
    private readonly TimeSpan _startupDelay;

    public AgentLabelingReconciliationBackgroundService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<AgentLabelingReconciliationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        _activeInterval = TimeSpan.FromMinutes(Math.Max(10,
            configuration.GetValue<int?>("BackgroundJobs:AgentLabelingReconciliation:ActiveIntervalMinutes") ?? 10));
        _idleInterval = TimeSpan.FromMinutes(Math.Max((int)_activeInterval.TotalMinutes,
            configuration.GetValue<int?>("BackgroundJobs:AgentLabelingReconciliation:IdleIntervalMinutes") ?? 60));
        _startupDelay = TimeSpan.FromSeconds(Math.Max(0,
            configuration.GetValue<int?>("BackgroundJobs:AgentLabelingReconciliation:StartupDelaySeconds") ?? 30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(_startupDelay, stoppingToken);

        var nextDelay = _activeInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _serviceProvider.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IAgentAutoLabelingService>();

                if (!await service.HasEnabledRulesAsync(stoppingToken))
                {
                    nextDelay = _idleInterval;
                    _logger.LogDebug("Agent label reconciliation skipped because there are no enabled rules.");
                }
                else
                {
                    await service.ReprocessAllAgentsAsync("periodic-reconciliation", cancellationToken: stoppingToken);
                    nextDelay = _activeInterval;
                    _logger.LogInformation("Agent label reconciliation finished.");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                nextDelay = _activeInterval;
                _logger.LogError(ex, "Agent label reconciliation failed.");
            }

            await Task.Delay(nextDelay, stoppingToken);
        }
    }
}
