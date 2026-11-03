using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Repositories;

public class CommandRepository : ICommandRepository
{
    private readonly DiscoveryDbContext _db;

    // Logger opcional: os testes constroem o repositório só com o DbContext.
    private readonly ILogger<CommandRepository>? _logger;

    public CommandRepository(DiscoveryDbContext db, ILogger<CommandRepository>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AgentCommand?> GetByIdAsync(Guid id)
    {
        return await _db.AgentCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(command => command.Id == id);
    }

    public async Task<IEnumerable<AgentCommand>> GetPendingByAgentIdAsync(Guid agentId)
    {
        return await _db.AgentCommands
            .AsNoTracking()
            .Where(command => command.AgentId == agentId && command.Status == CommandStatus.Pending)
            .OrderBy(command => command.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<AgentCommand>> GetByAgentIdAsync(Guid agentId, int limit = 50)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);

        return await _db.AgentCommands
            .AsNoTracking()
            .Where(command => command.AgentId == agentId)
            .OrderByDescending(command => command.CreatedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<AgentCommand>> GetRedeliveryCandidatesAsync(
        IReadOnlyCollection<Guid> agentIds,
        DateTime createdAfterUtc,
        DateTime staleBeforeUtc,
        int limit,
        CancellationToken ct = default)
    {
        if (agentIds.Count == 0)
            return [];

        var safeLimit = Math.Clamp(limit, 1, 1000);
        var effectiveIds = agentIds.Distinct().ToList();

        return await _db.AgentCommands
            .AsNoTracking()
            .Where(command => effectiveIds.Contains(command.AgentId)
                && command.CreatedAt >= createdAfterUtc
                && (command.Status == CommandStatus.Pending
                    || command.Status == CommandStatus.Sent
                    || command.Status == CommandStatus.Running)
                && ((command.SentAt == null && command.CreatedAt <= staleBeforeUtc)
                    || (command.SentAt != null && command.SentAt < staleBeforeUtc)))
            .OrderBy(command => command.CreatedAt)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AgentCommand>> GetExpiredUnconfirmedAsync(
        DateTime createdBeforeUtc,
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);

        // DecommissionAgent fica FORA da expiração: ele tem que sobreviver a
        // janelas longas de agente offline/NATS indisponível — é o comando que
        // desinstala o PC, e expirá-lo silently deixaria a máquina instalada
        // para sempre com a lixeira já commitada no painel. O ciclo fecha via
        // reentrega (PendingCommandRedeliveryService) ou o purge do painel
        // (que apaga o registro por retenção — o agente tem TTL próprio de 30d).
        return await _db.AgentCommands
            .AsNoTracking()
            .Where(command => command.CreatedAt < createdBeforeUtc
                && command.CommandType != CommandType.DecommissionAgent
                && (command.Status == CommandStatus.Pending
                    || command.Status == CommandStatus.Sent
                    || command.Status == CommandStatus.Running))
            .OrderBy(command => command.CreatedAt)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public async Task<AgentCommand> CreateAsync(AgentCommand command)
    {
        command.Id = IdGenerator.NewId();
        command.CreatedAt = DateTime.UtcNow;
        command.Status = CommandStatus.Pending;

        _db.AgentCommands.Add(command);
        await _db.SaveChangesAsync();
        return command;
    }

    public async Task UpdateStatusAsync(Guid id, CommandStatus status, string? result, int? exitCode, string? errorMessage)
    {
        var now = DateTime.UtcNow;

        // O result do agente pode chegar mais de uma vez (ack antecipado do comando
        // de energia + resultado final + reentrega atrasada). Sem esta guarda, um
        // ack tardio reabre um comando terminal e uma falha vira "sucesso".
        var current = await _db.AgentCommands
            .AsNoTracking()
            .Where(command => command.Id == id)
            .Select(command => (CommandStatus?)command.Status)
            .SingleOrDefaultAsync();

        if (current is null)
            return;

        if (!CommandStatusTransitions.ShouldApply(current.Value, status))
        {
            _logger?.LogWarning(
                "Transição de status recusada para o comando {CommandId}: {CurrentStatus} -> {NextStatus} (resultado tardio não pode reabrir/limpar o veredito).",
                id, current.Value, status);
            return;
        }

        await _db.AgentCommands
            .Where(command => command.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(command => command.Status, _ => status)
                .SetProperty(command => command.Result, _ => result)
                .SetProperty(command => command.ExitCode, _ => exitCode)
                .SetProperty(command => command.ErrorMessage, _ => errorMessage)
                .SetProperty(command => command.SentAt,
                    command => status == CommandStatus.Sent ? now : command.SentAt)
                .SetProperty(command => command.CompletedAt,
                    command => status == CommandStatus.Completed
                        || status == CommandStatus.Failed
                        || status == CommandStatus.Timeout
                        || status == CommandStatus.Cancelled
                        || status == CommandStatus.Deferred
                        ? now
                        : command.CompletedAt));
    }
}
