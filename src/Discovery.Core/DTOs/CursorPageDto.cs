namespace Discovery.Core.DTOs;

/// <summary>
/// Contrato genérico de paginação por cursor (keyset) reutilizável em qualquer endpoint.
/// </summary>
public sealed record CursorPageDto<T>(
    IReadOnlyList<T> Items,
    int ReturnedItems,
    string? Cursor,
    string? NextCursor,
    bool HasMore,
    int Limit);
