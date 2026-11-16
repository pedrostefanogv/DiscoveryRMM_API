using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Como um comando saiu do dispatch.
///
/// Existe para o chamador distinguir "já está no fio" de "só ficou na fila".
/// Antes, <see cref="IAgentCommandDispatcher.DispatchAsync"/> devolvia o comando
/// sem informar o desfecho e o painel tratava Pending como sucesso — o operador
/// via "Comando de desinstalação reenviado" para um agente offline que jamais o
/// receberia.
/// </summary>
public enum CommandDispatchOutcome
{
    /// <summary>
    /// Comando publicado no transporte E o agente estava online no momento do
    /// envio. É o único desfecho que autoriza a UI a dizer "enviado".
    /// </summary>
    Sent,

    /// <summary>
    /// Comando persistido como Pending: ou o transporte estava indisponível, ou
    /// o agente estava OFFLINE. Nos dois casos ninguém recebeu a mensagem ainda
    /// e a entrega depende da reentrega quando o agente reconectar.
    ///
    /// Atenção: publicar em NATS core é fire-and-forget — `SendCommandAsync`
    /// conclui sem erro mesmo sem nenhum assinante. Por isso o estado do agente
    /// precisa ser consultado separadamente; sem isso, "publicado com sucesso"
    /// seria reportado como sucesso para um agente que nunca receberia nada.
    /// </summary>
    Queued
}

/// <summary>Resultado explícito de um dispatch: o comando e o que aconteceu com ele.</summary>
public readonly record struct AgentCommandDispatchResult(AgentCommand Command, CommandDispatchOutcome Outcome)
{
    public bool WasSent => Outcome == CommandDispatchOutcome.Sent;
}

public interface IAgentCommandDispatcher
{
    /// <summary>
    /// Persiste e tenta enviar o comando. Mantido por compatibilidade: NÃO
    /// informa o desfecho — use <see cref="DispatchWithOutcomeAsync"/> quando o
    /// chamador precisar saber se o comando foi realmente enviado ou apenas
    /// enfileirado (ex.: telas que confirmam a ação ao operador).
    /// </summary>
    Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual a <see cref="DispatchAsync"/>, mas informa se o comando foi
    /// efetivamente publicado ou apenas persistido como Pending.
    /// </summary>
    Task<AgentCommandDispatchResult> DispatchWithOutcomeAsync(AgentCommand command, CancellationToken cancellationToken = default);
}
