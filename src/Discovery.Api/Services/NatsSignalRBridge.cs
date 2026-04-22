using System.Text.Json;
using Discovery.Api.Hubs;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;
using NATS.Client.Core;

namespace Discovery.Api.Services;

/// <summary>
/// Bridge que conecta eventos NATS ao SignalR para notificar o dashboard em tempo real.
/// Inscreve-se em tópicos NATS e repassa eventos para clientes SignalR conectados.
/// </summary>
public class NatsSignalRBridge : BackgroundService
{
    private readonly NatsConnection _natsConnection;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly IRedisService _redisService;
    private readonly ILogger<NatsSignalRBridge> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public NatsSignalRBridge(
        NatsConnection natsConnection,
        IHubContext<AgentHub> hubContext,
        IRedisService redisService,
        ILogger<NatsSignalRBridge> logger)
    {
        _natsConnection = natsConnection;
        _hubContext = hubContext;
        _redisService = redisService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NATS-SignalR Bridge starting...");

        int retryCount = 0;
        const int maxRetries = 5;
        const int retryDelayMs = 5000;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting NATS dashboard bridge subscription...");

                var clientTask = RelayDashboardSubjectAsync("tenant.*.dashboard.events", stoppingToken);
                var siteTask = RelayDashboardSubjectAsync("tenant.*.site.*.dashboard.events", stoppingToken);

                await Task.WhenAll(clientTask, siteTask);

                retryCount = 0;
                _logger.LogInformation("NATS-SignalR bridge subscription ended");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("NATS-SignalR Bridge stopping.");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogError(ex, "Error in NATS-SignalR Bridge ({RetryCount}/{MaxRetries})", retryCount, maxRetries);

                if (retryCount >= maxRetries)
                {
                    _logger.LogError("Max retry attempts reached. Resetting...");
                    retryCount = 0;
                }

                await Task.Delay(retryDelayMs, stoppingToken);
            }
        }

        _logger.LogInformation("NATS-SignalR Bridge stopped.");
    }

    private async Task RelayDashboardSubjectAsync(string subjectPattern, CancellationToken stoppingToken)
    {
        await foreach (var msg in _natsConnection.SubscribeAsync<string>(subjectPattern, cancellationToken: stoppingToken))
        {
            try
            {
                var eventData = JsonSerializer.Deserialize<DashboardEventMessage>(msg.Data ?? "", JsonOptions);
                if (eventData is null)
                    continue;

                _logger.LogDebug("Dashboard event received on {Subject}: {EventType}", msg.Subject, eventData.EventType);
                await InvalidateDashboardCacheAsync(eventData.ClientId, eventData.SiteId);

                await _hubContext.Clients.Group(DashboardGroupNames.Global)
                    .SendAsync("DashboardEvent", eventData.EventType, eventData.Data, eventData.TimestampUtc, cancellationToken: stoppingToken);

                if (eventData.ClientId.HasValue)
                {
                    await _hubContext.Clients.Group(DashboardGroupNames.ForClient(eventData.ClientId.Value))
                        .SendAsync("DashboardEvent", eventData.EventType, eventData.Data, eventData.TimestampUtc, cancellationToken: stoppingToken);
                }

                if (eventData.SiteId.HasValue)
                {
                    await _hubContext.Clients.Group(DashboardGroupNames.ForSite(eventData.SiteId.Value))
                        .SendAsync("DashboardEvent", eventData.EventType, eventData.Data, eventData.TimestampUtc, cancellationToken: stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing dashboard event from NATS subject {Subject}", msg.Subject);
            }
        }
    }

    private async Task InvalidateDashboardCacheAsync(Guid? clientId, Guid? siteId)
    {
        foreach (var windowHours in DashboardCacheKeys.SupportedWindowHours)
        {
            await _redisService.DeleteAsync(DashboardCacheKeys.GlobalSummary(windowHours));

            if (clientId.HasValue)
            {
                await _redisService.DeleteAsync(DashboardCacheKeys.ClientSummary(clientId.Value, windowHours));
            }

            if (clientId.HasValue && siteId.HasValue)
            {
                await _redisService.DeleteAsync(DashboardCacheKeys.SiteSummary(clientId.Value, siteId.Value, windowHours));
            }
        }
    }
}
