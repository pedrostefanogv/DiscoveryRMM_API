namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Command to transition a ticket to a new workflow state.
/// </summary>
public sealed record TransitionTicketStateCommand(
    Guid TicketId,
    Guid TargetStateId,
    Guid? ChangedByUserId
) : ICommand<Result<TransitionTicketStateResult>>;

/// <summary>
/// Corpo aceito pelo endpoint PATCH /tickets/{id}/workflow-state.
///
/// Aceita as duas grafias do estado de destino de propósito:
/// <list type="bullet">
/// <item><c>targetStateId</c> — contrato canônico do CQRS.</item>
/// <item><c>workflowStateId</c> — nome legado (pré-CQRS) que a console web
/// enviava. Sem o alias, o model binder deixava <c>TargetStateId</c> vazio e o
/// endpoint devolvia 400 para TODA troca de estado — a causa raiz dos chamados
/// que "não fechavam".</item>
/// </list>
/// Aceitar as duas grafias evita reincidência com clientes/PWA em cache.
/// </summary>
public sealed record TransitionTicketStateRequest(
    Guid? TargetStateId,
    Guid? WorkflowStateId
)
{
    /// <summary>Resolve o estado de destino tolerando corpo vazio e grafia legada.</summary>
    public Guid ResolveTargetStateId()
    {
        if (TargetStateId.HasValue && TargetStateId.Value != Guid.Empty)
            return TargetStateId.Value;

        return WorkflowStateId ?? Guid.Empty;
    }
}

/// <summary>
/// Result of a ticket state transition.
/// </summary>
public sealed record TransitionTicketStateResult(
    Guid TicketId,
    Guid PreviousStateId,
    Guid NewStateId,
    DateTime? ClosedAt
);
