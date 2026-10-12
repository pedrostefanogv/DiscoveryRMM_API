using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>Resultado da avaliação de reentrega de um comando.</summary>
public readonly record struct CommandRedeliveryDecision(bool Allowed, string? Reason)
{
    public static CommandRedeliveryDecision Allow => new(true, null);

    public static CommandRedeliveryDecision Block(string reason) => new(false, reason);
}

/// <summary>
/// Última palavra antes de reenviar um comando não confirmado ao agente.
///
/// A reentrega do servidor é genérica e cega ao domínio: um convite de chat de
/// suporte (TicketChatInvite) reenviado depois de a sessão ter sido
/// encerrada/recusada/vencida reabria a janela do chat no PC do usuário "do
/// nada". A guarda deixa o domínio (ex.: chat de suporte) vetar o reenvio sem
/// mexer no laço genérico de reentrega.
/// </summary>
public interface ICommandRedeliveryGuard
{
    Task<CommandRedeliveryDecision> EvaluateAsync(
        AgentCommand command,
        CancellationToken cancellationToken = default);
}
