using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

public class AgentCommandDispatcher(
    ICommandRepository commandRepository,
    IAgentMessaging messaging,
    SpecialCommandPayloadValidator specialCommandPayloadValidator,
    ILogger<AgentCommandDispatcher> logger) : IAgentCommandDispatcher
{
    public async Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
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

        if (messaging.IsConnected)
        {
            try
            {
                await messaging.SendCommandAsync(created.AgentId, created.Id, wireCommandType, created.Payload);
                sent = true;
                sentAtUtc = DateTime.UtcNow;
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
        }
        else
        {
            logger.LogDebug(
                "Command {CommandId} for agent {AgentId} persisted as pending because no realtime transport was available.",
                created.Id,
                created.AgentId);
        }

        return created;
    }
}