using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Knowledge.Queries;

/// <param name="Mode">"semantic" | "keyword" | "hybrid" (default hybrid — semântico com fallback keyword).</param>
public sealed record SearchKnowledgeQuery(string Query, Guid? ClientId, Guid? SiteId, int MaxResults = 10, Guid? DepartmentId = null, string Mode = "hybrid") : IQuery<Result<IReadOnlyList<ArticleResponse>>>;

/// <summary>Busca de sugestões na KB (POST /knowledge/chat-search) — chunks com score para IA/chat.</summary>
public sealed record SearchKbSuggestionsQuery(KbSearchRequest Request) : IQuery<Result<KbSuggestResult>>;

/// <summary>
/// Lista artigos com base na ACL do usuário (multi-escopo).
/// Quando clientId/siteId são informados, refinam o escopo (precisa ter acesso).
/// Sem filtros, retorna todos os artigos visíveis ao usuário.
/// </summary>
public sealed record ListKnowledgeArticlesByUserScopeQuery(
    string? Cursor = null,
    int Limit = 20,
    string? Status = null,
    Guid? DepartmentId = null,
    string? Category = null,
    Guid? ClientId = null,
    Guid? SiteId = null,
    string? SortBy = null,
    string? SortDirection = null) : IQuery<Result<CursorPageDto<ArticleListItem>>>;

public sealed record GetKnowledgeArticleByIdQuery(Guid Id) : IQuery<Result<ArticleResponse>>;
