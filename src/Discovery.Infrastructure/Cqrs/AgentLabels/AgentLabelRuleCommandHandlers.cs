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

public sealed class CreateLabelRuleCommandHandler(ILabelService svc, ICustomFieldService customFieldService)
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

        var expression = await LabelRuleValidation.ParseAndValidateExpressionAsync(cmd.ExpressionJson, customFieldService, ct);
        if (!expression.IsSuccess)
            return Result<LabelRuleDto>.Failure(expression.Errors[0]);

        var rule = new AgentLabelRule
        {
            Id = Guid.NewGuid(),
            Name = cmd.Name.Trim(),
            Label = cmd.Label.Trim(),
            Description = cmd.Description,
            IsEnabled = cmd.IsEnabled,
            ApplyMode = applyMode.Value,
            ExpressionJson = cmd.ExpressionJson,
            CreatedBy = cmd.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await svc.CreateRuleAsync(rule, ct);
        return Result<LabelRuleDto>.Success(ToDto(created));
    }

    internal static LabelRuleDto ToDto(AgentLabelRule r) => new(
        r.Id, r.Name, r.Label, r.Description, r.IsEnabled,
        r.ApplyMode.ToString(), r.ExpressionJson,
        r.CreatedBy, r.CreatedAt, r.UpdatedAt);
}

public sealed class UpdateLabelRuleCommandHandler(ILabelService svc, ICustomFieldService customFieldService)
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

        if (cmd.ExpressionJson is not null)
        {
            var expression = await LabelRuleValidation.ParseAndValidateExpressionAsync(cmd.ExpressionJson, customFieldService, ct);
            if (!expression.IsSuccess)
                return Result<LabelRuleDto>.Failure(expression.Errors[0]);

            existing.ExpressionJson = cmd.ExpressionJson;
        }

        if (cmd.Name is not null) existing.Name = cmd.Name.Trim();
        if (cmd.Label is not null) existing.Label = cmd.Label.Trim();
        if (cmd.Description is not null) existing.Description = cmd.Description;
        if (cmd.IsEnabled.HasValue) existing.IsEnabled = cmd.IsEnabled.Value;
        if (parsedMode.HasValue) existing.ApplyMode = parsedMode.Value;
        existing.UpdatedBy = cmd.UpdatedBy;
        existing.UpdatedAt = DateTime.UtcNow;

        await svc.UpdateRuleAsync(existing, ct);
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
            await queue.EnqueueAsync(ct);
        }
        catch (Exception)
        {
            // Best-effort: a reconciliacao periodica tambem corrige o estado.
        }

        return Result<VoidResult>.Success(VoidResult.Value);
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
        var jobId = await queue.EnqueueAsync(ct);
        return Result<string>.Success(jobId);
    }
}
