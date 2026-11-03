using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Exceptions;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Operações imperativas de sensores: executar agora, resetar estado e forçar
/// sync. Usa o AgentCommandDispatcher existente (NATS); se o agente estiver
/// offline o comando fica pendente e a resposta indica não despachado.
/// </summary>
public sealed class SensorOperationService(
    DiscoveryDbContext db,
    IAgentCommandDispatcher dispatcher,
    ISensorDefinitionService definitionService) : ISensorOperationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SensorRunResponseDto> RunAsync(Guid agentId, IReadOnlyList<Guid>? sensorIds, CancellationToken cancellationToken = default)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new SensorValidationException("Agente não encontrado.");

        var applicable = await definitionService.ResolveApplicableAsync(agentId, cancellationToken);
        var knownIds = applicable.Select(s => s.Id).ToHashSet();
        var requested = (sensorIds ?? []).Where(knownIds.Contains).Distinct().ToList();

        var command = new AgentCommand
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            CommandType = CommandType.RunSensors,
            Payload = JsonSerializer.Serialize(new { sensorIds = requested, forceSync = false }, JsonOptions),
            Status = CommandStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        var dispatched = await dispatcher.DispatchAsync(command, cancellationToken);

        return new SensorRunResponseDto
        {
            CommandId = dispatched.Id,
            Dispatched = dispatched.Status is CommandStatus.Sent,
            SensorCount = requested.Count > 0 ? requested.Count : applicable.Count,
            Message = dispatched.Status is CommandStatus.Sent
                ? "Execução solicitada ao agente."
                : "Agente offline: o comando ficará pendente e será entregue quando ele reconectar."
        };
    }

    public async Task<SensorRunResponseDto> SyncNowAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var exists = await db.Agents.AsNoTracking().AnyAsync(a => a.Id == agentId, cancellationToken);
        if (!exists) throw new SensorValidationException("Agente não encontrado.");

        var command = new AgentCommand
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            CommandType = CommandType.RunSensors,
            Payload = JsonSerializer.Serialize(new { sensorIds = Array.Empty<Guid>(), forceSync = true }, JsonOptions),
            Status = CommandStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        var dispatched = await dispatcher.DispatchAsync(command, cancellationToken);
        return new SensorRunResponseDto
        {
            CommandId = dispatched.Id,
            Dispatched = dispatched.Status is CommandStatus.Sent,
            SensorCount = 0,
            Message = dispatched.Status is CommandStatus.Sent
                ? "Sincronização de sensores solicitada ao agente."
                : "Agente offline: a sincronização será feita no próximo contato."
        };
    }

    public async Task<int> ResetAsync(Guid agentId, Guid? sensorId, CancellationToken cancellationToken = default)
    {
        var query = db.AgentSensorStates.Where(s => s.AgentId == agentId);
        if (sensorId is not null) query = query.Where(s => s.SensorId == sensorId);

        var states = await query.ToListAsync(cancellationToken);
        if (states.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var state in states)
        {
            state.Status = SensorStatus.Pending;
            state.Severity = null;
            state.Message = "Estado reiniciado pelo operador.";
            state.ConsecutiveFailures = 0;
            state.LastAlertedAt = null;
            state.LastAlertSeverity = null;
            state.LastStatusChangedAt = now;
            state.LastRunId = null;
            state.UpdatedAt = now;

            db.AgentSensorEvents.Add(new AgentSensorEvent
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                SensorId = state.SensorId,
                TargetKey = state.TargetKey,
                // RunId por estado: o índice único é (agent, run, kind).
                RunId = Guid.NewGuid(),
                Kind = SensorEventKind.Reset,
                Status = SensorStatus.Pending,
                Message = "Estado reiniciado pelo operador.",
                OccurredAt = now,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return states.Count;
    }
}
