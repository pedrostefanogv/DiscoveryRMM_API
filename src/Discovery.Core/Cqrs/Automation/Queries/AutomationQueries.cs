using Discovery.Core.Cqrs;
namespace Discovery.Core.Cqrs.Automation.Queries;
public sealed record ListScriptsQuery(string? Search, string? Cursor, int Limit = 50) : IQuery<Result<ListScriptsResult>>;
public sealed record ListScriptsResult(IReadOnlyList<ScriptDto> Items, string? NextCursor, bool HasMore);
public sealed record ScriptDto(Guid Id, string Name, string? Description, string Type, DateTime CreatedAt);
public sealed record GetScriptByIdQuery(Guid Id) : IQuery<Result<ScriptDetailDto>>;
public sealed record ScriptDetailDto(Guid Id, string Name, string Description, string Type, string Content, DateTime CreatedAt);
public sealed record ListTasksQuery(Guid? ScriptId, string? Status, string? Cursor, int Limit = 50) : IQuery<Result<ListTasksResult>>;
public sealed record ListTasksResult(IReadOnlyList<TaskDto> Items, string? NextCursor, bool HasMore);
public sealed record TaskDto(Guid Id, string Name, string Status, Guid? ScriptId, DateTime CreatedAt);

// Removidos: ListAutoTicketRulesQuery e AutoTicketRuleDto viviam aqui, SEM
// handler e SEM chamador, e colidiam em nome com os tipos vivos em
// Discovery.Core.Cqrs.AutoTicketRules.* (que têm 15 campos, contra 5 destes).
//
// Era uma armadilha real: um arquivo que importasse os dois namespaces
// compilaria escolhendo um deles em silêncio, e o DTO de 5 campos não tem os
// campos que a tela de regras consome (scopeLevel, alertCodeFilter, priority...).
// A rota /api/v1/auto-ticket-rules sempre usou a versão de 15 campos.
