using System.Text;

namespace Discovery.Core.Helpers;

/// <summary>
/// Helpers reutilizáveis para paginação por cursor (keyset).
/// Padrões suportados:
///   Type A — cursor composto por CreatedAt (ticks) + Id (Guid) → "ticks|guidN"
///   Type B — cursor por Id (Guid) → "guidN"
///   Type C — cursor composto por Name (string) + Id (Guid) → "name|guidN"
///   Type D — cursor composto por long (DownloadCount) + Id (Guid) → "count|guidN"
/// Todos codificados em Base64.
/// </summary>
public static class CursorPaginationHelper
{
    /// <summary>Codifica cursor Type A (CreatedAt desc + Id desc).</summary>
    public static string EncodeCreatedAtCursor(DateTime createdAtUtc, Guid id)
    {
        // Formato legado de LogsController — compatível
        var payload = $"{createdAtUtc.Ticks}|{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>Decodifica cursor Type A.</summary>
    public static bool TryDecodeCreatedAtCursor(string? cursor, out DateTime createdAtUtc, out Guid id)
    {
        createdAtUtc = default;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor)) return false;

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return false;
            if (!long.TryParse(parts[0], out var ticks)) return false;
            if (!Guid.TryParseExact(parts[1], "N", out var parsedId)) return false;

            createdAtUtc = new DateTime(ticks, DateTimeKind.Utc);
            id = parsedId;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Codifica cursor Type B (Id desc) como base64.</summary>
    public static string EncodeGuidCursor(Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(id.ToString("N")));

    /// <summary>Decodifica cursor Type B.</summary>
    public static bool TryDecodeGuidCursor(string? cursor, out Guid id)
    {
        id = default;

        if (string.IsNullOrWhiteSpace(cursor)) return false;

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return Guid.TryParseExact(raw, "N", out id);
        }
        catch
        {
            return false;
        }
    }

    // ── Type C: Name (string) + Id (Guid) — para Winget/AppPackage ──────────

    /// <summary>Codifica cursor Type C (Name asc + PackageId asc).</summary>
    public static string EncodeNameCursor(string name, Guid id)
    {
        var payload = $"{name}|{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>Decodifica cursor Type C.</summary>
    public static bool TryDecodeNameCursor(string? cursor, out string name, out Guid id)
    {
        name = string.Empty;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor)) return false;

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            // name pode conter |, então pegamos só o último |
            var lastPipe = raw.LastIndexOf('|');
            if (lastPipe < 0) return false;
            name = raw[..lastPipe];
            if (!Guid.TryParseExact(raw[(lastPipe + 1)..], "N", out id)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Aplica cláusula WHERE para cursor tipo Name + Id (Name asc, Id asc).</summary>
    public static IQueryable<T> ApplyNameCursor<T>(
        IQueryable<T> query,
        string? cursorName,
        Guid? cursorId,
        Func<T, string> nameSelector,
        Func<T, Guid> idSelector) where T : class
    {
        if (string.IsNullOrEmpty(cursorName) || !cursorId.HasValue)
            return query;

        var targetName = cursorName;
        var targetId = cursorId.Value;

        return query.Where(item =>
            string.Compare(nameSelector(item), targetName, StringComparison.OrdinalIgnoreCase) > 0 ||
            (string.Equals(nameSelector(item), targetName, StringComparison.OrdinalIgnoreCase) && idSelector(item).CompareTo(targetId) > 0));
    }

    // ── Type D: long (DownloadCount) + Id (Guid) — para Chocolatey ──────────

    /// <summary>Codifica cursor Type D (DownloadCount desc + PackageId asc).</summary>
    public static string EncodeLongCountCursor(long count, Guid id)
    {
        var payload = $"{count}|{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>Decodifica cursor Type D.</summary>
    public static bool TryDecodeLongCountCursor(string? cursor, out long count, out Guid id)
    {
        count = 0;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor)) return false;

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|', 2);
            if (parts.Length != 2) return false;
            if (!long.TryParse(parts[0], out count)) return false;
            if (!Guid.TryParseExact(parts[1], "N", out id)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Aplica cláusula WHERE para cursor tipo long + Id (long desc, Id asc).</summary>
    public static IQueryable<T> ApplyLongCountCursor<T>(
        IQueryable<T> query,
        long? cursorCount,
        Guid? cursorId,
        Func<T, long> countSelector,
        Func<T, Guid> idSelector) where T : class
    {
        if (!cursorCount.HasValue || !cursorId.HasValue)
            return query;

        var targetCount = cursorCount.Value;
        var targetId = cursorId.Value;

        return query.Where(item =>
            countSelector(item) < targetCount ||
            (countSelector(item) == targetCount && idSelector(item).CompareTo(targetId) > 0));
    }

    /// <summary>
    /// Aplica cláusula WHERE para cursor tipo CreatedAt + Id.
    /// </summary>
    public static IQueryable<T> ApplyCreatedAtCursor<T>(
        IQueryable<T> query,
        DateTime? cursorCreatedAtUtc,
        Guid? cursorId,
        Func<T, DateTime> createdAtSelector,
        Func<T, Guid> idSelector) where T : class
    {
        if (!cursorCreatedAtUtc.HasValue || !cursorId.HasValue)
            return query;

        var targetCreatedAt = cursorCreatedAtUtc.Value;
        var targetId = cursorId.Value;

        return query.Where(item =>
            createdAtSelector(item) < targetCreatedAt ||
            (createdAtSelector(item) == targetCreatedAt && idSelector(item).CompareTo(targetId) < 0));
    }

    /// <summary>
    /// Aplica cláusula WHERE para cursor tipo Id (Guid descendente).
    /// </summary>
    public static IQueryable<T> ApplyGuidCursor<T>(
        IQueryable<T> query,
        Guid? cursorId,
        Func<T, Guid> idSelector) where T : class
    {
        if (!cursorId.HasValue)
            return query;

        return query.Where(item => idSelector(item).CompareTo(cursorId.Value) < 0);
    }

    /// <summary>Retorna página com hasMore = items.Count > limit.</summary>
    public static (IReadOnlyList<T> Page, bool HasMore, T? LastItem) SlicePage<T>(IReadOnlyList<T> items, int limit)
    {
        var hasMore = items.Count > limit;
        var page = hasMore ? items.Take(limit).ToList() : items.ToList();
        var lastItem = hasMore ? page[^1] : default;
        return (page, hasMore, lastItem);
    }
}
