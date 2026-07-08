using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Workflow.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Workflow.QueryHandlers;

public sealed class ListWorkflowStatesQueryHandler(IWorkflowRepository repo)
    : IRequestHandler<ListWorkflowStatesQuery, Result<List<WorkflowStateDto>>>
{
    public async Task<Result<List<WorkflowStateDto>>> Handle(ListWorkflowStatesQuery q, CancellationToken ct)
    {
        var states = await repo.GetStatesAsync(q.ClientId);
        var dtos = states.Select(s => new WorkflowStateDto(s.Id, s.Name, s.IsInitial, s.IsFinal, s.CreatedAt)).ToList();
        return Result<List<WorkflowStateDto>>.Success(dtos);
    }
}

public sealed class ListWorkflowProfilesQueryHandler(IWorkflowProfileRepository repo)
    : IRequestHandler<ListWorkflowProfilesQuery, Result<ListProfilesResult>>
{
    public async Task<Result<ListProfilesResult>> Handle(ListWorkflowProfilesQuery q, CancellationToken ct)
    {
        var profiles = q.DepartmentId.HasValue
            ? await repo.GetByDepartmentAsync(q.DepartmentId.Value)
            : await repo.GetGlobalAsync();
        var dtos = profiles.Select(p => new WorkflowProfileDto(p.Id, p.Name, p.DepartmentId, p.CreatedAt)).ToList();
        return Result<ListProfilesResult>.Success(new ListProfilesResult(dtos, null, false));
    }
}
