using System.Text.Json;
using Meduza.Api.Hubs;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;
using NATS.Client.Core;

namespace Meduza.Api.Services;

/// <summary>
/// Bridge que conecta eventos NATS ao SignalR para notificar o dashboard em tempo real.
/// Inscreve-se em tópicos NATS e repassa eventos para clientes SignalR conectados.
/// </summary>
public class NatsSignalRBridge : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly NatsConnection _natsConnection;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly ILogger<NatsSignalRBridge> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public NatsSignalRBridge(
        IServiceProvider services,
        NatsConnection natsConnection,
        IHubContext<AgentHub> hubContext,
        ILogger<NatsSignalRBridge> logger)
    {
        _services = services;
        _natsConnection = natsConnection;
        _hubContext = hubContext;
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

                // Inscrever em dashboard.events e repassar para SignalR
                await foreach (var msg in _natsConnection.SubscribeAsync<string>("dashboard.events", cancellationToken: stoppingToken))
                {
                    try
                    {
                        var eventData = JsonSerializer.Deserialize<DashboardEvent>(msg.Data ?? "", JsonOptions);
                        if (eventData is not null)
                        {
                            _logger.LogDebug("Dashboard event received: {EventType}", eventData.EventType);

                            // Repassar para todos os clientes do grupo dashboard
                            await _hubContext.Clients.Group("dashboard")
                                .SendAsync("DashboardEvent", eventData.EventType, eventData.Data, eventData.Timestamp, cancellationToken: stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing dashboard event from NATS");
                    }
                }

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

    private record DashboardEvent(string EventType, object Data, DateTime Timestamp);
}
