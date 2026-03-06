using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meduza.Api.Services;

/// <summary>
/// Serviço de background que monitora SLAs de tickets e marca violações.
/// Executa a cada 5 minutos por padrão.
/// </summary>
public class SlaMonitoringBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SlaMonitoringBackgroundService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);

    public SlaMonitoringBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<SlaMonitoringBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SLA Monitoring Background Service starting...");

        // Aguardar um pouco para que a aplicação inicialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSlaBreachesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during SLA breach check");
            }

            // Aguardar próximo ciclo
            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("SLA Monitoring Background Service stopped");
    }

    private async Task CheckSlaBreachesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        
        var ticketRepo = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
        var slaService = scope.ServiceProvider.GetRequiredService<ISlaService>();
        var activityLogService = scope.ServiceProvider.GetRequiredService<IActivityLogService>();

        try
        {
            // Obter todos os tickets abertos com SLA configurado
            var openTickets = await ticketRepo.GetOpenTicketsWithSlaAsync();

            if (openTickets == null || !openTickets.Any())
            {
                _logger.LogDebug("No open tickets with SLA to check");
                return;
            }

            _logger.LogInformation("Checking SLA for {Count} open tickets", openTickets.Count);

            foreach (var ticket in openTickets)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var breached = await slaService.CheckAndLogSlaBreachAsync(ticket.Id);

                if (breached)
                {
                    _logger.LogWarning("SLA Breached: Ticket {TicketId} - {Title}", ticket.Id, ticket.Title);
                }
                else
                {
                    // Verificar se está próximo de violar (80%)
                    var (_, percentUsed, _) = await slaService.GetSlaStatusAsync(ticket.Id);
                    
                    if (percentUsed >= 80 && percentUsed < 85)
                    {
                        // Registrar aviso de SLA
                        await activityLogService.LogActivityAsync(
                            ticket.Id,
                            TicketActivityType.SlaWarning,
                            null,
                            percentUsed.ToString("F2"),
                            "80",
                            "SLA warning: 20% time remaining"
                        );

                        _logger.LogWarning("SLA Warning: Ticket {TicketId} - Only {Percent}% time remaining", 
                            ticket.Id, (100 - percentUsed).ToString("F2"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CheckSlaBreachesAsync");
            throw;
        }
    }
}
