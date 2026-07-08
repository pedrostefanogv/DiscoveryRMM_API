using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationTasks.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AutomationTasks;

public sealed class ListAutomationTasksQueryHandler : IRequestHandler<ListAutomationTasksQuery, Result<IReadOnlyList<AutomationTaskDto>>>
{
    public Task<Result<IReadOnlyList<AutomationTaskDto>>> Handle(ListAutomationTasksQuery q, CancellationToken ct)
    {
        return Task.FromResult(Result<IReadOnlyList<AutomationTaskDto>>.Success(Array.Empty<AutomationTaskDto>()));
    }
}

public sealed class GetAutomationTaskByIdQueryHandler : IRequestHandler<GetAutomationTaskByIdQuery, Result<AutomationTaskDto>>
{
    public Task<Result<AutomationTaskDto>> Handle(GetAutomationTaskByIdQuery q, CancellationToken ct)
    {
        return Task.FromResult(Result<AutomationTaskDto>.Failure(Error.NotFound($"Task {q.Id} not found")));
    }
}
