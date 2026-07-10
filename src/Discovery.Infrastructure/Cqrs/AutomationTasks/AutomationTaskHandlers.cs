using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationTasks.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AutomationTasks;

public sealed class ListAutomationTasksQueryHandler(IAutomationTaskService svc) : IRequestHandler<ListAutomationTasksQuery, Result<CursorPageDto<AutomationTaskDto>>>
{
    public async Task<Result<CursorPageDto<AutomationTaskDto>>> Handle(ListAutomationTasksQuery q, CancellationToken ct)
    {
        // Mapeia clientId para os parâmetros do service
        Guid? scopeId = q.ClientId;
        AppApprovalScopeType? scopeType = q.ClientId.HasValue ? AppApprovalScopeType.Client : null;

        var page = await svc.GetListPageAsync(
            scopeType, scopeId,
            activeOnly: true, deletedOnly: false, includeDeleted: false,
            search: null,
            clientId: q.ClientId,
            siteId: null, agentId: null,
            scopeTypes: null, actionTypes: null, labels: null,
            q.Cursor, q.Limit, ct);

        var dtos = page.Items.Select(t => new AutomationTaskDto(t.Id, t.Name, t.Description, t.IsActive, t.LastUpdatedAt, t.LastUpdatedAt)).ToList();
        return Result<CursorPageDto<AutomationTaskDto>>.Success(
            new CursorPageDto<AutomationTaskDto>(dtos.AsReadOnly(), dtos.Count, page.Cursor, page.NextCursor, page.HasMore, page.Limit));
    }
}

public sealed class GetAutomationTaskByIdQueryHandler(IAutomationTaskService svc) : IRequestHandler<GetAutomationTaskByIdQuery, Result<AutomationTaskDto>>
{
    public async Task<Result<AutomationTaskDto>> Handle(GetAutomationTaskByIdQuery q, CancellationToken ct)
    {
        var t = await svc.GetByIdAsync(q.Id, false, ct);
        if (t is null) return Result<AutomationTaskDto>.Failure(Error.NotFound($"Task {q.Id} not found"));
        return Result<AutomationTaskDto>.Success(new AutomationTaskDto(t.Id, t.Name, t.Description, t.IsActive, t.CreatedAt, t.UpdatedAt));
    }
}
