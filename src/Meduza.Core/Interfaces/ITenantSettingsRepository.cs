using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface ITenantSettingsRepository
{
    Task<TenantSettings?> GetByClientIdAsync(Guid clientId);
    Task<TenantSettings> UpsertAsync(TenantSettings settings);
}
