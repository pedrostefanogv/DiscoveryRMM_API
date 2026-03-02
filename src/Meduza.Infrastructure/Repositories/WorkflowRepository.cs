using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class WorkflowRepository : IWorkflowRepository
{
    private readonly IDbConnectionFactory _db;

    public WorkflowRepository(IDbConnectionFactory db) => _db = db;

    // --- States ---

    public async Task<WorkflowState?> GetStateByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<WorkflowState>(
            """
            SELECT id, client_id AS ClientId, name, color, is_initial AS IsInitial,
                   is_final AS IsFinal, sort_order AS SortOrder, created_at AS CreatedAt
            FROM workflow_states WHERE id = @Id
            """, new { Id = id });
    }

    public async Task<IEnumerable<WorkflowState>> GetStatesAsync(Guid? clientId = null)
    {
        using var conn = _db.CreateConnection();
        // Retorna estados globais (client_id IS NULL) + do client
        return await conn.QueryAsync<WorkflowState>(
            """
            SELECT id, client_id AS ClientId, name, color, is_initial AS IsInitial,
                   is_final AS IsFinal, sort_order AS SortOrder, created_at AS CreatedAt
            FROM workflow_states
            WHERE client_id IS NULL OR client_id = @ClientId
            ORDER BY sort_order, name
            """, new { ClientId = clientId });
    }

    public async Task<WorkflowState?> GetInitialStateAsync(Guid? clientId = null)
    {
        using var conn = _db.CreateConnection();
        // Prioriza estado inicial do client; se não tiver, pega o global
        return await conn.QueryFirstOrDefaultAsync<WorkflowState>(
            """
            SELECT id, client_id AS ClientId, name, color, is_initial AS IsInitial,
                   is_final AS IsFinal, sort_order AS SortOrder, created_at AS CreatedAt
            FROM workflow_states
            WHERE is_initial = true AND (client_id = @ClientId OR client_id IS NULL)
            ORDER BY CASE WHEN client_id IS NOT NULL THEN 0 ELSE 1 END
            LIMIT 1
            """, new { ClientId = clientId });
    }

    public async Task<WorkflowState> CreateStateAsync(WorkflowState state)
    {
        state.Id = IdGenerator.NewId();
        state.CreatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO workflow_states (id, client_id, name, color, is_initial, is_final, sort_order, created_at)
            VALUES (@Id, @ClientId, @Name, @Color, @IsInitial, @IsFinal, @SortOrder, @CreatedAt)
            """, state);
        return state;
    }

    public async Task UpdateStateAsync(WorkflowState state)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE workflow_states SET name = @Name, color = @Color, is_initial = @IsInitial,
                   is_final = @IsFinal, sort_order = @SortOrder
            WHERE id = @Id
            """, state);
    }

    public async Task DeleteStateAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM workflow_states WHERE id = @Id", new { Id = id });
    }

    // --- Transitions ---

    public async Task<IEnumerable<WorkflowTransition>> GetTransitionsAsync(Guid? clientId = null)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<WorkflowTransition>(
            """
            SELECT id, client_id AS ClientId, from_state_id AS FromStateId,
                   to_state_id AS ToStateId, name, created_at AS CreatedAt
            FROM workflow_transitions
            WHERE client_id IS NULL OR client_id = @ClientId
            """, new { ClientId = clientId });
    }

    public async Task<IEnumerable<WorkflowTransition>> GetTransitionsFromStateAsync(Guid fromStateId, Guid? clientId = null)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<WorkflowTransition>(
            """
            SELECT id, client_id AS ClientId, from_state_id AS FromStateId,
                   to_state_id AS ToStateId, name, created_at AS CreatedAt
            FROM workflow_transitions
            WHERE from_state_id = @FromStateId AND (client_id IS NULL OR client_id = @ClientId)
            """, new { FromStateId = fromStateId, ClientId = clientId });
    }

    public async Task<bool> IsTransitionValidAsync(Guid fromStateId, Guid toStateId, Guid? clientId = null)
    {
        using var conn = _db.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(1) FROM workflow_transitions
            WHERE from_state_id = @FromStateId AND to_state_id = @ToStateId
            AND (client_id IS NULL OR client_id = @ClientId)
            """, new { FromStateId = fromStateId, ToStateId = toStateId, ClientId = clientId });
        return count > 0;
    }

    public async Task<WorkflowTransition> CreateTransitionAsync(WorkflowTransition transition)
    {
        transition.Id = IdGenerator.NewId();
        transition.CreatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO workflow_transitions (id, client_id, from_state_id, to_state_id, name, created_at)
            VALUES (@Id, @ClientId, @FromStateId, @ToStateId, @Name, @CreatedAt)
            """, transition);
        return transition;
    }

    public async Task DeleteTransitionAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM workflow_transitions WHERE id = @Id", new { Id = id });
    }
}
