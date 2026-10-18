using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IDepartmentService
{
    Task<IReadOnlyList<Department>> GetByClientAsync(Guid clientId, bool includeGlobal = true, CancellationToken ct = default);
    Task<IReadOnlyList<Department>> GetGlobalAsync(CancellationToken ct = default);

    /// <summary>
    /// Todos os departamentos (globais + de todos os clientes), usados pela
    /// listagem do console com o filtro de escopo "todos" (D11). A implementação
    /// padrão degrada para a listagem de globais — o comportamento anterior —
    /// para não quebrar implementações de teste/legadas.
    /// </summary>
    Task<IReadOnlyList<Department>> GetAllAsync(CancellationToken ct = default)
        => GetGlobalAsync(ct);

    Task<Department?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Department> CreateAsync(Department department, CancellationToken ct = default);
    Task<Department> UpdateAsync(Department department, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Existe filho ativo herdando deste departamento? A implementação padrão
    /// devolve falso para não quebrar implementações de teste/legadas.
    /// </summary>
    Task<bool> HasActiveChildrenAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(false);
}
