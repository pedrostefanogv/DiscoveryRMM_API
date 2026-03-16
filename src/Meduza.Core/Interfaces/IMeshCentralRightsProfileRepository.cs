using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IMeshCentralRightsProfileRepository
{
    Task<IReadOnlyList<MeshCentralRightsProfile>> GetAllAsync();
    Task<MeshCentralRightsProfile?> GetByIdAsync(Guid id);
    Task<MeshCentralRightsProfile?> GetByNameAsync(string name);
    Task<bool> IsNameInUseAsync(string name, Guid? excludeId = null);
    Task<bool> IsProfileReferencedByRolesAsync(string name);
    Task<MeshCentralRightsProfile> CreateAsync(MeshCentralRightsProfile profile);
    Task<MeshCentralRightsProfile> UpdateAsync(MeshCentralRightsProfile profile);
    Task<bool> DeleteAsync(Guid id);
}
