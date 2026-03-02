using Meduza.Core.Interfaces;

namespace Meduza.Api.Services;

/// <summary>
/// Background service que mantém as subscriptions NATS ativas.
/// </summary>
public class NatsBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NatsBackgroundService> _logger;

    public NatsBackgroundService(IServiceProvider services, ILogger<NatsBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NATS background service starting...");

        using var scope = _services.CreateScope();
        var messaging = scope.ServiceProvider.GetRequiredService<IAgentMessaging>();

        try
        {
            await messaging.SubscribeToAgentMessagesAsync(stoppingToken);

            // Manter o serviço ativo enquanto não for cancelado
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("NATS background service stopping.");
        }
    }
}
