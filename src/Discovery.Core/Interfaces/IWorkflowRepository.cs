using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IWorkflowRepository
{
    // States
    Task<WorkflowState?> GetStateByIdAsync(Guid id);
    Task<IEnumerable<WorkflowState>> GetStatesAsync(Guid? clientId = null);
    Task<WorkflowState?> GetInitialStateAsync(Guid? clientId = null);
    Task<WorkflowState> CreateStateAsync(WorkflowState state);
    Task UpdateStateAsync(WorkflowState state);
    Task DeleteStateAsync(Guid id);

    // Transitions
    Task<IEnumerable<WorkflowTransition>> GetTransitionsAsync(Guid? clientId = null);
    Task<IEnumerable<WorkflowTransition>> GetTransitionsFromStateAsync(Guid fromStateId, Guid? clientId = null);
    Task<bool> IsTransitionValidAsync(Guid fromStateId, Guid toStateId, Guid? clientId = null);
    Task<WorkflowTransition> CreateTransitionAsync(WorkflowTransition transition);
    Task DeleteTransitionAsync(Guid id);
}
