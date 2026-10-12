using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Política de reentrega por tipo de comando.
///
/// Dois casos são restringidos hoje:
///
/// 1. Convite de chat de suporte: vale enquanto a sessão estiver Pending e
///    dentro do prazo do convite. Nos demais casos o comando é encerrado como
///    terminal (Cancelled) e não vai à rede — sem isso o agente recebia um
///    convite de uma conversa que já tinha terminado e abria a janela do chat
///    "do nada".
///
/// 2. Comando de energia (restart/shutdown): depois da primeira publicação não
///    é mais reenviado (ver <see cref="EvaluatePowerRedelivery"/>).
/// </summary>
public sealed class CommandRedeliveryGuard(
    ITicketChatRepository chatRepository,
    // Opcional de propósito: se o host não registrar TimeProvider, a guarda
    // ainda resolve (System) — uma dependência ausente aqui derrubaria a
    // reentrega de TODOS os comandos, não só a do chat.
    TimeProvider? timeProvider = null) : ICommandRedeliveryGuard
{
    public async Task<CommandRedeliveryDecision> EvaluateAsync(
        AgentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Decisão sobre energia é puramente local (não consulta banco).
        if (IsPowerCommand(command.CommandType))
            return EvaluatePowerRedelivery(command);

        if (command.CommandType != CommandType.TicketChatInvite)
            return CommandRedeliveryDecision.Allow;

        if (!TryReadSessionId(command.Payload, out var sessionId))
            return CommandRedeliveryDecision.Block("convite de chat sem sessionId válido");

        var session = await chatRepository.GetByIdAsync(sessionId, cancellationToken);

        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        return EvaluateSession(session, nowUtc);
    }

    /// <summary>
    /// Comandos que reiniciam/desligam a máquina: a entrega derruba o próprio
    /// agente que confirmaria o comando, então cada reentrega é um novo reboot.
    /// </summary>
    internal static bool IsPowerCommand(CommandType commandType)
        => commandType is CommandType.Restart or CommandType.Shutdown;

    /// <summary>
    /// Caso real: 1 (um) restart solicitado pelo console virou 54 reboots em ~5h,
    /// um a cada ~6 min (IntervalSeconds=120, RetryGraceSeconds=300). O Windows
    /// reinicia antes de o agente publicar o result/ack, então o comando nunca
    /// era confirmado e a reentrega o reenviava até a retenção de 24h. A
    /// deduplicação por CommandId no agente (TTL 24h) não protege aqui: o estado
    /// dela morre junto com o processo no reboot.
    ///
    /// Regra: só reentrega enquanto o comando NUNCA foi publicado. O dispatch
    /// inicial grava Status=Sent/SentAt apenas quando o publish no NATS dá certo
    /// (AgentCommandDispatcher); logo Sent, Running ou SentAt preenchido provam
    /// que uma entrega já saiu. Nesse caso a decisão é Ignore (não reenvia e não
    /// cancela), e a linha segue até expirar pela janela de retenção.
    ///
    /// O que a regra preserva: comando de energia criado com o transporte fora
    /// do ar fica Pending com SentAt nulo e continua elegível à reentrega —
    /// senão o restart pedido com o NATS caído nunca chegaria ao agente.
    ///
    /// Limitação aceita: Sent indica que o TRANSPORTE DO SERVIDOR estava
    /// conectado (AgentCommandDispatcher só olha messaging.IsConnected), não que
    /// aquele agente recebeu. Um comando de energia publicado com o transporte no
    /// ar mas o agente offline não será reentregue: ele expira pela janela de
    /// retenção e é encerrado como Timeout ("Não entregue: agente offline além da
    /// janela de reentrega."), com motivo explícito no histórico e reenvio manual
    /// pelo operador. Trade-off deliberado — um comando não executado e VISÍVEL é
    /// melhor que um loop de reinício (54 reboots em ~5h no incidente que
    /// originou esta regra).
    /// </summary>
    internal static CommandRedeliveryDecision EvaluatePowerRedelivery(AgentCommand command)
    {
        var alreadyPublished = command.SentAt is not null
            || command.Status is CommandStatus.Sent or CommandStatus.Running;

        if (!alreadyPublished)
            return CommandRedeliveryDecision.Allow;

        return CommandRedeliveryDecision.Ignore(
            "comando de energia já publicado; reentrega repetiria o reinício/desligamento");
    }

    /// <summary>
    /// Decisão pura (sem I/O) a partir da sessão persistida — pura para ser
    /// testável sem banco.
    /// </summary>
    internal static CommandRedeliveryDecision EvaluateSession(TicketChatSession? session, DateTime nowUtc)
    {
        if (session is null)
            return CommandRedeliveryDecision.Block("sessão de chat não encontrada");

        if (session.Status != TicketChatSessionStatus.Pending)
            return CommandRedeliveryDecision.Block($"sessão de chat já não está pendente ({session.Status})");

        if (session.InviteExpiresAt is not null && session.InviteExpiresAt <= nowUtc)
            return CommandRedeliveryDecision.Block("convite de chat expirado");

        return CommandRedeliveryDecision.Allow;
    }

    /// <summary>Lê o sessionId do payload JSON do comando.</summary>
    internal static bool TryReadSessionId(string? payload, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!document.RootElement.TryGetProperty("sessionId", out var property))
                return false;

            if (property.ValueKind != JsonValueKind.String)
                return false;

            return Guid.TryParse(property.GetString(), out sessionId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
