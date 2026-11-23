using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationScripts.Commands;
using Discovery.Core.Cqrs.AutomationScripts.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AutomationScripts;

internal static class AutomationScriptScopeGuard
{
    public static async Task<Error?> CheckClientAccessAsync(
        IScopeContext? scopeContext,
        ActionType action,
        Guid? clientId)
    {
        if (scopeContext is null || !scopeContext.UserId.HasValue)
            return null;

        var access = await scopeContext.GetAccessAsync(ResourceType.Automation, action);
        var allowed = clientId.HasValue
            ? TenantScopePolicy.Allows(access, clientId.Value, Guid.Empty)
            : access.HasGlobalAccess;

        return allowed ? null : Error.Forbidden("Você não tem permissão para operar scripts neste escopo.");
    }
}

public sealed class ListAutomationScriptsQueryHandler(IAutomationScriptService svc) : IRequestHandler<ListAutomationScriptsQuery, Result<CursorPageDto<AutomationScriptSummaryDto>>>
{
    public async Task<Result<CursorPageDto<AutomationScriptSummaryDto>>> Handle(ListAutomationScriptsQuery q, CancellationToken ct)
    {
        var page = await svc.GetListPageAsync(q.ClientId, q.ActiveOnly, q.Cursor, q.Limit, ct, q.Search, q.ScriptType);
        return Result<CursorPageDto<AutomationScriptSummaryDto>>.Success(page);
    }
}

public sealed class GetAutomationScriptByIdQueryHandler(
    IAutomationScriptService svc,
    IScopeContext? scopeContext = null) : IRequestHandler<GetAutomationScriptByIdQuery, Result<AutomationScriptDetailDto>>
{
    public async Task<Result<AutomationScriptDetailDto>> Handle(GetAutomationScriptByIdQuery q, CancellationToken ct)
    {
        var s = await svc.GetByIdAsync(q.Id, includeInactive: true, ct);
        if (s is null) return Result<AutomationScriptDetailDto>.Failure(Error.NotFound($"Script {q.Id} not found"));

        var denied = await AutomationScriptScopeGuard.CheckClientAccessAsync(scopeContext, ActionType.View, s.ClientId);
        if (denied is not null && s.ClientId.HasValue)
            return Result<AutomationScriptDetailDto>.Failure(denied);

        return Result<AutomationScriptDetailDto>.Success(s);
    }
}

public sealed class GetAutomationScriptAuditQueryHandler(IAutomationScriptService svc) : IRequestHandler<GetAutomationScriptAuditQuery, Result<IReadOnlyList<AutomationScriptAuditDto>>>
{
    public async Task<Result<IReadOnlyList<AutomationScriptAuditDto>>> Handle(GetAutomationScriptAuditQuery q, CancellationToken ct)
    {
        var audit = await svc.GetAuditAsync(q.Id, q.Limit, ct);
        return Result<IReadOnlyList<AutomationScriptAuditDto>>.Success(audit);
    }
}

public sealed class GetAutomationScriptConsumeQueryHandler(IAutomationScriptService svc) : IRequestHandler<GetAutomationScriptConsumeQuery, Result<AutomationScriptConsumeDto>>
{
    public async Task<Result<AutomationScriptConsumeDto>> Handle(GetAutomationScriptConsumeQuery q, CancellationToken ct)
    {
        var payload = await svc.GetConsumePayloadAsync(
            q.Id, q.ChangedBy, q.IpAddress, q.CorrelationId ?? "api", ct);

        if (payload is null)
            return Result<AutomationScriptConsumeDto>.Failure(Error.NotFound($"Script {q.Id} not found"));

        return Result<AutomationScriptConsumeDto>.Success(payload);
    }
}

// ── Commands ─────────────────────────────────────────────────────────

public sealed class CreateAutomationScriptCommandHandler(
    IAutomationScriptService svc,
    IScopeContext? scopeContext = null) : IRequestHandler<CreateAutomationScriptCommand, Result<AutomationScriptDetailDto>>
{
    public async Task<Result<AutomationScriptDetailDto>> Handle(CreateAutomationScriptCommand cmd, CancellationToken ct)
    {
        var denied = await AutomationScriptScopeGuard.CheckClientAccessAsync(scopeContext, ActionType.Create, cmd.ClientId);
        if (denied is not null)
            return Result<AutomationScriptDetailDto>.Failure(denied);

        var request = new CreateAutomationScriptRequest
        {
            ClientId = cmd.ClientId,
            Name = cmd.Name,
            Summary = cmd.Summary,
            ScriptType = cmd.ScriptType,
            Version = cmd.Version,
            Content = cmd.Content,
            IsActive = cmd.IsActive
        };

        var result = await svc.CreateAsync(request, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", ct);
        return Result<AutomationScriptDetailDto>.Success(result);
    }
}

public sealed class UpdateAutomationScriptCommandHandler(
    IAutomationScriptService svc,
    IScopeContext? scopeContext = null) : IRequestHandler<UpdateAutomationScriptCommand, Result<AutomationScriptDetailDto>>
{
    public async Task<Result<AutomationScriptDetailDto>> Handle(UpdateAutomationScriptCommand cmd, CancellationToken ct)
    {
        var existing = await svc.GetByIdAsync(cmd.Id, includeInactive: true, ct);
        if (existing is null) return Result<AutomationScriptDetailDto>.Failure(Error.NotFound($"Script {cmd.Id} not found"));

        var denied = await AutomationScriptScopeGuard.CheckClientAccessAsync(scopeContext, ActionType.Edit, existing.ClientId);
        if (denied is not null)
            return Result<AutomationScriptDetailDto>.Failure(denied);

        var request = new UpdateAutomationScriptRequest
        {
            Name = cmd.Name,
            Summary = cmd.Summary,
            ScriptType = cmd.ScriptType,
            Version = cmd.Version,
            Content = cmd.Content,
            IsActive = cmd.IsActive,
            Reason = cmd.Reason
        };

        var result = await svc.UpdateAsync(cmd.Id, request, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", ct);
        if (result is null) return Result<AutomationScriptDetailDto>.Failure(Error.NotFound($"Script {cmd.Id} not found"));
        return Result<AutomationScriptDetailDto>.Success(result);
    }
}

public sealed class DeleteAutomationScriptCommandHandler(
    IAutomationScriptService svc,
    IScopeContext? scopeContext = null) : IRequestHandler<DeleteAutomationScriptCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteAutomationScriptCommand cmd, CancellationToken ct)
    {
        var existing = await svc.GetByIdAsync(cmd.Id, includeInactive: true, ct);
        if (existing is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Script {cmd.Id} not found"));

        var denied = await AutomationScriptScopeGuard.CheckClientAccessAsync(scopeContext, ActionType.Delete, existing.ClientId);
        if (denied is not null)
            return Result<VoidResult>.Failure(denied);

        var ok = await svc.DeleteAsync(cmd.Id, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", cmd.Reason, ct);
        return ok ? Result<VoidResult>.Success(VoidResult.Value) : Result<VoidResult>.Failure(Error.NotFound($"Script {cmd.Id} not found"));
    }
}
