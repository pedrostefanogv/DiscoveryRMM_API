using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Política de reentrega por tipo de comando.
///
/// Hoje só o convite de chat de suporte é restringido: ele vale enquanto a
/// sessão estiver Pending e dentro do prazo do convite. Nos demais casos o
/// comando é encerrado como terminal (Cancelled) e não vai à rede — sem isso o
/// agente recebia um convite de uma conversa que já tinha terminado e abria a
/// janela do chat "do nada".
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

        if (command.CommandType != CommandType.TicketChatInvite)
            return CommandRedeliveryDecision.Allow;

        if (!TryReadSessionId(command.Payload, out var sessionId))
            return CommandRedeliveryDecision.Block("convite de chat sem sessionId válido");

        var session = await chatRepository.GetByIdAsync(sessionId, cancellationToken);

        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        return EvaluateSession(session, nowUtc);
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
