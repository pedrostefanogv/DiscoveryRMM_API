using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Patches;

/// <summary>
/// Upload do inventário de Windows Update do agente. Os blocos chegam como
/// objetos JSON (mesmo padrão do inventário de software) e são normalizados
/// pelo handler.
///
/// <para>
/// <c>State</c> é OPCIONAL (agentes novos): sonda de estado do serviço
/// (wuauserv/WSUS/política/última instalação/download em andamento) usada para
/// explicar na UI um scan que volta vazio. Agentes antigos continuam enviando o
/// mesmo envelope sem o campo.
/// </para>
/// </summary>
public sealed record ReportAgentWindowsUpdatesCommand(
    Guid AgentId,
    DateTime? CollectedAt,
    object? Scan,
    object? PendingReboot,
    object? Updates,
    object? State = null
) : ICommand<Result<VoidResult>>;

/// <summary>
/// Sonda leve do agente (endpoint usado a cada poucos minutos): reinício
/// pendente + estado do serviço Windows Update. Todos os campos de estado são
/// opcionais — agentes antigos mandam só <c>pending</c>/<c>reasons</c>.
///
/// <para>
/// O agente faz o scan WUA caro SÓ quando a assinatura (<c>StateHash</c>) muda;
/// esta sonda mantém a aba "Atualizações" explicável no meio do intervalo.
/// </para>
/// </summary>
public sealed record ReportAgentPendingRebootCommand(
    Guid AgentId,
    bool Pending,
    IReadOnlyList<string>? Reasons,
    DateTime? CheckedAt,
    object? State = null
) : ICommand<Result<VoidResult>>;

public sealed record GetAgentWindowsUpdatesQuery(Guid AgentId) : IQuery<Result<object>>;
