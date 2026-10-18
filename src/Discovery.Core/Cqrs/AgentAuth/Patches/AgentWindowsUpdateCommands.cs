using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Patches;

/// <summary>
/// Upload do inventário de Windows Update do agente. Os blocos chegam como
/// objetos JSON (mesmo padrão do inventário de software) e são normalizados
/// pelo handler.
/// </summary>
public sealed record ReportAgentWindowsUpdatesCommand(
    Guid AgentId,
    DateTime? CollectedAt,
    object? Scan,
    object? PendingReboot,
    object? Updates
) : ICommand<Result<VoidResult>>;

/// <summary>Sonda de reinício pendente (endpoint leve, enviado só em mudança).</summary>
public sealed record ReportAgentPendingRebootCommand(
    Guid AgentId,
    bool Pending,
    IReadOnlyList<string>? Reasons,
    DateTime? CheckedAt
) : ICommand<Result<VoidResult>>;

public sealed record GetAgentWindowsUpdatesQuery(Guid AgentId) : IQuery<Result<object>>;
