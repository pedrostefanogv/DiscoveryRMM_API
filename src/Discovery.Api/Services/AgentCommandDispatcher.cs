using Discovery.Api.Hubs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Discovery.Api.Services;

public class AgentCommandDispatcher(
    ICommandRepository commandRepository,
    IAgentMessaging messaging,
    IHubContext<AgentHub> hubContext,
    ILogger<AgentCommandDispatcher> logger) : IAgentCommandDispatcher
{
    public async Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var created = await commandRepository.CreateAsync(command);
        var sent = false;
        DateTime? sentAtUtc = null;

        if (messaging.IsConnected)
        {
            try
            {
                await messaging.SendCommandAsync(created.AgentId, created.Id, created.CommandType.ToString(), created.Payload);
                sent = true;
                sentAtUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Realtime command dispatch via NATS failed for agent {AgentId}, command {CommandId}. Falling back to SignalR if available.",
                    created.AgentId,
                    created.Id);
            }
        }

        if (!sent && AgentHub.IsAgentConnected(created.AgentId))
        {
            await hubContext.Clients.Group($"agent-{created.AgentId}")
                .SendAsync("ExecuteCommand", created.Id, created.CommandType, created.Payload, cancellationToken);

            sent = true;
            sentAtUtc = DateTime.UtcNow;
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