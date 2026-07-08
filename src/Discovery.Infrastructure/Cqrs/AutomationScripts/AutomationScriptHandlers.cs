using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationScripts.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AutomationScripts;

public sealed class ListAutomationScriptsQueryHandler(IAutomationScriptService svc) : IRequestHandler<ListAutomationScriptsQuery, Result<IReadOnlyList<AutomationScriptDto>>>
{
    public async Task<Result<IReadOnlyList<AutomationScriptDto>>> Handle(ListAutomationScriptsQuery q, CancellationToken ct)
    {
        var page = await svc.GetListPageAsync(q.ClientId, true, q.Cursor, q.Limit, ct);
        var dtos = new List<AutomationScriptDto>();
        foreach (var s in page.Items)
            dtos.Add(new AutomationScriptDto(s.Id, s.Name, "Script", s.IsActive, s.CreatedAt, s.CreatedAt));
        return Result<IReadOnlyList<AutomationScriptDto>>.Success(dtos.AsReadOnly());
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
