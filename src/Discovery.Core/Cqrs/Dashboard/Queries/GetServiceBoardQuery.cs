using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Dashboard.Queries;

/// <summary>
/// Fila unificada do Service Board: tickets ativos, automações em falha/execução
/// recente, agentes com patches pendentes e alertas ativos — mesclados e ordenados
/// por prioridade (desc) e idade (desc), truncados em <c>Limit</c>.
/// </summary>
public sealed record GetServiceBoardQuery(
    Guid? ClientId,
    Guid? SiteId,
    int Limit = 50
) : IQuery<Result<ServiceBoardDto>>;

public sealed record ServiceBoardDto(
    DateTime GeneratedAtUtc,
    ServiceBoardCountsDto Counts,
    IReadOnlyList<ServiceBoardItemDto> Items);

public sealed record ServiceBoardCountsDto(int Ticket, int Automation, int Patch, int Alert);

public sealed record ServiceBoardItemDto(
    string Kind,
    Guid Id,
    string Title,
    string Status,
    string Priority,
    double AgeHours,
    Guid? ClientId,
    string? ClientName,
    Guid? SiteId,
    string DeepLink);
