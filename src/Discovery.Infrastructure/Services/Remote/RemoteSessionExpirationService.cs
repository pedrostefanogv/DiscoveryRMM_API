using Discovery.Core.Configuration;
using Discovery.Infrastructure.Services.Remote.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote;

/// <summary>
/// Serviço de expiração de sessões remotas (background hosted service).
/// Intervalo configurável via RemoteAccess:Nats:ExpirationCheckIntervalSeconds (default 15s).
/// </summary>
public class RemoteSessionExpirationService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RemoteSessionExpirationService> _logger;
    private readonly RemoteAccessOptions _options;
    private readonly TimeSpan _checkInterval;

    public RemoteSessionExpirationService(
        IServiceProvider services,
        IOptions<RemoteAccessOptions> options,
        ILogger<RemoteSessionExpirationService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
        _checkInterval = _options.Nats.ExpirationCheckIntervalSeconds > 0
            ? TimeSpan.FromSeconds(_options.Nats.ExpirationCheckIntervalSeconds)
            : TimeSpan.FromSeconds(15);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("RemoteAccess desabilitado — expiration service nao iniciado");
            return;
        }

        _logger.LogInformation("RemoteSessionExpirationService iniciado — intervalo {Seconds}s", _checkInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
                await CleanupExpiredSessionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no RemoteSessionExpirationService");
            }
        }
    }

    private async Task CleanupExpiredSessionsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IRemoteSessionRepository>();
        var auditService = scope.ServiceProvider.GetRequiredService<RemoteSessionAuditService>();

        var now = DateTime.UtcNow;
        var expired = await repo.GetExpiredAsync(now, ct);

        foreach (var session in expired)
        {
            session.Status = "expired";
            session.ClosedAt = now;
            session.DurationSeconds = (int)(now - session.StartedAt).TotalSeconds;

            await repo.UpdateAsync(session, ct);
            await auditService.RecordExpirationAsync(session, ct);
        }

        if (expired.Any())
        {
            _logger.LogInformation("Encerradas {Count} sessoes remotas expiradas", expired.Count());
        }
    }
}
