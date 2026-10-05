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

    public async Task<bool> HasInitialStateAsync(Guid? clientId, Guid? excludeId = null)
    {
        return await _db.WorkflowStates
            .AsNoTracking()
            .AnyAsync(state => state.IsInitial
                && state.ClientId == clientId
                && (excludeId == null || state.Id != excludeId));
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
        // PausesSla ficou de fora deste mapeamento: o toggle "pausar SLA" era
        // aceito pela API e devolvido no DTO, mas nunca chegava ao banco.
        existingState.PausesSla = state.PausesSla;

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Remove o estado e os dados que o referenciam (transições de/para ele e
    /// regras de alerta vinculadas), dentro de uma transação. Não remove
    /// chamados: o handler bloqueia a exclusão quando existem chamados no estado.
    /// </summary>
    public async Task DeleteStateAsync(Guid id)
    {
        // Transação explícita no provedor relacional (produção). O InMemory não
        // suporta transações e o SaveChanges já é atômico, então é dispensada.
        var tx = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            var transitions = await _db.WorkflowTransitions
                .Where(transition => transition.FromStateId == id || transition.ToStateId == id)
                .ToListAsync();
            _db.WorkflowTransitions.RemoveRange(transitions);

            var alertRules = await _db.TicketAlertRules
                .Where(rule => rule.WorkflowStateId == id)
                .ToListAsync();
            _db.TicketAlertRules.RemoveRange(alertRules);

            var state = await _db.WorkflowStates.SingleOrDefaultAsync(existing => existing.Id == id);
            if (state is not null)
                _db.WorkflowStates.Remove(state);

            await _db.SaveChangesAsync();
            if (tx is not null) await tx.CommitAsync();
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    public async Task<int> CountTicketsInStateAsync(Guid stateId)
    {
        return await _db.Tickets
            .AsNoTracking()
            .CountAsync(ticket => ticket.DeletedAt == null && ticket.WorkflowStateId == stateId);
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

    public async Task<bool> TransitionExistsAsync(Guid fromStateId, Guid toStateId, Guid? clientId)
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
