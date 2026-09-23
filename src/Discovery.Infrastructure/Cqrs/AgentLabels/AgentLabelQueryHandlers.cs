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

/// <summary>
/// Labels de varios agentes em uma unica consulta. A UI da lista de agentes precisava
/// disso para filtrar por label sem disparar uma requisicao por agente.
/// </summary>
public sealed class ListAgentLabelsBatchQueryHandler(ILabelService svc)
    : IRequestHandler<ListAgentLabelsBatchQuery, Result<IReadOnlyList<AgentLabelDto>>>
{
    private const int MaxAgentsPerRequest = 500;

    public async Task<Result<IReadOnlyList<AgentLabelDto>>> Handle(ListAgentLabelsBatchQuery q, CancellationToken ct)
    {
        if (q.AgentIds.Count == 0)
            return Result<IReadOnlyList<AgentLabelDto>>.Success([]);

        if (q.AgentIds.Count > MaxAgentsPerRequest)
            return Result<IReadOnlyList<AgentLabelDto>>.Failure(
                Error.Validation("agentIds", $"No máximo {MaxAgentsPerRequest} agentes por consulta."));

        var labels = await svc.GetByAgentIdsAsync(q.AgentIds, ct);
        var dtos = labels
            .Select(l => new AgentLabelDto(l.Id, l.AgentId, l.Label, l.SourceType.ToString(), l.CreatedAt))
            .ToList()
            .AsReadOnly();

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

/// <summary>Labels com contagem de agentes. Permite montar o filtro sem carregar todas as labels.</summary>
public sealed class GetLabelUsageQueryHandler(ILabelService svc)
    : IRequestHandler<GetLabelUsageQuery, Result<IReadOnlyList<AgentLabelUsageDto>>>
{
    public async Task<Result<IReadOnlyList<AgentLabelUsageDto>>> Handle(GetLabelUsageQuery q, CancellationToken ct)
    {
        var usage = await svc.GetLabelUsageAsync(q.Limit, ct);
        return Result<IReadOnlyList<AgentLabelUsageDto>>.Success(usage);
    }
}

/// <summary>
/// Ids de agentes que possuem uma label, paginados por cursor. Substitui a necessidade
/// de trazer as labels de toda a frota para a UI filtrar (limite de 500 do lote).
/// </summary>
public sealed class GetAgentIdsByLabelQueryHandler(ILabelService svc)
    : IRequestHandler<GetAgentIdsByLabelQuery, Result<AgentIdsByLabelResponse>>
{
    public async Task<Result<AgentIdsByLabelResponse>> Handle(GetAgentIdsByLabelQuery q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q.Label))
            return Result<AgentIdsByLabelResponse>.Failure(Error.Validation("label", "Label is required."));

        var label = q.Label.Trim();
        var limit = Math.Clamp(q.Limit, 1, 1000);

        var total = await svc.CountAgentsByLabelAsync(label, ct);

        // Busca limit+1 para saber se ha proxima pagina sem uma segunda consulta.
        var ids = await svc.GetAgentIdsByLabelPagedAsync(label, q.AfterAgentId, limit + 1, ct);
        var hasMore = ids.Count > limit;
        var page = hasMore ? ids.Take(limit).ToList() : ids.ToList();

        return Result<AgentIdsByLabelResponse>.Success(new AgentIdsByLabelResponse
        {
            Label = label,
            Total = total,
            AgentIds = page,
            NextCursor = hasMore && page.Count > 0 ? page[^1] : null,
            HasMore = hasMore,
            Limit = limit
        });
    }
}

/// <summary>Supressoes de labels de um agente (label removida manualmente que o reconcile respeita).</summary>
public sealed class GetAgentLabelSuppressionsQueryHandler(ILabelService svc)
    : IRequestHandler<GetAgentLabelSuppressionsQuery, Result<IReadOnlyList<AgentLabelSuppressionDto>>>
{
    public async Task<Result<IReadOnlyList<AgentLabelSuppressionDto>>> Handle(GetAgentLabelSuppressionsQuery q, CancellationToken ct)
    {
        if (q.AgentId == Guid.Empty)
            return Result<IReadOnlyList<AgentLabelSuppressionDto>>.Failure(
                Error.Validation("agentId", "Agent ID is required."));

        var suppressions = await svc.GetSuppressionsByAgentIdAsync(q.AgentId, ct);
        return Result<IReadOnlyList<AgentLabelSuppressionDto>>.Success(suppressions);
    }
}

