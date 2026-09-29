using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
        var labels = await svc.GetDistinctLabelsAsync(q.Limit, q.SourceType, ct);
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

/// <summary>Historico de mudancas de label do agente (auditoria de aplicacao/remocao).</summary>
public sealed class GetAgentLabelHistoryQueryHandler(ILabelService svc)
    : IRequestHandler<GetAgentLabelHistoryQuery, Result<IReadOnlyList<AgentLabelChangeLogDto>>>
{
    public async Task<Result<IReadOnlyList<AgentLabelChangeLogDto>>> Handle(GetAgentLabelHistoryQuery q, CancellationToken ct)
    {
        if (q.AgentId == Guid.Empty)
            return Result<IReadOnlyList<AgentLabelChangeLogDto>>.Failure(
                Error.Validation("agentId", "Agent ID is required."));

        var history = await svc.GetChangeLogAsync(q.AgentId, q.Limit, ct);
        return Result<IReadOnlyList<AgentLabelChangeLogDto>>.Success(history);
    }
}

public sealed class ReleaseAgentLabelSuppressionCommandHandler(ILabelService svc, IAgentAutoLabelingService autoLabeling)
    : IRequestHandler<ReleaseAgentLabelSuppressionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ReleaseAgentLabelSuppressionCommand cmd, CancellationToken ct)
    {
        var agentId = await svc.ReleaseSuppressionAsync(cmd.SuppressionId, ct);
        if (agentId is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Suppression {cmd.SuppressionId} not found"));

        // Reavalia o agente imediatamente: antes a label so voltava na reconciliacao
        // seguinte (ate 10 min), mesmo com o usuario pedindo a liberacao agora.
        try
        {
            await autoLabeling.EvaluateAgentAsync(
                agentId.Value,
                $"suppression-released:{cmd.SuppressionId}",
                "system",
                ct);
        }
        catch (Exception)
        {
            // Best-effort: a reconciliacao periodica tambem reaplica a label.
        }

        return Result<VoidResult>.Success(VoidResult.Value);
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
            r.ApplyMode.ToString(), r.LabelMatch.ToString(),
            AgentLabelExpressionJson.DeserializeOrDefault(r.ExpressionJson),
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
            rule.ApplyMode.ToString(), rule.LabelMatch.ToString(),
            AgentLabelExpressionJson.DeserializeOrDefault(rule.ExpressionJson),
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

        // Valida a ESTRUTURA (profundidade, nos, filhos, disco-em-grupo, regex): uma
        // previa com expressao fora dos limites consumia recursos sem necessidade.
        var expressionErrors = AgentLabelExpressionValidator.Validate(q.Request.Expression);
        if (expressionErrors.Count > 0)
            return Result<AgentLabelRuleDryRunResponse>.Failure(
                Error.Validation("expression", string.Join(" ", expressionErrors)));

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

/// <summary>
/// Previa em lote: valida a lista uma vez e avalia todos os agentes em uma unica
/// passagem no servidor, em vez de a UI fazer uma requisicao por agente.
/// </summary>
public sealed class DryRunLabelRuleBatchQueryHandler(IAgentAutoLabelingService svc)
    : IRequestHandler<DryRunLabelRuleBatchQuery, Result<IReadOnlyList<AgentLabelRuleDryRunResponse>>>
{
    private const int MaxAgentsPerRequest = 500;

    public async Task<Result<IReadOnlyList<AgentLabelRuleDryRunResponse>>> Handle(
        DryRunLabelRuleBatchQuery q,
        CancellationToken ct)
    {
        if (q.Request.Expression is null)
            return Result<IReadOnlyList<AgentLabelRuleDryRunResponse>>.Failure(
                Error.Validation("expression", "Expression is required."));

        var expressionErrors = AgentLabelExpressionValidator.Validate(q.Request.Expression);
        if (expressionErrors.Count > 0)
            return Result<IReadOnlyList<AgentLabelRuleDryRunResponse>>.Failure(
                Error.Validation("expression", string.Join(" ", expressionErrors)));

        if (q.Request.AgentIds is null || q.Request.AgentIds.Count == 0)
            return Result<IReadOnlyList<AgentLabelRuleDryRunResponse>>.Failure(
                Error.Validation("agentIds", "Informe ao menos um agente para a prévia."));

        if (q.Request.AgentIds.Count > MaxAgentsPerRequest)
            return Result<IReadOnlyList<AgentLabelRuleDryRunResponse>>.Failure(
                Error.Validation("agentIds", $"No máximo {MaxAgentsPerRequest} agentes por prévia."));

        try
        {
            var response = await svc.DryRunBatchAsync(q.Request, ct);
            return Result<IReadOnlyList<AgentLabelRuleDryRunResponse>>.Success(response);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<AgentLabelRuleDryRunResponse>>.Failure(
                Error.Internal($"Falha ao executar prévia em lote: {ex.Message}"));
        }
    }
}

/// <summary>Labels protegidas: nenhuma regra no modo Remover pode apaga-las.</summary>
public sealed class GetProtectedLabelsQueryHandler(ILabelService svc)
    : IRequestHandler<GetProtectedLabelsQuery, Result<IReadOnlyList<AgentLabelProtectedLabelDto>>>
{
    public async Task<Result<IReadOnlyList<AgentLabelProtectedLabelDto>>> Handle(GetProtectedLabelsQuery q, CancellationToken ct)
    {
        var labels = await svc.GetProtectedLabelsAsync(ct);
        return Result<IReadOnlyList<AgentLabelProtectedLabelDto>>.Success(labels);
    }
}

public sealed class AddProtectedLabelCommandHandler(ILabelService svc)
    : IRequestHandler<AddProtectedLabelCommand, Result<AgentLabelProtectedLabelDto>>
{
    public async Task<Result<AgentLabelProtectedLabelDto>> Handle(AddProtectedLabelCommand cmd, CancellationToken ct)
    {
        var label = cmd.Label?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(label))
            return Result<AgentLabelProtectedLabelDto>.Failure(
                Error.Validation("label", "Label is required."));

        if (label.Length > 120)
            return Result<AgentLabelProtectedLabelDto>.Failure(
                Error.Validation("label", "Label exceeds maximum length of 120."));

        try
        {
            var created = await svc.AddProtectedLabelAsync(label, cmd.CreatedBy, ct);
            return Result<AgentLabelProtectedLabelDto>.Success(created);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            // Corrida entre duas requisicoes: a outra inseriu a mesma label. Idempotente:
            // devolve o registro existente em vez de 500.
            var existing = (await svc.GetProtectedLabelsAsync(ct))
                .FirstOrDefault(item => string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase));

            return existing is not null
                ? Result<AgentLabelProtectedLabelDto>.Success(existing)
                : Result<AgentLabelProtectedLabelDto>.Failure(
                    Error.Conflict($"Protected label '{label}' already exists."));
        }
    }
}

