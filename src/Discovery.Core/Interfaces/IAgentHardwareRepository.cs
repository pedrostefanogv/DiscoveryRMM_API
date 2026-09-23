using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentHardwareRepository
{
    Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId);
    Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId);

    /// <summary>Busca hardware de varios agentes em uma unica query (evita N+1).</summary>
    Task<IReadOnlyDictionary<Guid, AgentHardwareInfo>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default);

    /// <summary>Busca os discos de varios agentes em uma unica query (evita N+1).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>> GetDisksByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default);
    Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null);
}
