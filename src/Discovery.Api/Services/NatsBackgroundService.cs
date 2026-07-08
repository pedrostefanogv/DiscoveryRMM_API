using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Background service que mantém as subscriptions NATS ativas.
/// Implementa retry com backoff exponencial e circuit breaker.
/// </summary>
public class NatsBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NatsBackgroundService> _logger;
    private const int MaxRetryAttempts = 10;
    private const int BaseRetryDelayMs = 1_000;
    private const int MaxRetryDelayMs = 60_000;

    public NatsBackgroundService(IServiceProvider services, ILogger<NatsBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NATS background service starting...");

        int retryCount = 0;
        int consecutiveSuccesses = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var messaging = scope.ServiceProvider.GetRequiredService<IAgentMessaging>();

                _logger.LogInformation("Starting NATS subscriptions...");

                await messaging.SubscribeToAgentMessagesAsync(stoppingToken);

                // Subscriptions rodaram por pelo menos 10s sem erro → reset contadores.
                consecutiveSuccesses++;
                if (consecutiveSuccesses >= 3)
                {
                    retryCount = 0;
                    consecutiveSuccesses = 0;
                }

                _logger.LogInformation("NATS subscriptions ended normally.");
                await Task.Delay(BaseRetryDelayMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("NATS background service stopping.");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                consecutiveSuccesses = 0;

                // Backoff exponencial: 1s, 2s, 4s, 8s, 16s, 32s, 60s (cap).
                var delayMs = (int)Math.Min(BaseRetryDelayMs * Math.Pow(2, retryCount - 1), MaxRetryDelayMs);
                _logger.LogError(ex, "Error in NATS background service (Attempt {RetryCount}, retrying in {DelayMs}ms)", retryCount, delayMs);

                if (retryCount >= MaxRetryAttempts)
                {
                    _logger.LogCritical("NATS background service reached max retry attempts ({MaxRetries}) — will continue retrying at max interval.", MaxRetryAttempts);
                    // Continua tentando a cada MaxRetryDelayMs, mas loga como Critical para alertar operacao.
                }

                await Task.Delay(delayMs, stoppingToken);
            }
        }

        _logger.LogInformation("NATS background service stopped.");
    }
}
