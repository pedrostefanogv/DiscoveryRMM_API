using Discovery.Core.Entities;

namespace Discovery.Core.DTOs;

public sealed record LogFacetCountDto(
    string Key,
    int Count);

public sealed record LogScopeFacetCountDto(
    Guid Id,
    string? Name,
    int Count);

public sealed record LogCursorPageDto(
    IReadOnlyList<LogEntry> Items,
    int ReturnedItems,
    string? Cursor,
    string? NextCursor,
    bool HasMore,
    int Limit,
    string? Search,
    string? TraceId,
    string? CorrelationId,
    string? RequestPath,
    int? StatusCode,
    string? Period,
    DateTime? From,
    DateTime? To);

public sealed record LogSummaryDto(
    int Total,
    string? Search,
    string? TraceId,
    string? CorrelationId,
    string? RequestPath,
    int? StatusCode,
    string? Period,
    DateTime? From,
    DateTime? To,
    IReadOnlyList<LogFacetCountDto> Levels,
    IReadOnlyList<LogFacetCountDto> Sources,
    IReadOnlyList<LogFacetCountDto> Types,
    IReadOnlyList<LogScopeFacetCountDto> Clients,
    IReadOnlyList<LogScopeFacetCountDto> Sites,
    IReadOnlyList<LogScopeFacetCountDto> Agents);

public sealed record LogSummaryRawDto(
    int Total,
    IReadOnlyList<LogFacetCountDto> Levels,
    IReadOnlyList<LogFacetCountDto> Sources,
    IReadOnlyList<LogFacetCountDto> Types,
    IReadOnlyList<(Guid Id, int Count)> Clients,
    IReadOnlyList<(Guid Id, int Count)> Sites,
    IReadOnlyList<(Guid Id, int Count)> Agents);