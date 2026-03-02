using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IClientRepository
{
    Task<Client?> GetByIdAsync(Guid id);
    Task<IEnumerable<Client>> GetAllAsync(bool includeInactive = false);
    Task<Client> CreateAsync(Client client);
    Task UpdateAsync(Client client);
    Task DeleteAsync(Guid id);
}
