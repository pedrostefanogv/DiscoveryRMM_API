using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.WorkflowProfiles.Commands;

namespace Discovery.Core.Cqrs.WorkflowProfiles.Queries;

public sealed record ListWorkflowProfilesQuery(Guid? ClientId, bool IncludeGlobal = true) : IQuery<Result<IReadOnlyList<WorkflowProfileDto>>>;
public sealed record GetWorkflowProfileByIdQuery(Guid Id) : IQuery<Result<WorkflowProfileDto>>;