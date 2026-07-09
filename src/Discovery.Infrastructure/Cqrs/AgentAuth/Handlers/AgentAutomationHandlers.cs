using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class SyncAutomationPolicyHandler(
    IAutomationTaskRepository taskRepo
) : IRequestHandler<SyncAutomationPolicyCommand, Result<object>>
{
    public async Task<Result<object>> Handle(SyncAutomationPolicyCommand cmd, CancellationToken ct)
    {
        // Use GetListPageAsync to fetch active tasks for this agent
        var tasks = await taskRepo.GetListPageAsync(
            scopeType: null, scopeId: null,
            activeOnly: true, deletedOnly: false, includeDeleted: false,
            search: null, clientId: null, siteId: null, agentId: cmd.AgentId,
            scopeTypes: null, actionTypes: null,
            cursor: null, limit: 200);

        return Result<object>.Success(new
        {
            upToDate = true,
            policyFingerprint = Guid.NewGuid().ToString("N"),
            generatedAt = DateTime.UtcNow,
            taskCount = tasks.Count,
            tasks
        });
    }
}

public sealed class GetAgentCommandsHandler(
    IAutomationTaskRepository taskRepo
) : IRequestHandler<GetAgentCommandsQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentCommandsQuery q, CancellationToken ct)
    {
        var tasks = await taskRepo.GetListPageAsync(
            scopeType: null, scopeId: null,
            activeOnly: true, deletedOnly: false, includeDeleted: false,
            search: null, clientId: null, siteId: null, agentId: q.AgentId,
            scopeTypes: null, actionTypes: null,
            cursor: null, limit: q.Limit);

        return Result<object>.Success(new { commands = tasks });
    }
}

public sealed class AckAutomationExecutionHandler() : IRequestHandler<AckAutomationExecutionCommand, Result<VoidResult>>
{
    public Task<Result<VoidResult>> Handle(AckAutomationExecutionCommand cmd, CancellationToken ct)
    {
        return Task.FromResult(Result<VoidResult>.Success(VoidResult.Value));
    }
}

public sealed class CompleteAutomationExecutionHandler() : IRequestHandler<CompleteAutomationExecutionCommand, Result<VoidResult>>
{
    public Task<Result<VoidResult>> Handle(CompleteAutomationExecutionCommand cmd, CancellationToken ct)
    {
        return Task.FromResult(Result<VoidResult>.Success(VoidResult.Value));
    }
}