using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class CommandRepository : ICommandRepository
{
    private readonly DiscoveryDbContext _db;

    public CommandRepository(DiscoveryDbContext db) => _db = db;

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
                        ? now
                        : command.CompletedAt));
    }
}
