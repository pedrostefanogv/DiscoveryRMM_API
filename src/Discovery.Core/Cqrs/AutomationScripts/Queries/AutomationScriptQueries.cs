using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AutomationScripts.Queries;

public sealed record ListAutomationScriptsQuery(Guid? ClientId, string? Cursor = null, int Limit = 50) : IQuery<Result<CursorPageDto<AutomationScriptDto>>>;
public sealed record GetAutomationScriptByIdQuery(Guid Id) : IQuery<Result<AutomationScriptDto>>;

public sealed record AutomationScriptDto(Guid Id, string Name, string? Description, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);