public sealed class ReleaseAgentLabelSuppressionCommandHandler(ILabelService svc)
    : IRequestHandler<ReleaseAgentLabelSuppressionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ReleaseAgentLabelSuppressionCommand cmd, CancellationToken ct)
    {
        var released = await svc.ReleaseSuppressionAsync(cmd.SuppressionId, ct);
        return released
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Suppression {cmd.SuppressionId} not found"));
    }
}

public sealed class RemoveAgentLabelCommandHandler(ILabelService svc)
    : IRequestHandler<RemoveAgentLabelCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveAgentLabelCommand cmd, CancellationToken ct)
    {
        // Antes o handler devolvia Success mesmo quando a label nao existia
        // (DELETE de id inexistente respondia 204 em vez de 404).
        // Agora tambem registra a supressao quando a label e automatica, para que a
        // remocao manual seja duradoura (o reconcile nao a recria).
        var deleted = await svc.DeleteWithSuppressionAsync(cmd.LabelId, cmd.SuppressedBy, ct);
        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Label {cmd.LabelId} not found"));
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

/// <summary>
/// Lista custom fields usaveis em regras nos escopos Agent, Site e Client.
/// Antes retornava apenas escopo Agent e no formato errado (fieldType/texto em vez
/// de dataType/numerico), o que quebrava a selecao de operadores na UI.
/// </summary>
public sealed class GetAvailableCustomFieldsQueryHandler(ICustomFieldService svc)
    : IRequestHandler<GetAvailableCustomFieldsQuery, Result<IReadOnlyList<AvailableCustomFieldDto>>>
{
    public async Task<Result<IReadOnlyList<AvailableCustomFieldDto>>> Handle(GetAvailableCustomFieldsQuery q, CancellationToken ct)
    {
        var definitions = await svc.GetDefinitionsAsync(scopeType: null, includeInactive: false, ct);

        var dtos = definitions
            .Where(d => d.ScopeType is CustomFieldScopeType.Agent or CustomFieldScopeType.Site or CustomFieldScopeType.Client)
            .OrderBy(d => d.ScopeType)
            .ThenBy(d => d.Label)
            .Select(d => new AvailableCustomFieldDto(
                d.Id,
                d.Name,
                string.IsNullOrWhiteSpace(d.Label) ? d.Name : d.Label,
                d.Description,
                (int)d.ScopeType,
                (int)d.DataType,
                ParseOptions(d.OptionsJson)))
            .ToList()
            .AsReadOnly();

        return Result<IReadOnlyList<AvailableCustomFieldDto>>.Success(dtos);
    }

    /// <summary>As opcoes vem como JSON (array de strings) ou CSV legado.</summary>
    private static IReadOnlyList<string> ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
            return [];

        var trimmed = optionsJson.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(trimmed);
                return parsed?.Where(option => !string.IsNullOrWhiteSpace(option)).ToList() ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }

        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        var safePage = q.Page < 1 ? 1 : q.Page;
        var safePageSize = Math.Clamp(q.PageSize, 1, 500);

        var (total, agents) = await svc.GetAgentsByRuleIdPagedAsync(q.RuleId, safePage, safePageSize, ct);

        return Result<AgentLabelRuleAgentsResponse>.Success(new AgentLabelRuleAgentsResponse
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            Label = rule.Label,
            Description = rule.Description,
            TotalAgents = total,
            Page = safePage,
            PageSize = safePageSize,
            Agents = agents
        });
    }
}

public sealed class EvaluateLabelRuleImpactQueryHandler(IAgentAutoLabelingService svc)
    : IRequestHandler<EvaluateLabelRuleImpactQuery, Result<AgentLabelRuleImpactResponse>>
{
    public async Task<Result<AgentLabelRuleImpactResponse>> Handle(EvaluateLabelRuleImpactQuery q, CancellationToken ct)
    {
        if (q.Request.Expression is null)
            return Result<AgentLabelRuleImpactResponse>.Failure(Error.Validation("expression", "Expression is required."));

        try
        {
            var response = await svc.EvaluateImpactAsync(q.Request, ct);
            return Result<AgentLabelRuleImpactResponse>.Success(response);
        }
        catch (Exception ex)
        {
            return Result<AgentLabelRuleImpactResponse>.Failure(Error.Internal($"Falha ao estimar o impacto da regra: {ex.Message}"));
        }
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
