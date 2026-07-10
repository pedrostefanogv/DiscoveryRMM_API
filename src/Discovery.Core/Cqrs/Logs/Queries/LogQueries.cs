using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Logs.Queries;

/// <summary>
/// Query paginada de logs com suporte a filtros por nível, tipo, origem, período e busca textual.
/// Retorna <see cref="CursorPageDto{LogDto}"/> com paginação por cursor (keyset sobre CreatedAt DESC + Id DESC).
/// </summary>
public sealed record ListLogsQuery(
    string? AgentId = null,
    string? SiteId = null,
    string? ClientId = null,
    string? Cursor = null,
    int Limit = 50,
    int? Level = null,
    int? Type = null,
    int? Source = null,
    string? Period = null,
    string? Search = null
) : IQuery<Result<CursorPageDto<LogDto>>>;

public sealed record LogDto(
    Guid Id,
    string Level,
    string Type,
    string Source,
    string Message,
    DateTime CreatedAt,
    Guid? ClientId = null,
    Guid? SiteId = null,
    Guid? AgentId = null,
    string? DataJson = null
);

/// <summary>
/// Query de sumário agregado de logs (contagens por nível, tipo, origem, e escopos).
/// </summary>
public sealed record GetLogsSummaryQuery(
    string? AgentId = null,
    string? SiteId = null,
    string? ClientId = null,
    int? Level = null,
    int? Type = null,
    int? Source = null,
    string? Period = null,
    string? Search = null,
    int Limit = 50
) : IQuery<Result<LogSummaryDto>>;

/// <summary>
/// Query para obter opções de escopo disponíveis para filtro de logs.
/// Retorna clientes, sites e agentes que possuem registros de log, além das enumerações disponíveis.
/// </summary>
public sealed record GetLogsScopeOptionsQuery
    : IQuery<Result<LogsScopeOptionsDto>>;

public sealed record LogsScopeOptionsDto(
    IReadOnlyList<LogScopeOptionDto> Clients,
    IReadOnlyList<LogScopeOptionDto> Sites,
    IReadOnlyList<LogScopeOptionDto> Agents,
    IReadOnlyList<LogEnumOptionDto> Levels,
    IReadOnlyList<LogEnumOptionDto> Types,
    IReadOnlyList<LogEnumOptionDto> Sources
);

public sealed record LogScopeOptionDto(
    string Id,
    string? Name
);

public sealed record LogEnumOptionDto(
    int Value,
    string Label
);
