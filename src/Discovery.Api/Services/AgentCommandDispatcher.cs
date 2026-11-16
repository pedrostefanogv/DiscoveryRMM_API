using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

public class AgentCommandDispatcher(
    ICommandRepository commandRepository,
    IAgentMessaging messaging,
    IHeartbeatCacheService heartbeatCache,
    SpecialCommandPayloadValidator specialCommandPayloadValidator,
    ILogger<AgentCommandDispatcher> logger) : IAgentCommandDispatcher
{
    public async Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        => (await DispatchWithOutcomeAsync(command, cancellationToken)).Command;

    public async Task<AgentCommandDispatchResult> DispatchWithOutcomeAsync(
        AgentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!specialCommandPayloadValidator.TryNormalize(
                command.CommandType,
                command.Payload,
                out var normalizedPayload,
                out var validationError))
        {
            throw new InvalidOperationException(
                $"Invalid payload for commandType '{CommandTypeWireMapper.ToWireValue(command.CommandType)}': {validationError}");
        }

        command.Payload = normalizedPayload;

        var created = await commandRepository.CreateAsync(command);
        var sent = false;
        DateTime? sentAtUtc = null;
        var wireCommandType = CommandTypeWireMapper.ToWireValue(created.CommandType);

        // O transporte não está conectado: o comando fica Pending e depende da
        // reentrega para algum dia chegar. O chamador PRECISA saber disso.
        if (messaging.IsConnected)
        {
            try
            {
                await messaging.SendCommandAsync(created.AgentId, created.Id, wireCommandType, created.Payload);

                // Publicar em NATS core é fire-and-forget: a chamada acima conclui
                // SEM erro mesmo quando não há nenhum assinante do outro lado. Sem
                // esta checagem, um agente OFFLINE recebia o mesmo desfecho de um
                // online e a UI prometia uma entrega que não aconteceu.
                if (await IsAgentReachableAsync(created.AgentId, cancellationToken))
                {
                    sent = true;
                    sentAtUtc = DateTime.UtcNow;
                }
                else
                {
                    logger.LogInformation(
                        "Command {CommandId} ({CommandType}) published for agent {AgentId} but the agent is OFFLINE — treating as queued; redelivery will deliver it once the agent reconnects.",
                        created.Id,
                        wireCommandType,
                        created.AgentId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Realtime command dispatch via NATS failed for agent {AgentId}, command {CommandId}.",
                    created.AgentId,
                    created.Id);
            }
        }

        if (sent)
        {
            await commandRepository.UpdateStatusAsync(created.Id, CommandStatus.Sent, null, null, null);
            created.Status = CommandStatus.Sent;
            created.SentAt = sentAtUtc;
            return new AgentCommandDispatchResult(created, CommandDispatchOutcome.Sent);
        }

        // Pending: NÃO é sucesso de entrega. Só sai da fila quando o agente
        // reconectar E for alcançado pela reentrega.
        logger.LogInformation(
            "Command {CommandId} ({CommandType}) for agent {AgentId} persisted as pending — no realtime transport available; delivery depends on redelivery once the agent reconnects.",
            created.Id,
            wireCommandType,
            created.AgentId);

        return new AgentCommandDispatchResult(created, CommandDispatchOutcome.Queued);
    }
    /// <summary>
    /// O agente tem heartbeat ativo (chave em Redis)? É a fonte de verdade de
    /// "online agora" (ver IHeartbeatCacheService). Fail-open deliberado: se a
    /// consulta falhar, não rebaixamos um comando que pode ter sido entregue —
    /// o pior caso é a UI informar "enviado" para um agente que ficou offline
    /// no exato instante do envio, e a reentrega ainda corrige o estado.
    /// </summary>
    private async Task<bool> IsAgentReachableAsync(Guid agentId, CancellationToken ct)
    {
        try
        {
            return await heartbeatCache.GetHeartbeatAsync(agentId, ct) is not null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Não foi possível verificar se o agente {AgentId} está online.", agentId);
            return true;
        }
    }
}
