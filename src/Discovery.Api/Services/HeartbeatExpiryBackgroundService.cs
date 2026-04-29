using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Discovery.Api.Services;

/// <summary>
/// Detecta agentes cujo heartbeat expirou no Redis e os marca como Offline no DB.
/// 
/// Estratégia:
/// - Busca agentes com Status=Online no DB (batch pequeno, só Online)
/// - Verifica se cada um tem chave heartbeat:agent:{id} no Redis
/// - Se não tem, faz transição Online→Offline (escreve Offline no DB + dashboard event)
/// - Roda a cada 30s — custo baixíssimo (só lê agentes Online, não todos)
/// </summary>
public class HeartbeatExpiryBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeartbeatExpiryBackgroundService> _logger;
    private static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromSeconds(30);

    public HeartbeatExpiryBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<HeartbeatExpiryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HeartbeatExpiryBackgroundService iniciado (intervalo: {Interval}s)", ExpiryCheckInterval.TotalSeconds);

        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DetectExpiredHeartbeatsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao detectar heartbeats expirados");
            }

            await Task.Delay(ExpiryCheckInterval, stoppingToken);
        }

        _logger.LogInformation("HeartbeatExpiryBackgroundService encerrado.");
    }

    private async Task DetectExpiredHeartbeatsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var heartbeatCache = scope.ServiceProvider.GetRequiredService<IHeartbeatCacheService>();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var messaging = scope.ServiceProvider.GetRequiredService<IAgentMessaging>();
        var siteRepo = scope.ServiceProvider.GetRequiredService<ISiteRepository>();

        var onlineAgents = await agentRepo.GetOnlineAsync(ct);
        if (onlineAgents.Count == 0) return;

        var onlineIds = onlineAgents.Select(a => a.Id).ToList();

        var expired = await heartbeatCache.DetectExpiredAsync(onlineIds, ct);
        if (expired.Count == 0) return;

        _logger.LogDebug("Detectados {Count} agentes com heartbeat expirado — marcando Offline", expired.Count);

        foreach (var agentId in expired)
        {
            try
            {
                var agent = onlineAgents.First(a => a.Id == agentId);
                await agentRepo.UpdateStatusAsync(agentId, AgentStatus.Offline, null);

                if (messaging.IsConnected)
                {
                    Guid? clientId = null;
                    if (agent.SiteId != Guid.Empty)
                    {
                        var site = await siteRepo.GetByIdAsync(agent.SiteId);
                        clientId = site?.ClientId;
                    }

                    await messaging.PublishDashboardEventAsync(
                        DashboardEventMessage.Create("AgentStatusChanged",
                            new { agentId = agentId, status = "Offline" }, clientId, agent.SiteId), ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao marcar {AgentId} como Offline", agentId);
            }
        }
    }
}
