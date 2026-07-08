using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public sealed class DepartmentService : IDepartmentService
{
    private readonly IDepartmentRepository _repo;

    public DepartmentService(IDepartmentRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<Department>> GetByClientAsync(Guid clientId, bool includeGlobal = true, CancellationToken ct = default)
    {
        var deps = await _repo.GetByClientAsync(clientId, includeGlobal);
        return deps.AsReadOnly();
    }

    public async Task<IReadOnlyList<Department>> GetGlobalAsync(CancellationToken ct = default)
    {
        var deps = await _repo.GetGlobalAsync();
        return deps.AsReadOnly();
    }

    public Task<Department?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _repo.GetByIdAsync(id);

    public Task<Department> CreateAsync(Department department, CancellationToken ct = default)
        => _repo.CreateAsync(department);

    public Task<Department> UpdateAsync(Department department, CancellationToken ct = default)
        => _repo.UpdateAsync(department);

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => _repo.DeleteAsync(id);
}
