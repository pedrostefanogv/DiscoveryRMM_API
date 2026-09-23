using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentLabels;

public sealed class AddAgentLabelCommandHandler(ILabelService svc) : IRequestHandler<AddAgentLabelCommand, Result<AgentLabelDto>>
{
    /// <summary>Coluna agent_labels.label e varchar(120).</summary>
    private const int MaxLabelLength = 120;

    public async Task<Result<AgentLabelDto>> Handle(AddAgentLabelCommand cmd, CancellationToken ct)
    {
        // Normaliza espacos: antes " PROD " criava uma label distinta de "PROD".
        var label = cmd.Label?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(label))
            return Result<AgentLabelDto>.Failure(Error.Validation("label", "Label is required."));

        if (label.Length > MaxLabelLength)
            return Result<AgentLabelDto>.Failure(
                Error.Validation("label", $"Label exceeds maximum length of {MaxLabelLength}."));

        var existing = await svc.GetByAgentIdAsync(cmd.AgentId, ct);
        if (existing.Any(l => string.Equals(l.Label, label, StringComparison.OrdinalIgnoreCase)))
            return Result<AgentLabelDto>.Failure(Error.Conflict($"Agent already has label '{label}'."));

        try
        {
            var created = await svc.AddWithSuppressionClearAsync(new AgentLabel
            {
                AgentId = cmd.AgentId,
                Label = label,
                SourceType = AgentLabelSourceType.Manual
            }, ct);

            return Result<AgentLabelDto>.Success(
                new AgentLabelDto(created.Id, created.AgentId, created.Label, created.SourceType.ToString(), created.CreatedAt));
        }
        catch (Exception)
        {
            // Corrida entre o check e o insert: o indice unico ux_agent_labels_agent_label
            // garante a unicidade e nos permite responder 409 em vez de 500.
            return Result<AgentLabelDto>.Failure(Error.Conflict($"Agent already has label '{label}'."));
        }
    }
}
