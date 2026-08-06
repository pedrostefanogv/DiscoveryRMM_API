using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentRepository
{
    Task<Agent?> GetByIdAsync(Guid id);
    Task<IEnumerable<Agent>> GetAllAsync();
    Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId);
    Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId);
    Task<Agent> CreateAsync(Agent agent);
    Task UpdateAsync(Agent agent);
    Task UpdateStatusAsync(Guid id, Enums.AgentStatus status, string? ipAddress);
    Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default);
    Task ApproveZeroTouchAsync(Guid agentId);
    Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId);
    Task TransferSiteAsync(Guid agentId, Guid newSiteId);
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Busca agents (incluindo soft-deleted) que possuam o fingerprint informado e pertençam ao cliente.
    /// Usado pela Recuperação de Dispositivos para reutilizar um agent já registrado.
    /// </summary>
    Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default);
}
