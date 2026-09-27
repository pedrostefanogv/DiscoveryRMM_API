using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Queries;

namespace Discovery.Core.Cqrs.Agents.Crud.Queries;

public sealed record GetAgentByIdQuery(Guid Id) : IQuery<Result<AgentDto>>;
public sealed record GetAgentsBySiteQuery(Guid SiteId) : IQuery<Result<IReadOnlyList<AgentDto>>>;
public sealed record GetAgentsByClientQuery(Guid ClientId) : IQuery<Result<IReadOnlyList<AgentDto>>>;

/// <summary>
/// Agentes na lixeira (soft-deleted), paginados e com clientId resolvido.
/// HasGlobalAccess/AllowedClientIds aplicam row-level security
/// (ScopeSource.AccessList no controller).
/// </summary>
public sealed record GetDeletedAgentsQuery(
    Guid? ClientId,
    Guid? SiteId,
    int Page = 1,
    int PageSize = 50,
    string? Search = null,
    bool HasGlobalAccess = true,
    IReadOnlyList<Guid>? AllowedClientIds = null) : IQuery<Result<DeletedAgentsPageDto>>;

public sealed record GetAgentCustomFieldsQuery(Guid AgentId, bool IncludeSecrets = false) : IQuery<Result<IReadOnlyList<CustomFieldValueDto>>>;
public sealed record UpsertAgentCustomFieldCommand(Guid AgentId, Guid DefinitionId, string ValueJson, string? UpdatedBy) : ICommand<Result<CustomFieldValueDto>>;
public sealed record CustomFieldValueDto(Guid DefinitionId, string Name, string Label, string ValueJson);
