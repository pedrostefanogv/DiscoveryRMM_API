using Discovery.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote;

/// <summary>
/// Serviço de expiração de sessões remotas (background hosted service).
/// Roda a cada 60s e encerra sessões expiradas.
/// </summary>
public class RemoteSessionExpirationService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RemoteSessionExpirationService> _logger;
    private readonly RemoteAccessOptions _options;

    public RemoteSessionExpirationService(
        IServiceProvider services,
        IOptions<RemoteAccessOptions> options,
        ILogger<RemoteSessionExpirationService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("RemoteAccess desabilitado — expiration service nao iniciado");
            return;
        }

        _logger.LogInformation("RemoteSessionExpirationService iniciado — intervalo 60s");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
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
