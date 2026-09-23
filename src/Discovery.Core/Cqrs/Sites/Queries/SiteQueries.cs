using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Sites.Queries;

public sealed record GetSitesByClientQuery(Guid ClientId, bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<Site>>>;

/// <summary>
/// Lista global de sites (escopada pelas permissões do usuário) — evita o N+1
/// de buscar sites cliente a cliente na tela /sites.
/// </summary>
public sealed record GetAllSitesQuery(bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<SiteWithClientDto>>>;

/// <summary>
/// Site com o nome/estado do cliente resolvido no servidor — evita que o
/// frontend dependa de outra consulta (e funcione para usuários sem Clients.View).
/// </summary>
public sealed record SiteWithClientDto(
    Guid Id,
    Guid ClientId,
    string Name,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? ClientName,
    bool ClientActive
);
public sealed record GetSiteByIdQuery(Guid ClientId, Guid SiteId) : IQuery<Result<Site>>;
public sealed record GetSiteCustomFieldsQuery(Guid ClientId, Guid SiteId, bool IncludeSecrets = true) : IQuery<Result<IReadOnlyList<object>>>;
