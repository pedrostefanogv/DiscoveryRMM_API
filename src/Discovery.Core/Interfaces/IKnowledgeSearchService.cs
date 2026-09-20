using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>Parâmetros centralizados de busca na Knowledge Base.</summary>
/// <param name="Query">Texto da busca (linguagem natural ou palavras-chave).</param>
/// <param name="Mode">"semantic" | "keyword" | "hybrid" (semantic com fallback keyword).</param>
/// <param name="UseUserScope">true = ACL multi-escopo do usuário; false = escopo legado clientId/siteId.</param>
/// <param name="ClientId">Escopo legado (usado quando UseUserScope = false).</param>
/// <param name="SiteId">Escopo legado (usado quando UseUserScope = false).</param>
/// <param name="DepartmentId">Restringe artigos Internal ao departamento.</param>
/// <param name="MaxResults">Teto de resultados (1–50).</param>
public record KnowledgeSearchRequest(
    string Query,
    string Mode,
    bool UseUserScope,
    Guid? ClientId,
    Guid? SiteId,
    Guid? DepartmentId,
    int MaxResults);

/// <summary>Resultado unificado: artigo resolvido + chunk/score quando a origem é semântica.</summary>
public record KnowledgeSearchHit(
    KnowledgeArticle Article,
    KnowledgeChunkSearchResult? Chunk,
    double? Score,
    string Source); // "semantic" | "keyword"

/// <summary>
/// Busca unificada na KB: orquestra embeddings (semântico), keyword (ILIKE) e
/// o fallback do modo híbrido. Reaproveita a mesma lógica do KnowledgeMcpTool,
/// mas com ACL multi-escopo do usuário para os endpoints REST.
/// </summary>
public interface IKnowledgeSearchService
{
    /// <summary>Executa a busca no modo solicitado. Nunca lança para falha de IA — degrada para keyword no híbrido.</summary>
    Task<List<KnowledgeSearchHit>> SearchAsync(KnowledgeSearchRequest request, CancellationToken ct = default);
}
