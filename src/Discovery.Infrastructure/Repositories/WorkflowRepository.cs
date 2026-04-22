using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class WorkflowRepository : IWorkflowRepository
{
    private readonly DiscoveryDbContext _db;

    public WorkflowRepository(DiscoveryDbContext db) => _db = db;

    // --- States ---

    public async Task<WorkflowState?> GetStateByIdAsync(Guid id)
    {
        return await _db.WorkflowStates
            .AsNoTracking()
            .SingleOrDefaultAsync(state => state.Id == id);
    }

    public async Task<IEnumerable<WorkflowState>> GetStatesAsync(Guid? clientId = null)
    {
        return await _db.WorkflowStates
            .AsNoTracking()
            .Where(state => state.ClientId == null || state.ClientId == clientId)
            .OrderBy(state => state.SortOrder)
            .ThenBy(state => state.Name)
            .ToListAsync();
    }

    public async Task<WorkflowState?> GetInitialStateAsync(Guid? clientId = null)
    {
        return await _db.WorkflowStates
            .AsNoTracking()
            .Where(state => state.IsInitial && (state.ClientId == clientId || state.ClientId == null))
            .OrderBy(state => state.ClientId == null ? 1 : 0)
            .ThenBy(state => state.SortOrder)
            .FirstOrDefaultAsync();
    }

    public async Task<WorkflowState> CreateStateAsync(WorkflowState state)
    {
        state.Id = IdGenerator.NewId();
        state.CreatedAt = DateTime.UtcNow;

        _db.WorkflowStates.Add(state);
        await _db.SaveChangesAsync();
        return state;
    }

    public async Task UpdateStateAsync(WorkflowState state)
    {
        var existingState = await _db.WorkflowStates.SingleOrDefaultAsync(existing => existing.Id == state.Id);
        if (existingState is null)
            return;

        existingState.Name = state.Name;
        existingState.Color = state.Color;
        existingState.IsInitial = state.IsInitial;
        existingState.IsFinal = state.IsFinal;
        existingState.SortOrder = state.SortOrder;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteStateAsync(Guid id)
    {
        await _db.WorkflowStates
            .Where(state => state.Id == id)
            .ExecuteDeleteAsync();
    }

    // --- Transitions ---

    public async Task<IEnumerable<WorkflowTransition>> GetTransitionsAsync(Guid? clientId = null)
    {
        return await _db.WorkflowTransitions
            .AsNoTracking()
            .Where(transition => transition.ClientId == null || transition.ClientId == clientId)
            .ToListAsync();
    }

    public async Task<IEnumerable<WorkflowTransition>> GetTransitionsFromStateAsync(Guid fromStateId, Guid? clientId = null)
    {
        return await _db.WorkflowTransitions
            .AsNoTracking()
            .Where(transition => transition.FromStateId == fromStateId
                && (transition.ClientId == null || transition.ClientId == clientId))
            .ToListAsync();
    }

    public async Task<bool> IsTransitionValidAsync(Guid fromStateId, Guid toStateId, Guid? clientId = null)
    {
        return await _db.WorkflowTransitions
            .AsNoTracking()
            .AnyAsync(transition => transition.FromStateId == fromStateId
                && transition.ToStateId == toStateId
                && (transition.ClientId == null || transition.ClientId == clientId));
    }

    public async Task<WorkflowTransition> CreateTransitionAsync(WorkflowTransition transition)
    {
        transition.Id = IdGenerator.NewId();
        transition.CreatedAt = DateTime.UtcNow;

        _db.WorkflowTransitions.Add(transition);
        await _db.SaveChangesAsync();
        return transition;
    }

    public async Task DeleteTransitionAsync(Guid id)
    {
        await _db.WorkflowTransitions
            .Where(transition => transition.Id == id)
            .ExecuteDeleteAsync();
    }
}
