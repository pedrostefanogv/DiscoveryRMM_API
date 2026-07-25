using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class SyncAutomationPolicyHandler(
    IAutomationTaskService taskService
) : IRequestHandler<SyncAutomationPolicyCommand, Result<object>>
{
    // Reutiliza a mesma instância de JsonSerializerOptions (thread-safe após configuração)
    // em vez de alocar uma nova a cada chamada de policy-sync (chamada a cada ~5 min por agent).
    private static readonly JsonSerializerOptions SyncJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<object>> Handle(SyncAutomationPolicyCommand cmd, CancellationToken ct)
    {
        // Desserializa o request vindo do agent (JsonElement → AgentAutomationPolicySyncRequest)
        var syncRequest = DeserializeSyncRequest(cmd.Request);

        var response = await taskService.SyncPolicyForAgentAsync(
            cmd.AgentId,
            syncRequest,
            cmd.Username,
            cmd.IpAddress,
            cmd.CorrelationId ?? Guid.NewGuid().ToString("N"),
            ct);

        return Result<object>.Success(response);
    }

    private static AgentAutomationPolicySyncRequest DeserializeSyncRequest(object? request)
    {
        if (request is JsonElement json)
        {
            return JsonSerializer.Deserialize<AgentAutomationPolicySyncRequest>(
                json.GetRawText(),
                SyncJsonOptions) ?? new AgentAutomationPolicySyncRequest();
        }

        return new AgentAutomationPolicySyncRequest();
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