using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

/// <summary>
/// Guarda de monotonicidade do status de comando (AgentCommand.Status).
///
/// Motivo: o result do agente pode chegar múltiplas vezes (ack antecipado do
/// comando de energia, resultado final, reentrega atrasada). Sem guarda, um ack
/// tardio reverte Failed -> Completed e a falha desaparece do histórico.
///
/// Regras:
///  (a) terminal {Completed, Failed, Cancelled, Timeout, Deferred} -> não-terminal
///      {Pending, Sent, Running}: recusado (não reabre comando já encerrado).
///  (b) veredito negativo {Failed, Cancelled, Timeout, Deferred} -> Completed:
///      recusado (negativa não vira sucesso).
///  (c) Completed -> Failed: PERMITIDO — é o result final corrigindo o ack
///      antecipado (o agente confirmou cedo e depois falhou).
///  (d) qualquer transição não-terminal (Pending -> Sent, Sent -> Running, ...):
///      permitida.
/// </summary>
public static class CommandStatusTransitions
{
    public static bool IsTerminal(CommandStatus status)
        => status is CommandStatus.Completed
            or CommandStatus.Failed
            or CommandStatus.Cancelled
            or CommandStatus.Timeout
            or CommandStatus.Deferred;

    public static bool IsNonTerminal(CommandStatus status)
        => status is CommandStatus.Pending
            or CommandStatus.Sent
            or CommandStatus.Running;

    /// <summary>Vereditos que nunca podem ser sobrescritos por sucesso.</summary>
    public static bool IsNegativeVerdict(CommandStatus status)
        => status is CommandStatus.Failed
            or CommandStatus.Cancelled
            or CommandStatus.Timeout
            or CommandStatus.Deferred;

    public static bool ShouldApply(CommandStatus current, CommandStatus next)
    {
        if (IsTerminal(current) && IsNonTerminal(next))
            return false;

        if (IsNegativeVerdict(current) && next == CommandStatus.Completed)
            return false;

        return true;
    }
}