public sealed class RemoveProtectedLabelCommandHandler(ILabelService svc)
    : IRequestHandler<RemoveProtectedLabelCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveProtectedLabelCommand cmd, CancellationToken ct)
    {
        var removed = await svc.RemoveProtectedLabelAsync(cmd.Id, ct);
        return removed
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Protected label {cmd.Id} not found"));
    }
}

/// <summary>Historico de versoes (configuracao) de uma regra de label.</summary>
public sealed class GetLabelRuleVersionsQueryHandler(ILabelService svc)
    : IRequestHandler<GetLabelRuleVersionsQuery, Result<IReadOnlyList<LabelRuleVersionDto>>>
{
    public async Task<Result<IReadOnlyList<LabelRuleVersionDto>>> Handle(GetLabelRuleVersionsQuery q, CancellationToken ct)
    {
        if (q.RuleId == Guid.Empty)
            return Result<IReadOnlyList<LabelRuleVersionDto>>.Failure(
                Error.Validation("ruleId", "Rule ID is required."));

        var versions = await svc.GetRuleVersionsAsync(q.RuleId, q.Limit, ct);
        return Result<IReadOnlyList<LabelRuleVersionDto>>.Success(versions);
    }
}

/// <summary>Exporta todas as regras no formato portavel de import/export.</summary>
public sealed class ExportLabelRulesQueryHandler(ILabelService svc)
    : IRequestHandler<ExportLabelRulesQuery, Result<IReadOnlyList<AgentLabelRuleExportDto>>>
{
    public async Task<Result<IReadOnlyList<AgentLabelRuleExportDto>>> Handle(ExportLabelRulesQuery q, CancellationToken ct)
    {
        var rules = await svc.GetRulesAsync(includeDisabled: true, ct);
        var dtos = rules.Select(rule => new AgentLabelRuleExportDto
        {
            Name = rule.Name,
            Label = rule.Label,
            Description = rule.Description,
            ApplyMode = rule.ApplyMode.ToString(),
            LabelMatch = rule.LabelMatch.ToString(),
            IsEnabled = rule.IsEnabled,
            Expression = AgentLabelExpressionJson.DeserializeOrDefault(rule.ExpressionJson)
        }).ToList().AsReadOnly();

        return Result<IReadOnlyList<AgentLabelRuleExportDto>>.Success(dtos);
    }
}
