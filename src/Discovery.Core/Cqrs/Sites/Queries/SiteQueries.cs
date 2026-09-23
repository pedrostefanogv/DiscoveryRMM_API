using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Sites.Queries;

public sealed record GetSitesByClientQuery(Guid ClientId, bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<Site>>>;

/// <summary>
/// Lista global de sites (escopada pelas permissões do usuário) — evita o N+1
/// de buscar sites cliente a cliente na tela /sites.
/// </summary>
public sealed record GetAllSitesQuery(bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<Site>>>;
public sealed record GetSiteByIdQuery(Guid ClientId, Guid SiteId) : IQuery<Result<Site>>;
public sealed record GetSiteCustomFieldsQuery(Guid ClientId, Guid SiteId, bool IncludeSecrets = true) : IQuery<Result<IReadOnlyList<object>>>;
