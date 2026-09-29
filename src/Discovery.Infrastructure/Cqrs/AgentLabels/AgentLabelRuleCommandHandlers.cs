using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentLabels;

/// <summary>
/// Helpers compartilhados de validacao das regras de label. Antes o
/// AgentLabelExpressionValidator existia mas NUNCA era chamado em producao —
/// apenas pelos testes — de modo que expressoes invalidas podiam ser persistidas
/// via API direta.
/// </summary>
internal static class LabelRuleValidation
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<Result<AgentLabelRuleExpressionNodeDto>> ParseAndValidateExpressionAsync(
        string? expressionJson,
        ICustomFieldService customFieldService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expressionJson))
            return Result<AgentLabelRuleExpressionNodeDto>.Failure(
                Error.Validation("expression", "Expression is required."));

        AgentLabelRuleExpressionNodeDto? expression;
        try
        {
            expression = JsonSerializer.Deserialize<AgentLabelRuleExpressionNodeDto>(expressionJson, JsonOptions);
        }
        catch (JsonException)
        {
            return Result<AgentLabelRuleExpressionNodeDto>.Failure(
                Error.Validation("expression", "Expression is not valid JSON."));
        }

        if (expression is null)
            return Result<AgentLabelRuleExpressionNodeDto>.Failure(
                Error.Validation("expression", "Expression is required."));

        var customFieldTypes = await LoadCustomFieldTypesAsync(expression, customFieldService, ct);
        var errors = AgentLabelExpressionValidator.Validate(expression, customFieldTypes);

        if (errors.Count > 0)
        {
            return Result<AgentLabelRuleExpressionNodeDto>.Failure(
                Error.Validation("expression", string.Join(" ", errors)));
        }

        return Result<AgentLabelRuleExpressionNodeDto>.Success(expression);
    }

    /// <summary>Resolve DefinitionId -> DataType para os custom fields referenciados.</summary>
    private static async Task<IReadOnlyDictionary<Guid, CustomFieldDataType>?> LoadCustomFieldTypesAsync(
        AgentLabelRuleExpressionNodeDto expression,
        ICustomFieldService customFieldService,
        CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        CollectCustomFieldIds(expression, ids);
        if (ids.Count == 0)
            return null;

        var definitions = await customFieldService.GetDefinitionsAsync(scopeType: null, includeInactive: false, ct);
        return definitions
            .Where(definition => ids.Contains(definition.Id))
            .ToDictionary(definition => definition.Id, definition => definition.DataType);
    }

    private static void CollectCustomFieldIds(AgentLabelRuleExpressionNodeDto node, HashSet<Guid> ids)
    {
        if (node.NodeType == AgentLabelNodeType.Condition
            && node.Field is AgentLabelField.AgentCustomField
                or AgentLabelField.ClientCustomField
                or AgentLabelField.SiteCustomField
            && node.CustomFieldDefinitionId.HasValue)
        {
            ids.Add(node.CustomFieldDefinitionId.Value);
        }

        foreach (var child in node.Children)
            CollectCustomFieldIds(child, ids);
    }

    /// <summary>
    /// ApplyMode invalido antes caia silenciosamente em ApplyAndRemove — o modo mais
    /// destrutivo. Agora e erro de validacao explicito.
    /// </summary>
    public static Result<AgentLabelApplyMode> ParseApplyMode(string? applyMode, AgentLabelApplyMode fallback)
    {
        if (string.IsNullOrWhiteSpace(applyMode))
            return Result<AgentLabelApplyMode>.Success(fallback);

        return Enum.TryParse<AgentLabelApplyMode>(applyMode, ignoreCase: true, out var mode)
            ? Result<AgentLabelApplyMode>.Success(mode)
            : Result<AgentLabelApplyMode>.Failure(
                Error.Validation("applyMode", $"ApplyMode '{applyMode}' is not valid."));
    }

    public static Result<string> ValidateIdentity(string? name, string? label, bool requireName, bool requireLabel)
    {
        if (requireName)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Result<string>.Failure(Error.Validation("name", "Name is required."));

            if (name.Trim().Length > 200)
                return Result<string>.Failure(Error.Validation("name", "Name exceeds maximum length of 200."));
        }

        if (requireLabel)
        {
            if (string.IsNullOrWhiteSpace(label))
                return Result<string>.Failure(Error.Validation("label", "Label is required."));

            // Coluna e varchar(120): validar aqui evita erro de banco.
            if (label.Trim().Length > 120)
                return Result<string>.Failure(Error.Validation("label", "Label exceeds maximum length of 120."));
        }

        return Result<string>.Success("ok");
    }
}

