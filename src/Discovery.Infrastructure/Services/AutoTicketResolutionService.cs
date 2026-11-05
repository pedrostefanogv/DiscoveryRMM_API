using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Fecha o chamado correlacionado quando o sensor volta ao normal.
///
/// Fluxo: localiza o correlation lock pelo DedupKey da resolução → pega o
/// last_ticket_id → fecha o chamado no primeiro estado FINAL do workflow do
/// cliente.
///
/// Regras que evitam os dois erros clássicos:
///  - NÃO fechar por simples ausência de re-notificação (silêncio de agente não
///    é resolução; só o evento "resolved" explícito chega aqui).
///  - FECHAR mesmo dentro da janela de manutenção quando
///    CloseTicketResolvedDuringMaintenance está ligado — suprimir a ABERTURA é
///    aceitável, deixar o chamado aberto para sempre não é.
///
/// Best-effort: qualquer falha é logada e não desfaz a amostra persistida.
/// </summary>
public class AutoTicketResolutionService(
    DiscoveryDbContext db,
    IAutoTicketPolicyResolver policyResolver,
    ILogger<AutoTicketResolutionService> logger) : IAutoTicketResolutionService
{
    public async Task<Ticket?> CloseCorrelatedTicketAsync(
        AgentMonitoringEvent resolutionEvent,
        CancellationToken cancellationToken = default)
    {
        var policy = await policyResolver.ResolveAsync(
            resolutionEvent.ClientId, resolutionEvent.SiteId, cancellationToken);

        // O auto-ticket está desligado neste escopo: não há chamado nosso para fechar.
        if (!policy.Enabled)
        {
            logger.LogDebug(
                "Resolução ignorada: auto-ticket desabilitado ({Level}).",
                policy.DisabledByLevel ?? "Server");
            return null;
        }

        // Localiza o chamado correlacionado. A resolução chega com o MESMO alert
        // code do alerta (sensor:<key>), então o lock de correlação do alerta
        // original é o elo entre os dois.
        var ticketId = await FindCorrelatedTicketAsync(resolutionEvent, cancellationToken);
        if (ticketId is null)
        {
            logger.LogDebug(
                "Nenhum chamado correlacionado para a resolução do evento {MonitoringEventId}.",
                resolutionEvent.Id);
            return null;
        }

        var ticket = await db.Tickets
            .FirstOrDefaultAsync(t => t.Id == ticketId.Value, cancellationToken);

        if (ticket is null)
        {
            logger.LogWarning(
                "Correlation lock aponta para o chamado {TicketId}, que não existe mais.",
                ticketId.Value);
            return null;
        }

        // Já fechado: nada a fazer (idempotência).
        if (ticket.ClosedAt.HasValue)
        {
            logger.LogDebug("Chamado {TicketId} já estava fechado.", ticket.Id);
            return null;
        }

        var finalState = await ResolveFinalStateAsync(ticket.ClientId, cancellationToken);
        if (finalState is null)
        {
            logger.LogWarning(
                "Não há estado final de workflow para o cliente {ClientId}; chamado {TicketId} permanece aberto.",
                ticket.ClientId, ticket.Id);
            return null;
        }

        ticket.WorkflowStateId = finalState.Id;
        ticket.ClosedAt = DateTime.UtcNow;
        ticket.UpdatedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A tabela de tickets usa concorrência OTIMISTA (xmin). Se outra
            // requisição (lote duplicado/retry do mesmo agente) fechou o chamado
            // entre a leitura e a gravação, a escrita é rejeitada.
            //
            // Sem tratar aqui, a exceção subiria para o try/catch best-effort da
            // ingestão e o chamado ficaria ABERTO para sempre — exatamente o
            // "ticket órfão" que este serviço existe para evitar. O desfecho
            // correto é considerar a resolução ATENDIDA: alguém já fechou.
            logger.LogInformation(
                "Chamado {TicketId} foi fechado concorrentemente por outra requisição; resolução considerada atendida.",
                ticket.Id);
            return null;
        }

        logger.LogInformation(
            "Chamado {TicketId} fechado automaticamente pela resolução do sensor (estado final '{State}').",
            ticket.Id, finalState.Name);

        return ticket;
    }

    /// <summary>
    /// Encontra o chamado aberto correlacionado, usando o lock de correlação
    /// criado quando o alerta gerou o chamado.
    ///
    /// ATENÇÃO ao formato da chave (DedupFingerprintService.BuildDedupKey):
    ///   {clientIdN}:{siteN|none}:{agentIdN}:{alertCode}:{hash}:{timeBucket}
    ///
    /// O alert code NÃO está no início — está no 4º segmento. Por isso a busca é
    /// feita por segmentos (cliente, agente e alert code), e não por prefixo, que
    /// nunca casaria.
    /// </summary>
    private async Task<Guid?> FindCorrelatedTicketAsync(
        AgentMonitoringEvent resolutionEvent, CancellationToken ct)
    {
        var clientSegment = resolutionEvent.ClientId.ToString("N");
        var agentSegment = resolutionEvent.AgentId.ToString("N");
        var alertCodeSegment = resolutionEvent.AlertCode.Trim().ToLowerInvariant();

        // A janela do lock é curta, mas o chamado pode ter sido aberto horas
        // antes da resolução; usamos um horizonte generoso e filtramos por
        // LastTicketId presente.
        var since = DateTime.UtcNow.AddHours(-48);

        var candidates = await db.AlertCorrelationLocks.AsNoTracking()
            .Where(l => l.LastTicketId != null
                        && l.LastAlertAt >= since
                        && l.DedupKey.StartsWith(clientSegment + ":")
                        && l.DedupKey.Contains(":" + agentSegment + ":")
                        && l.DedupKey.Contains(":" + alertCodeSegment + ":"))
            .OrderByDescending(l => l.LastAlertAt)
            .Select(l => l.LastTicketId)
            .FirstOrDefaultAsync(ct);

        return candidates;
    }

    /// <summary>
    /// Primeiro estado FINAL do workflow do cliente; cai para um estado global
    /// (ClientId nulo) quando o cliente não tem workflow próprio.
    /// </summary>
    private async Task<WorkflowState?> ResolveFinalStateAsync(Guid clientId, CancellationToken ct)
    {
        return await db.WorkflowStates.AsNoTracking()
            .Where(s => s.IsFinal && s.ClientId == clientId)
            .OrderBy(s => s.SortOrder)
            .FirstOrDefaultAsync(ct)
            ?? await db.WorkflowStates.AsNoTracking()
                .Where(s => s.IsFinal && s.ClientId == null)
                .OrderBy(s => s.SortOrder)
                .FirstOrDefaultAsync(ct);
    }
}
