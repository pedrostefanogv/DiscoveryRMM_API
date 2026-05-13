using Discovery.Core.Entities;

namespace Discovery.Core.DTOs;

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