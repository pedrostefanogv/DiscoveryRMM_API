using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AutomationScripts.Queries;

// O contrato do console (AutomationScriptsPage) espera o resumo/detalhe completos:
// summary, scriptType, version, executionFrequency, triggerModes, lastUpdatedAt e (no detalhe) content.
// A paginação já é produzida por IAutomationScriptService.GetListPageAsync/GetByIdAsync.
public sealed record ListAutomationScriptsQuery(Guid? ClientId, string? Cursor = null, int Limit = 50) : IQuery<Result<CursorPageDto<AutomationScriptSummaryDto>>>;
public sealed record GetAutomationScriptByIdQuery(Guid Id) : IQuery<Result<AutomationScriptDetailDto>>;
