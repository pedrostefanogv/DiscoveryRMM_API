using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class SyncAutomationPolicyHandler(
    IAutomationTaskRepository taskRepo,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo
) : IRequestHandler<SyncAutomationPolicyCommand, Result<object>>
{
    public async Task<Result<object>> Handle(SyncAutomationPolicyCommand cmd, CancellationToken ct)
    {
        // Resolve agent's hierarchical scope (SiteId + ClientId)
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        Guid? siteId = agent?.SiteId;
        Guid? clientId = null;
        if (siteId.HasValue)
        {
            var site = await siteRepo.GetByIdAsync(siteId.Value);
            clientId = site?.ClientId;
        }

        var tasks = await taskRepo.GetActiveTasksForAgentAsync(
            agentId: cmd.AgentId,
            agentSiteId: siteId,
            siteClientId: clientId,
            limit: 200);

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
    IAutomationTaskRepository taskRepo,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo
) : IRequestHandler<GetAgentCommandsQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentCommandsQuery q, CancellationToken ct)
    {
        // Resolve agent's hierarchical scope (SiteId + ClientId)
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        Guid? siteId = agent?.SiteId;
        Guid? clientId = null;
        if (siteId.HasValue)
        {
            var site = await siteRepo.GetByIdAsync(siteId.Value);
            clientId = site?.ClientId;
        }

        var tasks = await taskRepo.GetActiveTasksForAgentAsync(
            agentId: q.AgentId,
            agentSiteId: siteId,
            siteClientId: clientId,
            limit: q.Limit);

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