using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentLabels;

public sealed class CreateLabelRuleCommandHandler(ILabelService svc) : IRequestHandler<CreateLabelRuleCommand, Result<LabelRuleDto>>
{
    public async Task<Result<LabelRuleDto>> Handle(CreateLabelRuleCommand cmd, CancellationToken ct)
    {
        var rule = new AgentLabelRule
        {
            Id = Guid.NewGuid(),
            Name = cmd.Name,
            Label = cmd.Label,
            Description = cmd.Description,
            IsEnabled = cmd.IsEnabled,
            ApplyMode = Enum.TryParse<AgentLabelApplyMode>(cmd.ApplyMode, ignoreCase: true, out var mode) ? mode : AgentLabelApplyMode.ApplyAndRemove,
            ExpressionJson = cmd.ExpressionJson,
            CreatedBy = cmd.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await svc.CreateRuleAsync(rule, ct);
        return Result<LabelRuleDto>.Success(ToDto(created));
    }

    private static LabelRuleDto ToDto(AgentLabelRule r) => new(
        r.Id, r.Name, r.Label, r.Description, r.IsEnabled,
        r.ApplyMode.ToString(), r.ExpressionJson,
        r.CreatedBy, r.CreatedAt, r.UpdatedAt);
}

public sealed class UpdateLabelRuleCommandHandler(ILabelService svc) : IRequestHandler<UpdateLabelRuleCommand, Result<LabelRuleDto>>
{
    public async Task<Result<LabelRuleDto>> Handle(UpdateLabelRuleCommand cmd, CancellationToken ct)
    {
        var existing = await svc.GetRuleByIdAsync(cmd.Id, ct);
        if (existing is null)
            return Result<LabelRuleDto>.Failure(Error.NotFound($"Label rule {cmd.Id} not found"));

        if (cmd.Name is not null) existing.Name = cmd.Name;
        if (cmd.Label is not null) existing.Label = cmd.Label;
        if (cmd.Description is not null) existing.Description = cmd.Description;
        if (cmd.IsEnabled.HasValue) existing.IsEnabled = cmd.IsEnabled.Value;
        if (cmd.ApplyMode is not null && Enum.TryParse<AgentLabelApplyMode>(cmd.ApplyMode, ignoreCase: true, out var mode))
            existing.ApplyMode = mode;
        if (cmd.ExpressionJson is not null) existing.ExpressionJson = cmd.ExpressionJson;
        existing.UpdatedBy = cmd.UpdatedBy;
        existing.UpdatedAt = DateTime.UtcNow;

        await svc.UpdateRuleAsync(existing, ct);
        return Result<LabelRuleDto>.Success(new LabelRuleDto(
            existing.Id, existing.Name, existing.Label, existing.Description,
            existing.IsEnabled, existing.ApplyMode.ToString(), existing.ExpressionJson,
            existing.CreatedBy, existing.CreatedAt, existing.UpdatedAt));
    }
}

public sealed class DeleteLabelRuleCommandHandler(ILabelService svc) : IRequestHandler<DeleteLabelRuleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteLabelRuleCommand cmd, CancellationToken ct)
    {
        var existing = await svc.GetRuleByIdAsync(cmd.Id, ct);
        if (existing is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Label rule {cmd.Id} not found"));

        await svc.DeleteRuleAsync(cmd.Id, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ReprocessLabelsCommandHandler(ILabelReprocessQueue queue)
    : IRequestHandler<ReprocessLabelsCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ReprocessLabelsCommand cmd, CancellationToken ct)
    {
        // Enfileira o reprocessamento para rodar em background, evitando bloquear
        // a requisição HTTP com uma operação em lote potencialmente longa.
        await queue.EnqueueAsync(ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
