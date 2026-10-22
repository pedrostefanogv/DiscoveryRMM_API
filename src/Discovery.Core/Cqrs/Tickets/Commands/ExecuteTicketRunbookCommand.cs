using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Executa o runbook de uma TicketMacro no chamado: cada passo dispara o
/// script de automação na máquina vinculada ao chamado, aguarda a conclusão
/// e anexa a evidência (stdout/exit code) como comentário.
/// Execução sequencial; falha de um passo NÃO aborta os seguintes.
/// </summary>
public sealed record ExecuteTicketRunbookCommand(
    Guid TicketId,
    Guid MacroId,
    Guid? OperatorId = null,
    string? OperatorName = null
) : ICommand<Result<ExecuteTicketRunbookResult>>;

public sealed record RunbookStepResult(
    int Order,
    Guid ScriptId,
    bool Ok,
    int? ExitCode,
    string? OutputPreview,
    long DurationMs,
    Guid? CommentId);

public sealed record ExecuteTicketRunbookResult(
    Guid TicketId,
    IReadOnlyList<RunbookStepResult> Steps);
