using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AgentAuth.Misc;

public sealed record GetAgentIdentityQuery(Guid AgentId) : IQuery<Result<object>>;
/// <summary>
/// Consulta da política efetiva da app-store para um agent.
///
/// Paginação (contrato do agent Go, campo <c>pageSize</c>/<c>cursor</c>):
/// a coleção pode passar de 27 mil itens (catálogo winget inteiro aprovado),
/// e devolver tudo numa única resposta custa dezenas de MB de RAM tanto na API
/// quanto no processo do agent. Quando <see cref="Paged"/> é true a resposta é
/// fatiada em ordem determinística e traz <c>hasMore</c>/<c>nextCursor</c>.
///
/// <see cref="Paged"/> é OPT-IN de propósito: agents já instalados (que enviam
/// apenas pageSize) continuam recebendo a coleção completa numa página e não
/// quebram no limite de páginas do cliente antigo.
/// </summary>
/// <param name="PageSize">Itens por página quando paginado (0 = default do servidor).</param>
/// <param name="Cursor">Cursor opaco devolvido como nextCursor na página anterior.</param>
public sealed record GetAppStoreEffectiveQuery(
    Guid AgentId,
    string InstallationType,
    bool Paged = false,
    int PageSize = 0,
    string? Cursor = null) : IQuery<Result<object>>;
public sealed record GetRuntimeCustomFieldsQuery(Guid AgentId, Guid? TaskId, Guid? ScriptId) : IQuery<Result<object>>;
public sealed record UpsertCollectedCustomFieldCommand(Guid AgentId, AgentCollectedCustomFieldValueRequest Request) : ICommand<Result<object>>;
public sealed record IssueZeroTouchDeployTokenCommand(Guid AgentId) : ICommand<Result<object>>;
public sealed record GetAgentUpdateManifestQuery(Guid AgentId, string? CurrentVersion, string? Platform, string? Architecture, string? ArtifactType) : IQuery<Result<object>>;
public sealed record DownloadAgentUpdateQuery(Guid AgentId, Guid? ReleaseId, string? Version, string? Platform, string? Architecture, string? ArtifactType) : IQuery<Result<object>>;
public sealed record ReportAgentUpdateCommand(Guid AgentId, AgentUpdateReportRequest Request) : ICommand<Result<object>>;