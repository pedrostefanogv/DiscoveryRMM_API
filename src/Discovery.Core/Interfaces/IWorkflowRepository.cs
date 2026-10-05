using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IWorkflowRepository
{
    // States
    Task<WorkflowState?> GetStateByIdAsync(Guid id);
    Task<IEnumerable<WorkflowState>> GetStatesAsync(Guid? clientId = null);
    Task<WorkflowState?> GetInitialStateAsync(Guid? clientId = null);
    /// <summary>Existe um estado inicial no escopo exato (mesmo ClientId)?</summary>
    Task<bool> HasInitialStateAsync(Guid? clientId, Guid? excludeId = null);
    Task<WorkflowState> CreateStateAsync(WorkflowState state);
    Task UpdateStateAsync(WorkflowState state);
    Task DeleteStateAsync(Guid id);
    /// <summary>Quantos chamados não excluídos referenciam o estado.</summary>
    Task<int> CountTicketsInStateAsync(Guid stateId);

    // Transitions
    Task<IEnumerable<WorkflowTransition>> GetTransitionsAsync(Guid? clientId = null);
    Task<IEnumerable<WorkflowTransition>> GetTransitionsFromStateAsync(Guid fromStateId, Guid? clientId = null);
    Task<bool> IsTransitionValidAsync(Guid fromStateId, Guid toStateId, Guid? clientId = null);
    /// <summary>Já existe uma transição From→To no escopo efetivo do cliente?</summary>
    Task<bool> TransitionExistsAsync(Guid fromStateId, Guid toStateId, Guid? clientId);
    Task<WorkflowTransition> CreateTransitionAsync(WorkflowTransition transition);
    Task DeleteTransitionAsync(Guid id);
}
