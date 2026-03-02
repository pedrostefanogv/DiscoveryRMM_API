using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class CommandRepository : ICommandRepository
{
    private readonly IDbConnectionFactory _db;

    public CommandRepository(IDbConnectionFactory db) => _db = db;

    public async Task<AgentCommand?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<AgentCommand>(
            """
            SELECT id, agent_id AS AgentId, command_type AS CommandType, payload,
                   status, result, exit_code AS ExitCode, error_message AS ErrorMessage,
                   created_at AS CreatedAt, sent_at AS SentAt, completed_at AS CompletedAt
            FROM agent_commands WHERE id = @Id
            """, new { Id = id });
    }

    public async Task<IEnumerable<AgentCommand>> GetPendingByAgentIdAsync(Guid agentId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<AgentCommand>(
            """
            SELECT id, agent_id AS AgentId, command_type AS CommandType, payload,
                   status, result, exit_code AS ExitCode, error_message AS ErrorMessage,
                   created_at AS CreatedAt, sent_at AS SentAt, completed_at AS CompletedAt
            FROM agent_commands WHERE agent_id = @AgentId AND status = @Status
            ORDER BY created_at ASC
            """, new { AgentId = agentId, Status = (int)CommandStatus.Pending });
    }

    public async Task<IEnumerable<AgentCommand>> GetByAgentIdAsync(Guid agentId, int limit = 50)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<AgentCommand>(
            """
            SELECT id, agent_id AS AgentId, command_type AS CommandType, payload,
                   status, result, exit_code AS ExitCode, error_message AS ErrorMessage,
                   created_at AS CreatedAt, sent_at AS SentAt, completed_at AS CompletedAt
            FROM agent_commands WHERE agent_id = @AgentId
            ORDER BY created_at DESC LIMIT @Limit
            """, new { AgentId = agentId, Limit = limit });
    }

    public async Task<AgentCommand> CreateAsync(AgentCommand command)
    {
        command.Id = IdGenerator.NewId();
        command.CreatedAt = DateTime.UtcNow;
        command.Status = CommandStatus.Pending;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO agent_commands (id, agent_id, command_type, payload, status, created_at)
            VALUES (@Id, @AgentId, @CommandType, @Payload, @Status, @CreatedAt)
            """, command);
        return command;
    }

    public async Task UpdateStatusAsync(Guid id, CommandStatus status, string? result, int? exitCode, string? errorMessage)
    {
        using var conn = _db.CreateConnection();
        var now = DateTime.UtcNow;
        await conn.ExecuteAsync(
            """
            UPDATE agent_commands SET status = @Status, result = @Result, exit_code = @ExitCode,
                   error_message = @ErrorMessage,
                   sent_at = CASE WHEN @Status = 1 THEN @Now ELSE sent_at END,
                   completed_at = CASE WHEN @Status IN (3, 4, 6) THEN @Now ELSE completed_at END
            WHERE id = @Id
            """, new { Id = id, Status = (int)status, Result = result, ExitCode = exitCode, ErrorMessage = errorMessage, Now = now });
    }
}
