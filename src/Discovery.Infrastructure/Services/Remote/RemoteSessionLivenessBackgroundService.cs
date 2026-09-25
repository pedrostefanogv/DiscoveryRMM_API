using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services.Remote.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote;

/// <summary>
/// Sweeper de sessoes remotas abandonadas. Substitui o antigo
/// RemoteSessionExpirationService acrescentando o stop no agent: sem isso a
/// captura/stream continuava rodando no processo depois de a sessao expirar no
/// banco (a tela ficava presa ate o viewer fechar o popup).
///
/// O agente tambem aplica viewer-timeout por ping/pong; este sweeper e o
/// backstop do lado servidor para quando o viewer some sem avisar.
/// </summary>
public class RemoteSessionLivenessBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RemoteSessionLivenessBackgroundService> _logger;
    private readonly RemoteAccessOptions _options;
    private readonly TimeSpan _sweepInterval;

    public RemoteSessionLivenessBackgroundService(
        IServiceProvider services,
        IOptions<RemoteAccessOptions> options,
        ILogger<RemoteSessionLivenessBackgroundService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;

        var seconds = _options.Liveness.SweepIntervalSeconds;
        if (seconds <= 0)
            seconds = _options.Nats.ExpirationCheckIntervalSeconds;
        if (seconds <= 0)
            seconds = 15;
        _sweepInterval = TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("RemoteAccess desabilitado — sweeper de liveness nao iniciado");
            return;
        }

        _logger.LogInformation(
            "RemoteSessionLivenessBackgroundService iniciado — intervalo {Seconds}s",
            _sweepInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_sweepInterval, stoppingToken);
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no sweeper de sessoes remotas");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRemoteSessionRepository>();
        var auditService = scope.ServiceProvider.GetRequiredService<RemoteSessionAuditService>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IAgentCommandDispatcher>();

        var now = DateTime.UtcNow;

        // Tolerancia: so encerra depois de KeepAliveTimeoutSeconds apos a
        // expiracao, cobrindo o atraso entre o ultimo ping e o /renew.
        var grace = Math.Max(0, _options.Liveness.KeepAliveTimeoutSeconds);
        var cutoff = now.AddSeconds(-grace);
        var expired = (await repo.GetExpiredAsync(cutoff, ct)).ToList();

        foreach (var session in expired)
        {
            // Auditoria ANTES de marcar como expired: RecordExpirationAsync
            // ignora sessões já com status "expired".
            await auditService.RecordExpirationAsync(session, ct);

            session.Status = "expired";
            session.ClosedAt = now;
            session.DurationSeconds = (int)(now - session.StartedAt).TotalSeconds;

            await repo.UpdateAsync(session, ct);
            await NotifyAgentStopAsync(dispatcher, session, ct);
        }

        if (expired.Count > 0)
            _logger.LogInformation("Encerradas {Count} sessoes remotas expiradas", expired.Count);
    }

    private async Task NotifyAgentStopAsync(
        IAgentCommandDispatcher dispatcher,
        RemoteSession session,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { action = "stop", sessionId = session.Id });
            await dispatcher.DispatchAsync(new AgentCommand
            {
                AgentId = session.AgentId,
                CommandType = CommandType.RemoteSessionStop,
                Payload = payload,
            }, ct);

            _logger.LogInformation(
                "Stop enviado ao agent para a sessao remota expirada {SessionId} (agent {AgentId})",
                session.Id, session.AgentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao notificar o agent do stop da sessao expirada {SessionId} (agent {AgentId})",
                session.Id, session.AgentId);
        }
    }
}
