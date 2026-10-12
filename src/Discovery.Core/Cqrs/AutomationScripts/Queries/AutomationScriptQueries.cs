using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AutomationScripts.Queries;

// O contrato do console (AutomationScriptsPage) espera o resumo/detalhe completos:
// summary, scriptType, version, executionFrequency, triggerModes, lastUpdatedAt e (no detalhe) content.
// A paginação já é produzida por IAutomationScriptService.GetListPageAsync/GetByIdAsync.
public sealed record ListAutomationScriptsQuery(Guid? ClientId, string? Cursor = null, int Limit = 50) : IQuery<Result<CursorPageDto<AutomationScriptSummaryDto>>>;
public sealed record GetAutomationScriptByIdQuery(Guid Id) : IQuery<Result<AutomationScriptDetailDto>>;

/// <summary>
/// Trilha de auditoria de um script (Created/Updated/Activated/Deactivated/
/// Consumed/Deleted). Sem este endpoint o modal "Auditoria do script" do console
/// e a página /automation/audit caíam em 404.
/// </summary>
public sealed record GetAutomationScriptAuditQuery(Guid Id, int Limit = 50) : IQuery<Result<IReadOnlyList<AutomationScriptAuditDto>>>;

/// <summary>
/// Payload de consumo do script: exatamente o conteúdo + hash SHA-256 + schema/
/// metadados que o agent recebe, e grava um evento "Consumed" na auditoria.
/// </summary>
public sealed record GetAutomationScriptConsumeQuery(
    Guid Id,
    string? ChangedBy = null,
    string? IpAddress = null,
    string? CorrelationId = null
) : IQuery<Result<AutomationScriptConsumeDto>>;
