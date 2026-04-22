using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IDepartmentRepository
{
    /// <summary>
    /// Obtém um departamento pelo ID.
    /// </summary>
    Task<Department?> GetByIdAsync(Guid id);
    
    /// <summary>
    /// Obtém todos os departamentos globais (ClientId = null).
    /// </summary>
    Task<List<Department>> GetGlobalAsync();
    
    /// <summary>
    /// Obtém departamentos de um cliente, opcionalmente incluindo globais herdáveis.
    /// </summary>
    Task<List<Department>> GetByClientAsync(Guid clientId, bool includeGlobal = true);
    
    /// <summary>
    /// Obtém departamentos de um cliente filtrado por atividade.
    /// </summary>
    Task<List<Department>> GetByClientAsync(Guid clientId, bool includeGlobal = true, bool activeOnly = true);
    
    /// <summary>
    /// Cria um novo departamento.
    /// </summary>
    Task<Department> CreateAsync(Department department);
    
    /// <summary>
    /// Atualiza um departamento existente.
    /// </summary>
    Task<Department> UpdateAsync(Department department);
    
    /// <summary>
    /// Deleta um departamento. Soft-delete recomendado (IsActive = false).
    /// </summary>
    Task<bool> DeleteAsync(Guid id);
    
    /// <summary>
    /// Verifica se existe um departamento com nome para um cliente.
    /// </summary>
    Task<bool> ExistsByNameAsync(Guid? clientId, string name);
}
