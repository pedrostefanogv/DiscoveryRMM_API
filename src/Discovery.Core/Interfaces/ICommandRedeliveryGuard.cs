using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>Resultado da avaliação de reentrega de um comando.</summary>
public readonly record struct CommandRedeliveryDecision(bool Allowed, string? Reason)
{
    /// <summary>
    /// true (default, comportamento histórico): quando !Allowed, o serviço
    /// encerra o comando como Cancelled. false: o reenvio é apenas ignorado e o
    /// status do comando é preservado — usado pelo comando de energia já
    /// publicado, que provavelmente JÁ executou (o reboot derruba o agente antes
    /// do result/ack) e não pode ser marcado como cancelado por engano.
    /// </summary>
    public bool MarkTerminal { get; init; } = true;

    public static CommandRedeliveryDecision Allow => new(true, null);

    public static CommandRedeliveryDecision Block(string reason) => new(false, reason);

    /// <summary>Não reenvia e não encerra: o comando segue com o status atual.</summary>
    public static CommandRedeliveryDecision Ignore(string reason) => new(false, reason) { MarkTerminal = false };
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
