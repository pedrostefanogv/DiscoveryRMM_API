using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IWorkflowProfileRepository
{
    /// <summary>
    /// Obtém um perfil de workflow pelo ID.
    /// </summary>
    Task<WorkflowProfile?> GetByIdAsync(Guid id);
    
    /// <summary>
    /// Obtém todos os perfis globais (ClientId = null).
    /// </summary>
    Task<List<WorkflowProfile>> GetGlobalAsync();
    
    /// <summary>
    /// Obtém perfis de um cliente, opcionalmente incluindo globais.
    /// </summary>
    Task<List<WorkflowProfile>> GetByClientAsync(Guid? clientId, bool includeGlobal = true);
    
    /// <summary>
    /// Obtém perfis de um departamento.
    /// </summary>
    Task<List<WorkflowProfile>> GetByDepartmentAsync(Guid departmentId);
    
    /// <summary>
    /// Obtém o perfil padrão de um departamento para um cliente.
    /// </summary>
    Task<WorkflowProfile?> GetDefaultByDepartmentAsync(Guid departmentId);
    
    /// <summary>
    /// Cria um novo perfil de workflow.
    /// </summary>
    Task<WorkflowProfile> CreateAsync(WorkflowProfile profile);
    
    /// <summary>
    /// Atualiza um perfil de workflow.
    /// </summary>
    Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile);
    
    /// <summary>
    /// Deleta um perfil de workflow (soft-delete recomendado).
    /// </summary>
    Task<bool> DeleteAsync(Guid id);
}
