using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Queries;

namespace Discovery.Core.Cqrs.Agents.Crud.Queries;

public sealed record GetAgentByIdQuery(Guid Id) : IQuery<Result<AgentDto>>;
public sealed record GetAgentsBySiteQuery(Guid SiteId) : IQuery<Result<IReadOnlyList<AgentDto>>>;
public sealed record GetAgentsByClientQuery(Guid ClientId) : IQuery<Result<IReadOnlyList<AgentDto>>>;

public sealed record GetAgentCustomFieldsQuery(Guid AgentId, bool IncludeSecrets = false) : IQuery<Result<IReadOnlyList<CustomFieldValueDto>>>;
public sealed record UpsertAgentCustomFieldCommand(Guid AgentId, Guid DefinitionId, string ValueJson, string? UpdatedBy) : ICommand<Result<CustomFieldValueDto>>;
public sealed record CustomFieldValueDto(Guid DefinitionId, string Name, string Label, string ValueJson);