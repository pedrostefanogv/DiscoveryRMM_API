using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.MonitoringEvents.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.MonitoringEvents;

/// <summary>
/// Lista o histórico de eventos de monitoramento do agente.
///
/// Antes o handler era um placeholder que devolvia lista vazia: o endpoint
/// /monitoring-events existia, mas nunca retornava dados — o que também deixava
/// a tool MCP get_agent_metrics_history inútil. Agora consulta a tabela
/// agent_monitoring_events por escopo, com cursor e limite curto (máx. 200).
/// </summary>
public sealed class ListMonitoringEventsQueryHandler(IAgentMonitoringEventRepository repo)
    : IRequestHandler<ListMonitoringEventsQuery, Result<IReadOnlyList<MonitoringEventDto>>>
{
    private const int MaxLimit = 200;

    public async Task<Result<IReadOnlyList<MonitoringEventDto>>> Handle(
        ListMonitoringEventsQuery q, CancellationToken ct)
    {
        var limit = Math.Clamp(q.Limit, 1, MaxLimit);

        var events = await repo.GetByFiltersPageAsync(
            q.AgentId, q.ClientId, q.SiteId, q.Cursor, limit, ct);

        // O repositório busca limit + 1 para sinalizar "tem mais"; a resposta
        // expõe apenas o limite pedido.
        var page = events.Count > limit ? events.Take(limit).ToList() : events.ToList();

        var dtos = page
            .Select(e => new MonitoringEventDto(
                e.Id,
                e.AgentId,
                e.AlertCode,
                e.Severity.ToString(),
                e.Source.ToString(),
                e.CreatedAt,
                e.LabelsSnapshotJson))
            .ToList()
            .AsReadOnly();

        return Result<IReadOnlyList<MonitoringEventDto>>.Success(dtos);
    }
}
