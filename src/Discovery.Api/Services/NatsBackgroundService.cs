using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Background service que mantém as subscriptions NATS ativas.
/// Implementa retry automático e logging detalhado.
/// </summary>
public class NatsBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NatsBackgroundService> _logger;
    private const int MaxRetryAttempts = 5;
    private const int RetryDelayMs = 5000;

    public NatsBackgroundService(IServiceProvider services, ILogger<NatsBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NATS background service starting...");

        int retryCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var messaging = scope.ServiceProvider.GetRequiredService<IAgentMessaging>();

                _logger.LogInformation("Starting NATS subscriptions...");

                await messaging.SubscribeToAgentMessagesAsync(stoppingToken);

                retryCount = 0;
                // Se chegou aqui, as subscriptions foram canceladas
                _logger.LogInformation("NATS subscriptions ended.");
                await Task.Delay(RetryDelayMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("NATS background service stopping.");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogError(ex, "Error in NATS background service (Attempt {RetryCount}/{MaxRetries})", retryCount, MaxRetryAttempts);

                if (retryCount >= MaxRetryAttempts)
                {
                    _logger.LogError("Max retry attempts reached. Resetting counter...");
                    retryCount = 0;
                }

                await Task.Delay(RetryDelayMs, stoppingToken);
            }
        }

        _logger.LogInformation("NATS background service stopped.");
    }
}