/// <summary>
/// Dispara a reconciliacao COMPLETA apos uma escrita de regra.
///
/// Necessario porque a reconciliacao periodica passou a ser incremental: uma regra
/// nova/alterada pode casar com agentes cujos dados nao mudaram, e esses nao seriam
/// selecionados pelo watermark. `coalesce: false` garante uma passagem posterior a
/// escrita mesmo com outro job em andamento. Best-effort.
/// </summary>
internal static class LabelRuleReprocessTrigger
{
    public static async Task EnqueueFullAsync(ILabelReprocessQueue queue, CancellationToken ct)
    {
        try
        {
            await queue.EnqueueAsync(actor: "system", ct, coalesce: false);
        }
        catch (Exception)
        {
            // A reconciliacao periodica tambem aplica a mudanca.
        }
    }
}

public sealed class CreateLabelRuleCommandHandler(ILabelService svc, ICustomFieldService customFieldService, ILabelReprocessQueue queue)
    : IRequestHandler<CreateLabelRuleCommand, Result<LabelRuleDto>>
{
    public async Task<Result<LabelRuleDto>> Handle(CreateLabelRuleCommand cmd, CancellationToken ct)
    {
        var identity = LabelRuleValidation.ValidateIdentity(cmd.Name, cmd.Label, requireName: true, requireLabel: true);
        if (!identity.IsSuccess)
            return Result<LabelRuleDto>.Failure(identity.Errors[0]);

        var applyMode = LabelRuleValidation.ParseApplyMode(cmd.ApplyMode, AgentLabelApplyMode.ApplyAndRemove);
        if (!applyMode.IsSuccess)
            return Result<LabelRuleDto>.Failure(applyMode.Errors[0]);

        // Regras em modo Manual nao possuem expressao avaliavel: exigir uma arvore
        // valida (grupo com pelo menos um filho) tornava impossivel criar a regra
        // pela UI, que envia um grupo vazio de proposito.
        var expressionJson = "{}";
        if (applyMode.Value != AgentLabelApplyMode.Manual)
        {
            var expression = await LabelRuleValidation.ParseAndValidateExpressionAsync(cmd.ExpressionJson, customFieldService, ct);
            if (!expression.IsSuccess)
                return Result<LabelRuleDto>.Failure(expression.Errors[0]);

            expressionJson = AgentLabelExpressionJson.Serialize(expression.Value);
        }

        var rule = new AgentLabelRule
        {
            Id = Guid.NewGuid(),
            Name = cmd.Name.Trim(),
            Label = cmd.Label.Trim(),
            Description = cmd.Description,
            IsEnabled = cmd.IsEnabled,
            ApplyMode = applyMode.Value,
            ExpressionJson = expressionJson,
            CreatedBy = cmd.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await svc.CreateRuleAsync(rule, ct);
        await LabelRuleReprocessTrigger.EnqueueFullAsync(queue, ct);
        return Result<LabelRuleDto>.Success(ToDto(created));
    }

    internal static LabelRuleDto ToDto(AgentLabelRule r) => new(
        r.Id, r.Name, r.Label, r.Description, r.IsEnabled,
        r.ApplyMode.ToString(), AgentLabelExpressionJson.DeserializeOrDefault(r.ExpressionJson),
        r.CreatedBy, r.CreatedAt, r.UpdatedAt);
}

public sealed class UpdateLabelRuleCommandHandler(ILabelService svc, ICustomFieldService customFieldService, ILabelReprocessQueue queue)
    : IRequestHandler<UpdateLabelRuleCommand, Result<LabelRuleDto>>
{
    public async Task<Result<LabelRuleDto>> Handle(UpdateLabelRuleCommand cmd, CancellationToken ct)
    {
        var existing = await svc.GetRuleByIdAsync(cmd.Id, ct);
        if (existing is null)
            return Result<LabelRuleDto>.Failure(Error.NotFound($"Label rule {cmd.Id} not found"));

        var identity = LabelRuleValidation.ValidateIdentity(cmd.Name, cmd.Label, requireName: cmd.Name is not null, requireLabel: cmd.Label is not null);
        if (!identity.IsSuccess)
            return Result<LabelRuleDto>.Failure(identity.Errors[0]);

        AgentLabelApplyMode? parsedMode = null;
        if (cmd.ApplyMode is not null)
        {
            var mode = LabelRuleValidation.ParseApplyMode(cmd.ApplyMode, existing.ApplyMode);
            if (!mode.IsSuccess)
                return Result<LabelRuleDto>.Failure(mode.Errors[0]);

            parsedMode = mode.Value;
        }

        var effectiveApplyMode = parsedMode ?? existing.ApplyMode;
        if (cmd.ExpressionJson is not null)
        {
            // Modo Manual nao tem expressao avaliavel (ver CreateLabelRuleCommandHandler).
            if (effectiveApplyMode == AgentLabelApplyMode.Manual)
            {
                existing.ExpressionJson = "{}";
            }
            else
            {
                var expression = await LabelRuleValidation.ParseAndValidateExpressionAsync(cmd.ExpressionJson, customFieldService, ct);
                if (!expression.IsSuccess)
                    return Result<LabelRuleDto>.Failure(expression.Errors[0]);

                existing.ExpressionJson = AgentLabelExpressionJson.Serialize(expression.Value);
            }
        }

        if (cmd.Name is not null) existing.Name = cmd.Name.Trim();
        if (cmd.Label is not null) existing.Label = cmd.Label.Trim();
        if (cmd.Description is not null) existing.Description = cmd.Description;
        if (cmd.IsEnabled.HasValue) existing.IsEnabled = cmd.IsEnabled.Value;
        if (parsedMode.HasValue) existing.ApplyMode = parsedMode.Value;
        existing.UpdatedBy = cmd.UpdatedBy;
        existing.UpdatedAt = DateTime.UtcNow;

        await svc.UpdateRuleAsync(existing, ct);
        await LabelRuleReprocessTrigger.EnqueueFullAsync(queue, ct);
        return Result<LabelRuleDto>.Success(CreateLabelRuleCommandHandler.ToDto(existing));
    }
}

/// <summary>
/// Exclui a regra e reconcilia o estado das labels. Antes, excluir deixava as labels
/// automaticas orfas visiveis nos agentes (e alimentando filtros de automacao) ate
/// uma reconciliacao futura, sem invalidar cache nem disparar reprocessamento.
/// </summary>
public sealed class DeleteLabelRuleCommandHandler(ILabelService svc, ILabelReprocessQueue queue)
    : IRequestHandler<DeleteLabelRuleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteLabelRuleCommand cmd, CancellationToken ct)
    {
        var existing = await svc.GetRuleByIdAsync(cmd.Id, ct);
        if (existing is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Label rule {cmd.Id} not found"));

        // Remove a regra e os matches (FK cascade); o cache e invalidado pelo LabelService.
        await svc.DeleteRuleAsync(cmd.Id, ct);

        // Dispara a reconciliacao para remover as labels automaticas que ficaram sem regra.
        try
        {
            // coalesce: false — a passagem em andamento pode ter carregado as regras
            // ANTES desta exclusao, entao precisamos garantir uma nova reconciliacao.
            await queue.EnqueueAsync(actor: "system", ct, coalesce: false);
        }
        catch (Exception)
        {
            // Best-effort: a reconciliacao periodica tambem corrige o estado.
        }

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

/// <summary>
/// Importa regras de um JSON exportado: cria as inexistentes e, quando
/// OverwriteExisting, atualiza as de mesmo nome. Cada regra passa pela MESMA
/// validacao de expressao da criacao; erros sao devolvidos sem abortar o lote.
/// </summary>
public sealed class ImportLabelRulesCommandHandler(ILabelService svc, ICustomFieldService customFieldService, ILabelReprocessQueue queue)
    : IRequestHandler<ImportLabelRulesCommand, Result<AgentLabelRuleImportResultDto>>
{
    private const int MaxRulesPerImport = 200;

    public async Task<Result<AgentLabelRuleImportResultDto>> Handle(ImportLabelRulesCommand cmd, CancellationToken ct)
    {
        var request = cmd.Request;
        if (request?.Rules is null || request.Rules.Count == 0)
            return Result<AgentLabelRuleImportResultDto>.Failure(
                Error.Validation("rules", "Nenhuma regra informada para importar."));

        if (request.Rules.Count > MaxRulesPerImport)
            return Result<AgentLabelRuleImportResultDto>.Failure(
                Error.Validation("rules", $"No máximo {MaxRulesPerImport} regras por importação."));

        var existingRules = await svc.GetRulesAsync(includeDisabled: true, ct);
        var byName = existingRules
            .GroupBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var actor = string.IsNullOrWhiteSpace(cmd.Actor) ? "system" : cmd.Actor!;
        var errors = new List<string>();
        var pending = new List<AgentLabelRule>();
        var created = 0;
        var updated = 0;
        var skipped = 0;

        for (var index = 0; index < request.Rules.Count; index++)
        {
            var imported = request.Rules[index];
            if (imported is null)
            {
                errors.Add($"Regra #{index + 1}: item vazio.");
                continue;
            }

            var identity = LabelRuleValidation.ValidateIdentity(imported.Name, imported.Label, requireName: true, requireLabel: true);
            if (!identity.IsSuccess)
            {
                errors.Add($"Regra #{index + 1}: {identity.Errors[0].Message}");
                continue;
            }

            var applyMode = LabelRuleValidation.ParseApplyMode(imported.ApplyMode, AgentLabelApplyMode.ApplyAndRemove);
            if (!applyMode.IsSuccess)
            {
                errors.Add($"Regra #{index + 1}: {applyMode.Errors[0].Message}");
                continue;
            }

            var expressionJson = "{}";
            if (applyMode.Value != AgentLabelApplyMode.Manual)
            {
                var expression = await LabelRuleValidation.ParseAndValidateExpressionAsync(
                    AgentLabelExpressionJson.Serialize(imported.Expression), customFieldService, ct);
                if (!expression.IsSuccess)
                {
                    errors.Add($"Regra #{index + 1}: {expression.Errors[0].Message}");
                    continue;
                }

                expressionJson = AgentLabelExpressionJson.Serialize(expression.Value);
            }

            var name = imported.Name.Trim();
            if (byName.TryGetValue(name, out var current))
            {
                if (!request.OverwriteExisting)
                {
                    skipped++;
                    continue;
                }

                current.Name = name;
                current.Label = imported.Label.Trim();
                current.Description = imported.Description;
                current.IsEnabled = imported.IsEnabled;
                current.ApplyMode = applyMode.Value;
                current.ExpressionJson = expressionJson;
                current.UpdatedBy = actor;
                current.UpdatedAt = DateTime.UtcNow;
                pending.Add(current);
                updated++;
                continue;
            }

            var newRule = new AgentLabelRule
            {
                Id = IdGenerator.NewId(),
                Name = name,
                Label = imported.Label.Trim(),
                Description = imported.Description,
                IsEnabled = imported.IsEnabled,
                ApplyMode = applyMode.Value,
                ExpressionJson = expressionJson,
                CreatedBy = actor,
                UpdatedBy = actor,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            byName[name] = newRule;
            pending.Add(newRule);
            created++;
        }

        // UMA gravacao e UMA invalidacao de cache para o lote inteiro: antes cada regra
        // fazia seu proprio SaveChanges (200 regras = 200 round-trips + 200 deletes).
        if (pending.Count > 0)
        {
            await svc.ImportRulesAsync(pending, ct);
            await LabelRuleReprocessTrigger.EnqueueFullAsync(queue, ct);
        }

        return Result<AgentLabelRuleImportResultDto>.Success(new AgentLabelRuleImportResultDto
        {
            Created = created,
            Updated = updated,
            Skipped = skipped,
            Errors = errors
        });
    }
}

public sealed class ReprocessLabelsCommandHandler(ILabelReprocessQueue queue)
    : IRequestHandler<ReprocessLabelsCommand, Result<string>>
{
    public async Task<Result<string>> Handle(ReprocessLabelsCommand cmd, CancellationToken ct)
    {
        // Enfileira o reprocessamento para rodar em background, evitando bloquear
        // a requisição HTTP com uma operação em lote potencialmente longa.
        // O jobId devolvido permite acompanhar o progresso.
        var jobId = await queue.EnqueueAsync(cmd.Actor, ct);
        return Result<string>.Success(jobId);
    }
}
