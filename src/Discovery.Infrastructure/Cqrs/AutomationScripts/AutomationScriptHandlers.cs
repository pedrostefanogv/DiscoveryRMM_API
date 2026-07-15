using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationScripts.Commands;
using Discovery.Core.Cqrs.AutomationScripts.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AutomationScripts;

public sealed class ListAutomationScriptsQueryHandler(IAutomationScriptService svc) : IRequestHandler<ListAutomationScriptsQuery, Result<CursorPageDto<AutomationScriptDto>>>
{
    public async Task<Result<CursorPageDto<AutomationScriptDto>>> Handle(ListAutomationScriptsQuery q, CancellationToken ct)
    {
        var page = await svc.GetListPageAsync(q.ClientId, true, q.Cursor, q.Limit, ct);
        var dtos = page.Items.Select(s => new AutomationScriptDto(s.Id, s.Name, "Script", s.IsActive, s.CreatedAt, s.CreatedAt)).ToList();
        return Result<CursorPageDto<AutomationScriptDto>>.Success(
            new CursorPageDto<AutomationScriptDto>(dtos.AsReadOnly(), dtos.Count, page.Cursor, page.NextCursor, page.HasMore, page.Limit));
    }
}

public sealed class GetAutomationScriptByIdQueryHandler(IAutomationScriptService svc) : IRequestHandler<GetAutomationScriptByIdQuery, Result<AutomationScriptDto>>
{
    public async Task<Result<AutomationScriptDto>> Handle(GetAutomationScriptByIdQuery q, CancellationToken ct)
    {
        var s = await svc.GetByIdAsync(q.Id, false, ct);
        if (s is null) return Result<AutomationScriptDto>.Failure(Error.NotFound($"Script {q.Id} not found"));
        return Result<AutomationScriptDto>.Success(new AutomationScriptDto(s.Id, s.Name, "Script", s.IsActive, s.CreatedAt, s.CreatedAt));
    }
}

// ── Commands ─────────────────────────────────────────────────────────

public sealed class CreateAutomationScriptCommandHandler(IAutomationScriptService svc) : IRequestHandler<CreateAutomationScriptCommand, Result<AutomationScriptDetailDto>>
{
    public async Task<Result<AutomationScriptDetailDto>> Handle(CreateAutomationScriptCommand cmd, CancellationToken ct)
    {
        var request = new CreateAutomationScriptRequest
        {
            ClientId = cmd.ClientId,
            Name = cmd.Name,
            Summary = cmd.Summary,
            ScriptType = cmd.ScriptType,
            Version = cmd.Version,
            ExecutionFrequency = cmd.ExecutionFrequency,
            TriggerModes = cmd.TriggerModes,
            Content = cmd.Content,
            ParametersSchemaJson = cmd.ParametersSchemaJson,
            MetadataJson = cmd.MetadataJson,
            IsActive = cmd.IsActive
        };

        var result = await svc.CreateAsync(request, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", ct);
        return Result<AutomationScriptDetailDto>.Success(result);
    }
}

public sealed class UpdateAutomationScriptCommandHandler(IAutomationScriptService svc) : IRequestHandler<UpdateAutomationScriptCommand, Result<AutomationScriptDetailDto>>
{
    public async Task<Result<AutomationScriptDetailDto>> Handle(UpdateAutomationScriptCommand cmd, CancellationToken ct)
    {
        var request = new UpdateAutomationScriptRequest
        {
            Name = cmd.Name,
            Summary = cmd.Summary,
            ScriptType = cmd.ScriptType,
            Version = cmd.Version,
            ExecutionFrequency = cmd.ExecutionFrequency,
            TriggerModes = cmd.TriggerModes,
            Content = cmd.Content,
            ParametersSchemaJson = cmd.ParametersSchemaJson,
            MetadataJson = cmd.MetadataJson,
            IsActive = cmd.IsActive,
            Reason = cmd.Reason
        };

        var result = await svc.UpdateAsync(cmd.Id, request, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", ct);
        if (result is null) return Result<AutomationScriptDetailDto>.Failure(Error.NotFound($"Script {cmd.Id} not found"));
        return Result<AutomationScriptDetailDto>.Success(result);
    }
}

public sealed class DeleteAutomationScriptCommandHandler(IAutomationScriptService svc) : IRequestHandler<DeleteAutomationScriptCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteAutomationScriptCommand cmd, CancellationToken ct)
    {
        var ok = await svc.DeleteAsync(cmd.Id, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", cmd.Reason, ct);
        return ok ? Result<VoidResult>.Success(VoidResult.Value) : Result<VoidResult>.Failure(Error.NotFound($"Script {cmd.Id} not found"));
    }
}
