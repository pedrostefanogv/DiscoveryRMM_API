using Meduza.Core.Interfaces;

namespace Meduza.Api.Services;

public class AgentLabelingReconciliationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AgentLabelingReconciliationBackgroundService> _logger;

    public AgentLabelingReconciliationBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<AgentLabelingReconciliationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IAgentAutoLabelingService>();
                await service.ReprocessAllAgentsAsync("periodic-reconciliation", stoppingToken);
                _logger.LogInformation("Agent label reconciliation finished.");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent label reconciliation failed.");
            }
        }
    }
}
