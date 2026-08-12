using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentLabels;

public sealed class ListAgentLabelsQueryHandler(ILabelService svc)
    : IRequestHandler<ListAgentLabelsQuery, Result<IReadOnlyList<AgentLabelDto>>>
{
    public async Task<Result<IReadOnlyList<AgentLabelDto>>> Handle(ListAgentLabelsQuery q, CancellationToken ct)
    {
        if (!q.AgentId.HasValue)
            return Result<IReadOnlyList<AgentLabelDto>>.Failure(Error.Validation("agentId", "Agent ID is required."));

        var labels = await svc.GetByAgentIdAsync(q.AgentId.Value, ct);
        var dtos = labels.Select(l => new AgentLabelDto(l.Id, l.AgentId, l.Label, l.SourceType.ToString(), l.CreatedAt))
            .ToList().AsReadOnly();
        return Result<IReadOnlyList<AgentLabelDto>>.Success(dtos);
    }
}

public sealed class GetDistinctLabelsQueryHandler(ILabelService svc)
    : IRequestHandler<GetDistinctLabelsQuery, Result<IReadOnlyList<string>>>
{
    public async Task<Result<IReadOnlyList<string>>> Handle(GetDistinctLabelsQuery q, CancellationToken ct)
    {
        var labels = await svc.GetDistinctLabelsAsync(ct);
        return Result<IReadOnlyList<string>>.Success(labels);
    }
}

public sealed class RemoveAgentLabelCommandHandler(ILabelService svc)
    : IRequestHandler<RemoveAgentLabelCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveAgentLabelCommand cmd, CancellationToken ct)
    {
        await svc.DeleteAsync(cmd.LabelId, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ListLabelRulesQueryHandler(ILabelService svc)
    : IRequestHandler<ListLabelRulesQuery, Result<IReadOnlyList<LabelRuleDto>>>
{
    public async Task<Result<IReadOnlyList<LabelRuleDto>>> Handle(ListLabelRulesQuery q, CancellationToken ct)
    {
        var rules = await svc.GetRulesAsync(q.IncludeDisabled, ct);
        var dtos = rules.Select(r => new LabelRuleDto(
            r.Id, r.Name, r.Label, r.Description, r.IsEnabled,
            r.ApplyMode.ToString(), r.ExpressionJson,
            r.CreatedBy, r.CreatedAt, r.UpdatedAt
        )).ToList().AsReadOnly();
        return Result<IReadOnlyList<LabelRuleDto>>.Success(dtos);
    }
}

public sealed class GetLabelRuleByIdQueryHandler(ILabelService svc)
    : IRequestHandler<GetLabelRuleByIdQuery, Result<LabelRuleDto>>
{
    public async Task<Result<LabelRuleDto>> Handle(GetLabelRuleByIdQuery q, CancellationToken ct)
    {
        var rule = await svc.GetRuleByIdAsync(q.Id, ct);
        if (rule is null)
            return Result<LabelRuleDto>.Failure(Error.NotFound($"Label rule {q.Id} not found"));

        return Result<LabelRuleDto>.Success(new LabelRuleDto(
            rule.Id, rule.Name, rule.Label, rule.Description, rule.IsEnabled,
            rule.ApplyMode.ToString(), rule.ExpressionJson,
            rule.CreatedBy, rule.CreatedAt, rule.UpdatedAt));
    }
}

public sealed class GetAvailableCustomFieldsQueryHandler(ICustomFieldService svc)
    : IRequestHandler<GetAvailableCustomFieldsQuery, Result<IReadOnlyList<AvailableCustomFieldDto>>>
{
    public async Task<Result<IReadOnlyList<AvailableCustomFieldDto>>> Handle(GetAvailableCustomFieldsQuery q, CancellationToken ct)
    {
        var definitions = await svc.GetDefinitionsAsync(CustomFieldScopeType.Agent, includeInactive: false, ct);
        var dtos = definitions.Select(d => new AvailableCustomFieldDto(
            d.Id, d.Name, d.DataType.ToString(), d.Description
        )).ToList().AsReadOnly();
        return Result<IReadOnlyList<AvailableCustomFieldDto>>.Success(dtos);
    }
}

public sealed class ListAgentsByRuleQueryHandler(ILabelService svc)
    : IRequestHandler<ListAgentsByRuleQuery, Result<AgentLabelRuleAgentsResponse>>
{
    public async Task<Result<AgentLabelRuleAgentsResponse>> Handle(ListAgentsByRuleQuery q, CancellationToken ct)
    {
        var rule = await svc.GetRuleByIdAsync(q.RuleId, ct);
        if (rule is null)
            return Result<AgentLabelRuleAgentsResponse>.Failure(Error.NotFound($"Label rule {q.RuleId} not found"));

        var agents = await svc.GetAgentsByRuleIdAsync(q.RuleId, ct);
        return Result<AgentLabelRuleAgentsResponse>.Success(new AgentLabelRuleAgentsResponse
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            Label = rule.Label,
            Description = rule.Description,
            TotalAgents = agents.Count,
            Agents = agents
        });
    }
}

public sealed class DryRunLabelRuleQueryHandler(IAgentAutoLabelingService svc)
    : IRequestHandler<DryRunLabelRuleQuery, Result<AgentLabelRuleDryRunResponse>>
{
    public async Task<Result<AgentLabelRuleDryRunResponse>> Handle(DryRunLabelRuleQuery q, CancellationToken ct)
    {
        if (q.Request.AgentId == Guid.Empty)
            return Result<AgentLabelRuleDryRunResponse>.Failure(Error.Validation("agentId", "Agent ID is required."));

        if (q.Request.Expression is null)
            return Result<AgentLabelRuleDryRunResponse>.Failure(Error.Validation("expression", "Expression is required."));

        try
        {
            var response = await svc.DryRunAsync(q.Request, ct);
            return Result<AgentLabelRuleDryRunResponse>.Success(response);
        }
        catch (InvalidOperationException ex)
        {
            return Result<AgentLabelRuleDryRunResponse>.Failure(Error.NotFound(ex.Message));
        }
        catch (Exception ex)
        {
            return Result<AgentLabelRuleDryRunResponse>.Failure(Error.Internal($"Falha ao executar prévia da regra: {ex.Message}"));
        }
    }
}
