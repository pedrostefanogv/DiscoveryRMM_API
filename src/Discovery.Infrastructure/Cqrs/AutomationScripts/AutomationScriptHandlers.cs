using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationScripts.Queries;
using Discovery.Core.DTOs;
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
