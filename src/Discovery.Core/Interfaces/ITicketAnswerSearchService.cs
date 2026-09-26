using Discovery.Core.Interfaces;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Busca nos chamados por resposta do questionário: semântica (pgvector) com
/// degradação automática para correspondência por texto.
/// </summary>
public interface ITicketAnswerSearchService
{
    Task<TicketAnswerSearchResult> SearchAsync(
        TicketAnswerSearchRequest request,
        CancellationToken ct = default);
}

public sealed record TicketAnswerSearchRequest(
    string Query,
    int Limit = 10,
    Guid? TemplateId = null,
    string? QuestionKey = null,
    double? MinSimilarity = null);

/// <summary>
/// <paramref name="Mode"/>: `semantic` (embeddings), `keyword` (degradação) ou
/// `disabled` (flag desligada e nada encontrado por texto).
/// </summary>
public sealed record TicketAnswerSearchResult(
    string Mode,
    IReadOnlyList<TicketAnswerSearchHit> Hits,
    long ElapsedMs);
